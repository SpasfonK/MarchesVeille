using System.Text.RegularExpressions;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Core.Providers;

/// <summary>
/// Qualification automatique des avis (ex. buyer_category = "VNF-DTNE") à partir des règles de connectors.json.
/// La première règle qui correspond l'emporte.
/// </summary>
public sealed class VnfTargetProfile
{
    private sealed record CompiledRule(string Category, List<Regex> Buyer, List<Regex> Text, HashSet<string> Sirets);

    private readonly List<CompiledRule> _rules;

    public VnfTargetProfile(IEnumerable<TaggingRule> rules)
    {
        static Regex Rx(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        _rules = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Category))
            .Select(r => new CompiledRule(
                r.Category,
                r.BuyerPatterns.Select(Rx).ToList(),
                r.TextPatterns.Select(Rx).ToList(),
                r.Sirets.Select(Digits).Where(s => s.Length > 0).ToHashSet()))
            .ToList();
    }

    public string? Qualify(Notice n)
    {
        var buyer = TextNormalizer.Normalize(n.BuyerName);
        var all = TextNormalizer.Normalize($"{n.BuyerName} {n.Title} {n.Description}");
        var siret = Digits(n.BuyerSiret);

        foreach (var r in _rules)
        {
            bool buyerMatch;
            if (r.Buyer.Count == 0 && r.Sirets.Count == 0) buyerMatch = true;
            else buyerMatch = r.Buyer.Any(rx => rx.IsMatch(buyer)) || (siret.Length > 0 && r.Sirets.Contains(siret));
            if (!buyerMatch) continue;
            if (r.Text.Count == 0 || r.Text.Any(rx => rx.IsMatch(all))) return r.Category;
        }
        return null;
    }

    /// <summary>Pose BuyerCategory si une règle correspond (sinon laisse la valeur existante).</summary>
    public void Apply(Notice n)
    {
        var cat = Qualify(n);
        if (cat != null) n.BuyerCategory = cat;
    }

    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
}
