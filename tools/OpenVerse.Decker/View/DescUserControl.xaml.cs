using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using OpenVerse.Decker.Data;

namespace OpenVerse.Decker.View;

public partial class DescUserControl : UserControl
{
    // from hyperlink bbcode color to readable color code for decker ui
    private const string LowContrastKeywordHex = "ffcd45";

    private readonly bool _interactive;

    /// <summary>
    /// this is used for non interactive description, which has no hyperlinks.
    /// </summary>
    /// <param name="interactive"></param>
    public DescUserControl(bool interactive)
    {
        InitializeComponent();
        _interactive = interactive;
    }

    public DescUserControl(TextLoader text, StatsLoader stats, int cardId) : this(interactive: true) =>
        ShowCard(text, stats, cardId);

    /// <summary>Re-points the panel at another card, so one instance can serve every hovered tile.</summary>
    public void ShowCard(TextLoader text, StatsLoader stats, int cardId)
    {
        RenderTitle(stats, cardId);
        Render(text, cardId);
        AdditionalBorder.Visibility = Visibility.Collapsed;
    }

    private void RenderTitle(StatsLoader stats, int cardId)
    {
        var s = stats.Id2UnevolvedStats.GetValueOrDefault(cardId);
        TypeText.Text = s?.CardType.Abbreviation() ?? "?";
        CostText.Text = s?.Cost.ToString() ?? "";

        var showStats = s is { Power: not -1 };
        PowerText.Visibility = SlashText.Visibility = LifeText.Visibility =
            showStats ? Visibility.Visible : Visibility.Collapsed;
        if (showStats)
        {
            PowerText.Text = s!.Power.ToString();
            LifeText.Text = s.Life.ToString();
        }
    }

    private void Render(TextLoader text, int cardId)
    {
        NameText.Text = CardTextMarkup.StripNotation(text.Id2Name.GetValueOrDefault(cardId, ""));

        DescText.Inlines.Clear();
        var desc = text.Id2Desc.GetValueOrDefault(cardId, "");
        // hyperlink-tagged segments become clickable only where a references panel makes sense
        AppendSegments(DescText.Inlines, desc, _interactive, () => ShowAdditional(text, cardId));
    }

    private void ShowAdditional(TextLoader text, int cardId)
    {
        var additional = text.Id2AdditionalDesc.GetValueOrDefault(cardId, "");
        if (string.IsNullOrEmpty(additional))
        {
            return;
        }

        AdditionalText.Inlines.Clear();
        // this panel is non-interactive per spec (no need for another level of clickable links) -
        // still shows color/bold, just never attaches a click handler
        AppendSegments(AdditionalText.Inlines, additional, makeClickable: false, onHyperlinkClick: null);

        AdditionalBorder.Visibility = Visibility.Visible;
    }

    private static void AppendSegments(InlineCollection inlines, string rawText, bool makeClickable, Action? onHyperlinkClick)
    {
        foreach (var segment in CardTextMarkup.Segmentize(rawText))
        {
            Inline inline;
            if (makeClickable && segment.IsHyperlink)
            {
                var link = new Hyperlink(new Run(segment.Text));
                link.Click += (_, _) => onHyperlinkClick?.Invoke();
                inline = link;
            }
            else
            {
                inline = new Run(segment.Text);
            }

            if (segment.IsBold)
            {
                inline.FontWeight = FontWeights.Bold;
            }
            if (segment.ColorHex is { } hex)
            {
                inline.Foreground = ResolveColor(hex);
            }
            inlines.Add(inline);
        }
    }

    // converts a raw game hex color to a display Brush
    private static Brush ResolveColor(string hex)
    {
        if (hex.Equals(LowContrastKeywordHex, StringComparison.OrdinalIgnoreCase) &&
            Application.Current.Resources["KeywordHighlightBrush"] is Brush brush)
        {
            return brush;
        }

        // convert RRGGBBAA in game to AARRGGBB in WPF
        var wpfHex = hex.Length == 8 ? hex[6..] + hex[..6] : hex;

        return ColorConverter.ConvertFromString($"#{wpfHex}") is Color color
            ? new SolidColorBrush(color)
            : Brushes.Black;
    }
}
