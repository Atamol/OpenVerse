using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace OpenVerse.Decker.Data;

/// <summary>
/// Shows a card's art, standing in with a reload glyph until the bitmap arrives. The loader hands
/// one of these back so callers never have to track whether a request is still in flight.
/// </summary>
public sealed class CardArtworkView : UserControl
{
    private const string ReloadGlyph =
        "M2 12C2 16.97 6.03 21 11 21C13.39 21 15.68 20.06 17.4 18.4L15.9 16.9C14.63 18.25 12.86 19 11 " +
        "19C4.76 19 1.64 11.46 6.05 7.05C10.46 2.64 18 5.77 18 12H15L19 16H19.1L23 12H20C20 7.03 15.97 " +
        "3 11 3C6.03 3 2 7.03 2 12Z";

    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly Viewbox _placeholder;

    /// <summary>Which card this view currently stands for; a recycled tile changes it mid-flight.</summary>
    public int CardId { get; internal set; }

    public CardArtworkView(int cardId, Brush fallbackFill)
    {
        CardId = cardId;
        _placeholder = new Viewbox
        {
            Width = 20,
            Height = 20,
            Child = new ShapePath
            {
                Data = Geometry.Parse(ReloadGlyph),
                Fill = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x72)),
            },
        };

        Content = new Grid
        {
            Background = fallbackFill,
            Children = { _placeholder, _image },
        };
    }

    internal void Show(BitmapSource bitmap)
    {
        _image.Source = bitmap;
        _placeholder.Visibility = Visibility.Collapsed;
    }

    /// <summary>Back to the glyph, so a recycled tile never shows the previous card's art.</summary>
    internal void ResetToPlaceholder()
    {
        _image.Source = null;
        _placeholder.Visibility = Visibility.Visible;
    }

    /// <summary>No bundle for this card, so settle on the rarity colour instead of spinning forever.</summary>
    internal void ShowFallbackOnly()
    {
        _image.Source = null;
        _placeholder.Visibility = Visibility.Collapsed;
    }
}

/// <summary>
/// Decodes card art off the UI thread, one card at a time. Opening a bundle costs ~190ms, so a
/// pool would only thrash the disk, and newest-first ordering means a fast scroll serves what is
/// actually on screen rather than working through everything it flew past.
/// </summary>
public sealed class CardArtworkLoader : IDisposable
{
    /// <summary>
    /// Cache at roughly tile size, not the 1024x1024 the bundle ships: full size would be 4MB a
    /// card, so a full cache would cost about a gigabyte.
    /// </summary>
    private const int CachedArtSize = 160;
    private const int CacheLimit = 240;

    private readonly string _bundleDirectory;
    private readonly Stack<CardArtworkView> _pending = new();
    private readonly Dictionary<int, BitmapSource> _cache = [];
    private readonly Queue<int> _cacheOrder = new();
    private readonly object _gate = new();
    private readonly Thread _worker;
    private bool _disposed;

    public CardArtworkLoader(string bundleDirectory)
    {
        _bundleDirectory = bundleDirectory;
        _worker = new Thread(Work) { IsBackground = true, Name = "card-artwork" };
        _worker.Start();
    }

    public bool IsAvailable => Directory.Exists(_bundleDirectory);

    /// <summary>Returns a view immediately; the bitmap fills in later if the bundle can be read.</summary>
    public CardArtworkView CreateView(int cardId, Brush fallbackFill)
    {
        var view = new CardArtworkView(cardId, fallbackFill);
        Request(view);
        return view;
    }

    /// <summary>Re-points an existing view at another card, for a recycled tile.</summary>
    public void Rebind(CardArtworkView view, int cardId)
    {
        view.CardId = cardId;
        view.ResetToPlaceholder();
        Request(view);
    }

