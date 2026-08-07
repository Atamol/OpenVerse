using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using OpenVerse.Decker.Data;

namespace OpenVerse.Decker.View;

public partial class DescUserControl : UserControl
{
    // "ffcd45" is the game's active-keyword gold - readable on the client's own dark UI, but too
    // low-contrast on this control's white background. Remapped to this resource-defined brush at
    // render time only; the underlying data (Id2Desc/Id2AdditionalDesc) keeps the original game
    // hex unchanged - see ResolveColor.
    private const string LowContrastKeywordHex = "ffcd45";

    public DescUserControl(TextLoader text, StatsLoader stats, int cardId)
    {
        InitializeComponent();
        RenderTitle(stats, cardId);
        Render(text, cardId);
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
        // this panel is interactive - hyperlink-tagged segments become clickable
        AppendSegments(DescText.Inlines, desc, makeClickable: true, () => ShowAdditional(text, cardId));
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

    // converts a raw game hex color to a display Brush - a UI-only concern, so the substitution
    // happens here rather than touching the data the color code came from (Id2Desc/
    // Id2AdditionalDesc keep the game's original "ffcd45"/"c8c8b0ff"/etc. untouched)
    private static Brush ResolveColor(string hex)
    {
        if (hex.Equals(LowContrastKeywordHex, StringComparison.OrdinalIgnoreCase) &&
            Application.Current.Resources["KeywordHighlightBrush"] is Brush brush)
        {
            return brush;
        }

        // the game's 8-digit hex is RRGGBBAA (alpha LAST, e.g. "c8c8b0ff") - WPF's ColorConverter
        // expects "#AARRGGBB" (alpha FIRST), so the alpha byte has to move to the front first or
        // the channels come out scrambled
        var wpfHex = hex.Length == 8 ? hex[6..] + hex[..6] : hex;

        return ColorConverter.ConvertFromString($"#{wpfHex}") is Color color
            ? new SolidColorBrush(color)
            : Brushes.Black;
    }
}
