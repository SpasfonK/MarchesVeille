using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Core.Providers;

/// <summary>API publique BOAMP (Opendatasoft, API Explore v2.1) : une requête paginée par jour de parution.</summary>
public sealed class BoampApiProvider : IMarketplaceProvider
{
    private static readonly Regex Siret = new(@"\b\d{14}\b", RegexOptions.Compiled);

    private readonly BoampConfig _cfg;
    private readonly ConnectorsConfig _global;
    private readonly HttpClient _http;
    private readonly List<Regex> _keepBuyer;
    private readonly HashSet<string> _keepDepts;

    public string SourceId => "boamp";
    public string Name => _cfg.Name;
    public string Type => "API";
    public string? BaseUrl => _cfg.BaseUrl;

    public BoampApiProvider(BoampConfig cfg, ConnectorsConfig global, HttpClient? http = null)
    {
        _cfg = cfg;
        _global = global;
        _http = http ?? HttpFactory.Create(global.UserAgent);
        _keepBuyer = cfg.KeepBuyerPatterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        _keepDepts = cfg.KeepDepartments.Select(d => d.Trim()).ToHashSet();
    }

    public async Task<IReadOnlyList<Notice>> FetchAsync(ScrapingOptions options, CancellationToken ct)
    {
        var results = new List<Notice>();
        var today = (options.Today ?? DateTime.UtcNow).Date;

        for (int d = 0; d < Math.Max(1, options.Days); d++)
        {
            var day = today.AddDays(-d);
            int offset = 0;
            int dayCount = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var url = BuildUrl(day, offset);
                var (json, _) = await HttpRetry.SendForStringAsync(
                    _http, () => new HttpRequestMessage(HttpMethod.Get, url), _global.MaxRetries, _global.RequestDelayMs, ct);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                    break;

                foreach (var rec in arr.EnumerateArray())
                {
                    var n = Map(rec);
                    if (n != null && Keep(n)) { results.Add(n); dayCount++; }
                }

                int total = root.TryGetProperty("total_count", out var tc) && tc.TryGetInt32(out var t) ? t : 0;
                offset += _cfg.PageSize;
                // Opendatasoft : offset + limit <= 10000
                if (offset >= total || offset + _cfg.PageSize > 10_000) break;
                await Task.Delay(_global.RequestDelayMs, ct);
            }
            options.Progress?.Report($"{day:yyyy-MM-dd} : {dayCount} avis retenus");
            await Task.Delay(_global.RequestDelayMs, ct);
        }
        return results;
    }

    private string BuildUrl(DateTime day, int offset)
    {
        var where = $"dateparution = date'{day:yyyy-MM-dd}'";
        if (!string.IsNullOrWhiteSpace(_cfg.ExtraWhere)) where += $" AND ({_cfg.ExtraWhere})";
        return $"{_cfg.BaseUrl.TrimEnd('/')}/api/explore/v2.1/catalog/datasets/{_cfg.Dataset}/records" +
               $"?limit={_cfg.PageSize}&offset={offset}&order_by={Uri.EscapeDataString("dateparution desc")}" +
               $"&where={Uri.EscapeDataString(where)}";
    }

    private bool Keep(Notice n)
    {
        if (_keepDepts.Count == 0 && _keepBuyer.Count == 0) return true;
        if (n.Department != null && _keepDepts.Contains(n.Department)) return true;
        var buyer = TextNormalizer.Normalize(n.BuyerName);
        return _keepBuyer.Any(rx => rx.IsMatch(buyer));
    }

    private Notice? Map(JsonElement rec)
    {
        var reference = JsonUtil.GetString(rec, "idweb", "id");
        if (string.IsNullOrWhiteSpace(reference)) return null;

        var objet = JsonUtil.GetString(rec, "objet", "titre_marche", "titre") ?? "";
        var dept = JsonUtil.GetString(rec, "code_departement")?.Split(',')[0].Trim();

        var extra = new StringBuilder();
        string? rawDonnees = null;
        if (rec.TryGetProperty("donnees", out var dn))
        {
            if (dn.ValueKind == JsonValueKind.String)
            {
                rawDonnees = dn.GetString();
                try
                {
                    using var inner = JsonDocument.Parse(rawDonnees ?? "");
                    JsonUtil.CollectStrings(inner.RootElement, extra, 6000);
                }
                catch (JsonException) { extra.Append(rawDonnees); }
            }
            else if (dn.ValueKind == JsonValueKind.Object)
            {
                rawDonnees = dn.GetRawText();
                JsonUtil.CollectStrings(dn, extra, 6000);
            }
        }
        var descripteurs = JsonUtil.GetString(rec, "descripteur_libelle");
        var description = string.Join("\n", new[] { objet, descripteurs, extra.ToString().Trim() }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (description.Length > 8000) description = description[..8000];

        var siret = rawDonnees != null ? Siret.Match(rawDonnees) : Match.Empty;

        return new Notice
        {
            SourceId = SourceId,
            Reference = reference,
            Title = objet.Length > 0 ? objet : reference,
            Description = description,
            BuyerName = JsonUtil.GetString(rec, "nomacheteur", "nom_acheteur") ?? "",
            BuyerSiret = siret.Success ? siret.Value : null,
            Department = dept,
            Region = Geo.RegionOf(dept),
            PublicationDate = DateParsing.ParseIso(JsonUtil.GetString(rec, "dateparution")),
            DeadlineDate = DateParsing.ParseIso(JsonUtil.GetString(rec, "datelimitereponse")),
            UrlNotice = JsonUtil.GetString(rec, "url_avis") ?? $"https://www.boamp.fr/pages/avis/?q=idweb:{reference}"
        };
    }
}
