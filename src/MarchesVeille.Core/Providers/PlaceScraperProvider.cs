using MarchesVeille.Core.Config;

namespace MarchesVeille.Core.Providers;

/// <summary>PLACE (marches-publics.gouv.fr) : formulaire Prado + tableau de résultats, piloté par la section « place » de connectors.json.</summary>
public sealed class PlaceScraperProvider : HtmlFormProvider
{
    public PlaceScraperProvider(HtmlConnectorConfig cfg, ConnectorsConfig global, HttpClient? http = null)
        : base(cfg, global, http)
    {
        if (string.IsNullOrWhiteSpace(cfg.Id)) cfg.Id = "place";
    }
}
