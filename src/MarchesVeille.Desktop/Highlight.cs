using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MarchesVeille.Desktop;

/// <summary>Propriété attachée : affiche un texte contenant [[termes]] avec les termes en gras surlignés.</summary>
public static class Highlight
{
    public static readonly DependencyProperty MarkupProperty =
        DependencyProperty.RegisterAttached("Markup", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static string? GetMarkup(DependencyObject o) => (string?)o.GetValue(MarkupProperty);
    public static void SetMarkup(DependencyObject o, string? v) => o.SetValue(MarkupProperty, v);

    private static readonly Brush Mark = new SolidColorBrush(Color.FromRgb(255, 236, 153));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var s = e.NewValue as string ?? "";
        int i = 0;
        while (i < s.Length)
        {
            int a = s.IndexOf("[[", i, StringComparison.Ordinal);
            if (a < 0) { tb.Inlines.Add(new Run(s[i..])); break; }
            if (a > i) tb.Inlines.Add(new Run(s[i..a]));
            int b = s.IndexOf("]]", a + 2, StringComparison.Ordinal);
            if (b < 0) { tb.Inlines.Add(new Run(s[a..])); break; }
            tb.Inlines.Add(new Run(s[(a + 2)..b]) { FontWeight = FontWeights.Bold, Background = Mark });
            i = b + 2;
        }
    }
}
