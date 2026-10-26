using System.Text.RegularExpressions;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Core.Ingestion;

/// <summary>
/// Détermine le type de marché (Travaux / Fournitures / Services) et le caractère « prestation intellectuelle » (PI)
/// à partir de motifs configurables (connectors.json, section « classification »), sauf si la source l'a déjà renseigné.
/// </summary>
public sealed class NoticeClassifier
{
    private readonly List<Regex> _travaux, _fournitures, _services, _pi;

    public NoticeClassifier(ClassificationConfig cfg)
    {
        static List<Regex> Compile(IEnumerable<string> p) =>
            p.Select(x => new Regex(x, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        _travaux = Compile(cfg.Travaux);
        _fournitures = Compile(cfg.Fournitures);
        _services = Compile(cfg.Services);
        _pi = Compile(cfg.Pi);
    }

    public void Apply(Notice n)
    {
        var desc = n.Description.Length > 800 ? n.Description[..800] : n.Description;
        var title = TextNormalizer.Normalize(n.Title);
        var text = TextNormalizer.Normalize($"{n.Title} {desc}");

        if (string.IsNullOrWhiteSpace(n.MarketType))
        {
            // le titre prime sur la description
            n.MarketType = Detect(title) ?? Detect(text);
        }
        if (!n.IsPi) n.IsPi = _pi.Any(rx => rx.IsMatch(title)) || _pi.Any(rx => rx.IsMatch(text));
        // une prestation intellectuelle est un service
        if (n.MarketType == null && n.IsPi) n.MarketType = "Services";
    }

    private string? Detect(string text)
    {
        if (_travaux.Any(rx => rx.IsMatch(text))) return "Travaux";
        if (_fournitures.Any(rx => rx.IsMatch(text))) return "Fournitures";
        if (_services.Any(rx => rx.IsMatch(text))) return "Services";
        return null;
    }

    /// <summary>Normalise un libellé source (« TRAVAUX », « Fournitures »…) ; null si inconnu.</summary>
    public static string? NormalizeMarketType(string? raw)
    {
        var t = TextNormalizer.Normalize(raw);
        if (t.Contains("travaux")) return "Travaux";
        if (t.Contains("fourniture")) return "Fournitures";
        if (t.Contains("service")) return "Services";
        return null;
    }
}
