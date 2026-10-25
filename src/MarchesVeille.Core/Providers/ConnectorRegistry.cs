using MarchesVeille.Core.Config;

namespace MarchesVeille.Core.Providers;

public sealed class ConnectorRegistry
{
    private readonly List<IMarketplaceProvider> _all = new();

    public ConnectorRegistry(ConnectorsConfig cfg)
    {
        if (cfg.Boamp.Enabled) _all.Add(new BoampApiProvider(cfg.Boamp, cfg));
        if (cfg.Place.Enabled) _all.Add(new PlaceScraperProvider(cfg.Place, cfg));
        if (cfg.GrandEst.Enabled) _all.Add(new GrandEstProvider(cfg.GrandEst, cfg));
        foreach (var c in cfg.CustomConnectors.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Id)))
            _all.Add(new HtmlFormProvider(c, cfg));
    }

    public IReadOnlyList<IMarketplaceProvider> All => _all;

    public IReadOnlyList<IMarketplaceProvider> Select(string? arg) => Filter(_all, arg);

    /// <summary>"all" ou liste d'identifiants séparés par des virgules. Lève ArgumentException si un identifiant est inconnu.</summary>
    public static IReadOnlyList<IMarketplaceProvider> Filter(IEnumerable<IMarketplaceProvider> all, string? arg)
    {
        var list = all.ToList();
        if (string.IsNullOrWhiteSpace(arg) || arg.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return list;

        var wanted = arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = wanted.Where(w => !list.Any(p => p.SourceId.Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"Source(s) inconnue(s) : {string.Join(", ", unknown)}. Disponibles : all, {string.Join(", ", list.Select(p => p.SourceId))}.");
        return list.Where(p => wanted.Contains(p.SourceId, StringComparer.OrdinalIgnoreCase)).ToList();
    }
}
