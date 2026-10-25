using MarchesVeille.Core.Config;

namespace MarchesVeille.Core.Providers;

/// <summary>Profil acheteur Région Grand Est (marchespublics.grandest.fr), piloté par la section « grandEst » de connectors.json.</summary>
public sealed class GrandEstProvider : HtmlFormProvider
{
    public GrandEstProvider(HtmlConnectorConfig cfg, ConnectorsConfig global, HttpClient? http = null)
        : base(cfg, global, http)
    {
        if (string.IsNullOrWhiteSpace(cfg.Id)) cfg.Id = "grandest";
        if (string.IsNullOrWhiteSpace(cfg.DefaultRegion)) cfg.DefaultRegion = "Grand Est";
    }
}
