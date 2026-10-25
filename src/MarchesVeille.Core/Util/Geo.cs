using System.Text.RegularExpressions;

namespace MarchesVeille.Core.Util;

public static class Geo
{
    private static readonly HashSet<string> GrandEstDepartments = new() { "08", "10", "51", "52", "54", "55", "57", "67", "68", "88" };
    private static readonly Regex Postal = new(@"\b(\d{2})\d{3}\b", RegexOptions.Compiled);
    private static readonly Regex Dept = new(@"(?<![\d])(2A|2B|\d{2,3})(?![\d])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? RegionOf(string? department) =>
        department != null && GrandEstDepartments.Contains(department.Trim()) ? "Grand Est" : null;

    public static string? ExtractDepartment(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = Postal.Match(s);
        if (p.Success) return p.Groups[1].Value;
        var m = Dept.Match(s);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }
}
