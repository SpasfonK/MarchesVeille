using System.Text.RegularExpressions;

namespace MarchesVeille.Core.Data;

/// <summary>
/// Transforme la saisie utilisateur en requête FTS5 valide.
/// Syntaxe : mots (préfixe automatique pendant la frappe), "phrase exacte", AND / OR / NOT.
/// </summary>
public static class FtsQueryBuilder
{
    private static readonly Regex Tokens = new("\"[^\"]*\"?|\\S+", RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    public static string? Build(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parts = new List<string>();
        foreach (Match m in Tokens.Matches(input))
        {
            var t = m.Value;
            if (t.StartsWith('"'))
            {
                var inner = NonAlnum.Replace(t, " ").Trim();
                if (inner.Length > 0) parts.Add($"\"{inner}\"");
                continue;
            }
            if (t is "AND" or "OR" or "NOT")
            {
                if (t == "NOT" && parts.Count > 0 && parts[^1] == "AND") parts.RemoveAt(parts.Count - 1); // "AND NOT" -> "NOT"
                parts.Add(t);
                continue;
            }
            var clean = NonAlnum.Replace(t, " ").Trim();
            if (clean.Length > 0) parts.Add($"\"{clean}\"*");
        }
        // pas d'opérateur en tête ni en queue, pas d'opérateurs consécutifs
        while (parts.Count > 0 && IsOp(parts[0])) parts.RemoveAt(0);
        while (parts.Count > 0 && IsOp(parts[^1])) parts.RemoveAt(parts.Count - 1);
        for (int i = parts.Count - 1; i > 0; i--)
            if (IsOp(parts[i]) && IsOp(parts[i - 1])) parts.RemoveAt(i);
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>Repli : tous les mots en ET implicite, sans opérateurs ni guillemets.</summary>
    public static string? BuildSafe(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var words = NonAlnum.Replace(input, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("AND" or "OR" or "NOT"))
            .Select(w => $"\"{w}\"*").ToList();
        return words.Count == 0 ? null : string.Join(' ', words);
    }

    private static bool IsOp(string s) => s is "AND" or "OR" or "NOT";
}
