using System.Globalization;
using System.Text.RegularExpressions;

namespace MarchesVeille.Core.Util;

public static class DateParsing
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly TimeZoneInfo Paris = FindParis();
    private static readonly Regex FrDate = new(@"(\d{2})/(\d{2})/(\d{4})(?:\D+(\d{1,2})[:hH](\d{2}))?", RegexOptions.Compiled);

    private static TimeZoneInfo FindParis()
    {
        foreach (var id in new[] { "Europe/Paris", "Romance Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* essai suivant */ }
        }
        return TimeZoneInfo.Utc;
    }

    private static DateTime ParisToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        try { return TimeZoneInfo.ConvertTimeToUtc(unspecified, Paris); }
        catch (ArgumentException) { return DateTime.SpecifyKind(local, DateTimeKind.Utc); }
    }

    /// <summary>Date affichée sur un portail français (heure de Paris) vers UTC.</summary>
    public static DateTime? ParseFrench(string? s, IEnumerable<string>? formats = null)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = Regex.Replace(s.Trim(), @"\s+", " ");
        if (formats != null)
            foreach (var f in formats)
                if (DateTime.TryParseExact(s, f, Fr, DateTimeStyles.None, out var d)) return ParisToUtc(d);
        var m = FrDate.Match(s);
        if (m.Success)
        {
            try
            {
                var local = new DateTime(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value),
                    m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0,
                    m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0, 0);
                return ParisToUtc(local);
            }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return ParseIso(s);
    }

    /// <summary>ISO 8601 (avec ou sans fuseau, sans fuseau = UTC) vers UTC.</summary>
    public static DateTime? ParseIso(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.UtcDateTime
            : null;
    }
}
