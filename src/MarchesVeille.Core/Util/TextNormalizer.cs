using System.Globalization;
using System.Text;

namespace MarchesVeille.Core.Util;

public static class TextNormalizer
{
    /// <summary>Minuscules, sans accents, tirets typographiques ramenés à '-'.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant()
            .Replace("œ", "oe").Replace("æ", "ae").Replace('\u2019', '\'')
            .Replace('\u2010', '-').Replace('\u2011', '-').Replace('\u2013', '-').Replace('\u2014', '-');
    }
}