    private void Request(CardArtworkView view)
    {
        if (!IsAvailable)
        {
            return;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(view.CardId, out var cached))
            {
                var wanted = view.CardId;
                view.Dispatcher.BeginInvoke(() =>
                {
                    if (view.CardId == wanted)
                    {
                        view.Show(cached);
                    }
                });
                return;
            }
            _pending.Push(view);
            Monitor.Pulse(_gate);
        }
    }

    private void Work()
    {
        while (true)
        {
            CardArtworkView view;
            lock (_gate)
            {
                while (_pending.Count == 0 && !_disposed)
                {
                    Monitor.Wait(_gate);
                }
                if (_disposed)
                {
                    return;
                }
                view = _pending.Pop();
            }

            var cardId = view.CardId;
            BitmapSource? bitmap;
            lock (_gate)
            {
                _cache.TryGetValue(cardId, out bitmap);
            }
            bitmap ??= Decode(cardId);
            if (bitmap is null)
            {
                view.Dispatcher.BeginInvoke(() =>
                {
                    if (view.CardId == cardId)
                    {
                        view.ShowFallbackOnly();
                    }
                });
                continue;
            }

            lock (_gate)
            {
                if (_cache.TryAdd(cardId, bitmap))
                {
                    _cacheOrder.Enqueue(cardId);
                    while (_cacheOrder.Count > CacheLimit)
                    {
                        _cache.Remove(_cacheOrder.Dequeue());
                    }
                }
            }

            var ready = bitmap;
            view.Dispatcher.BeginInvoke(() =>
            {
                // the tile may have been recycled onto another card while this was decoding
                if (view.CardId == cardId)
                {
                    view.Show(ready);
                }
            });
        }
    }

    private BitmapSource? Decode(int cardId)
    {
        var path = Path.Combine(_bundleDirectory, $"card_{cardId}0.unity3d");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manager = new AssetsManager();
            var bundle = manager.LoadBundleFile(path, true);
            var assets = manager.LoadAssetsFileFromBundle(bundle, 0, false);

            // one bundle holds the art plus effect and battle-log variants; the art is the one
            // named after the bundle itself, and the others decode into unrelated colour data
            var artName = $"{cardId}0";
            foreach (var info in assets.file.GetAssetsOfType(AssetClassID.Texture2D))
            {
                var texture = TextureFile.ReadTextureFile(manager.GetBaseField(assets, info));
                if (texture.m_Name != artName)
                {
                    continue;
                }

                var raw = ReadPixels(bundle, texture);
                if (raw is null)
                {
                    continue;
                }

                var bgra = TextureFile.DecodeManagedData(
                    raw, (TextureFormat)texture.m_TextureFormat, texture.m_Width, texture.m_Height, true, null);
                if (bgra is null)
                {
                    continue;
                }

                // Unity textures start at the bottom row, so the stride is walked backwards
                var full = BitmapSource.Create(
                    texture.m_Width, texture.m_Height, 96, 96, PixelFormats.Bgra32, null,
                    FlipVertically(bgra, texture.m_Width, texture.m_Height), texture.m_Width * 4);

                var shrunk = Downscale(full, CachedArtSize);
                shrunk.Freeze();
                return shrunk;
            }
        }
        catch (Exception)
        {
            // a corrupt or half-downloaded bundle just leaves the placeholder showing
        }
        return null;
    }

    private static byte[]? ReadPixels(BundleFileInstance bundle, TextureFile texture)
    {
        if (texture.pictureData is { Length: > 0 })
        {
            return texture.pictureData;
        }

        var streamPath = texture.m_StreamData.path;
        if (string.IsNullOrEmpty(streamPath))
        {
            return null;
        }

        // the pixels live in a .resS entry inside this same bundle, not in the asset itself
        var entry = streamPath[(streamPath.LastIndexOf('/') + 1)..];
        var resS = BundleHelper.LoadAssetDataFromBundle(bundle.file, entry);
        var slice = new byte[texture.m_StreamData.size];
        Array.Copy(resS, (long)texture.m_StreamData.offset, slice, 0, slice.Length);
        return slice;
    }

    /// <summary>Copies the scaled pixels out so the full-size source can be collected.</summary>
    private static BitmapSource Downscale(BitmapSource source, int maxSide)
    {
        if (source.PixelWidth <= maxSide && source.PixelHeight <= maxSide)
        {
            return source;
        }

        var scale = maxSide / (double)Math.Max(source.PixelWidth, source.PixelHeight);
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var stride = scaled.PixelWidth * 4;
        var pixels = new byte[stride * scaled.PixelHeight];
        scaled.CopyPixels(pixels, stride, 0);
        return BitmapSource.Create(
            scaled.PixelWidth, scaled.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    private static byte[] FlipVertically(byte[] bgra, int width, int height)
    {
        var stride = width * 4;
        var flipped = new byte[bgra.Length];
        for (var row = 0; row < height; row++)
        {
            Array.Copy(bgra, row * stride, flipped, (height - 1 - row) * stride, stride);
        }
        return flipped;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
    }
}
