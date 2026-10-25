using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Core.Providers;

/// <summary>
/// Connecteur HTML générique piloté par connectors.json : ouvre le formulaire de recherche, le soumet
/// (champs cachés Prado/ASP.NET repris automatiquement, cookies de session conservés), lit le tableau de
/// résultats via des sélecteurs CSS et suit la pagination (lien ou postback).
/// </summary>
public class HtmlFormProvider : IMarketplaceProvider
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    protected readonly HtmlConnectorConfig Cfg;
    protected readonly ConnectorsConfig Global;
    private readonly HttpClient _http;
    private readonly List<Regex> _keepBuyer;

    public string SourceId => Cfg.Id;
    public string Name => Cfg.Name;
    public string Type => "HTML_SCRAPER";
    public string? BaseUrl => Cfg.BaseUrl;

    public HtmlFormProvider(HtmlConnectorConfig cfg, ConnectorsConfig global, HttpClient? http = null)
    {
        Cfg = cfg;
        Global = global;
        _http = http ?? HttpFactory.Create(global.UserAgent);
        _keepBuyer = cfg.KeepBuyerPatterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
    }

    public async Task<IReadOnlyList<Notice>> FetchAsync(ScrapingOptions options, CancellationToken ct)
    {
        var results = new List<Notice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parser = new HtmlParser();

        var end = (options.Today ?? DateTime.UtcNow).Date;
        var start = end.AddDays(-(Math.Max(1, options.Days) - 1));
        var tokens = new Dictionary<string, string>
        {
            ["{dateStart}"] = start.ToString(Cfg.DateFormat, Inv),
            ["{dateEnd}"] = end.ToString(Cfg.DateFormat, Inv),
            ["{isoStart}"] = start.ToString("yyyy-MM-dd", Inv),
            ["{isoEnd}"] = end.ToString("yyyy-MM-dd", Inv)
        };

        var searchUrl = Resolve(new Uri(Cfg.BaseUrl, UriKind.Absolute), Cfg.SearchUrl);
        options.Progress?.Report($"Ouverture du formulaire {searchUrl}");
        var (html, pageUri) = await GetAsync(searchUrl, ct);

        var doc = parser.ParseDocument(html);
        var form = doc.QuerySelector(Cfg.FormSelector) as IHtmlFormElement
                   ?? throw new InvalidOperationException($"Formulaire introuvable ({Cfg.FormSelector}) sur {pageUri}");
        var fields = CollectFields(form);
        ApplyConfiguredFields(fields, tokens);
        ApplySubmit(fields, Cfg.SubmitName);

        await Task.Delay(Global.RequestDelayMs, ct);
        (html, pageUri) = await PostAsync(Resolve(pageUri, form.GetAttribute("action")), fields, ct);

        int maxPages = Math.Min(Math.Max(1, options.MaxPages), Math.Max(1, Cfg.MaxPages));
        for (int page = 1; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            doc = parser.ParseDocument(html);
            int newCount = 0;
            foreach (var row in doc.QuerySelectorAll(Cfg.RowSelector))
            {
                var n = ParseRow(row, pageUri);
                if (n == null) continue;
                n.EnsureId();
                if (seen.Add(n.Id)) { results.Add(n); newCount++; }
            }
            options.Progress?.Report($"Page {page} : {newCount} nouveaux avis ({results.Count} au total)");
            if (newCount == 0) break;

            var next = string.IsNullOrWhiteSpace(Cfg.Paging.NextSelector) ? null : doc.QuerySelector(Cfg.Paging.NextSelector);
            bool hasNext = false;
            switch ((Cfg.Paging.Mode ?? "none").ToLowerInvariant())
            {
                case "link":
                    {
                        var href = next?.GetAttribute("href");
                        if (!string.IsNullOrWhiteSpace(href) && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                        {
                            await Task.Delay(Global.RequestDelayMs, ct);
                            (html, pageUri) = await GetAsync(Resolve(pageUri, href), ct);
                            hasNext = true;
                        }
                        break;
                    }
                case "postback":
                    {
                        if (next != null && doc.QuerySelector(Cfg.FormSelector) is IHtmlFormElement f2)
                        {
                            var flds = CollectFields(f2);
                            ApplyConfiguredFields(flds, tokens);
                            if (!string.IsNullOrWhiteSpace(Cfg.PostbackTargetField) && !string.IsNullOrWhiteSpace(Cfg.Paging.PostbackTarget))
                                flds[Cfg.PostbackTargetField] = Cfg.Paging.PostbackTarget;
                            await Task.Delay(Global.RequestDelayMs, ct);
                            (html, pageUri) = await PostAsync(Resolve(pageUri, f2.GetAttribute("action")), flds, ct);
                            hasNext = true;
                        }
                        break;
                    }
            }
            if (!hasNext) break;
        }
        return results;
    }

    // ----------------------------------------------------------------- HTTP
    private Task<(string Body, Uri FinalUri)> GetAsync(Uri url, CancellationToken ct) =>
        HttpRetry.SendForStringAsync(_http, () => new HttpRequestMessage(HttpMethod.Get, url), Global.MaxRetries, Global.RequestDelayMs, ct);

    private Task<(string Body, Uri FinalUri)> PostAsync(Uri url, Dictionary<string, string> fields, CancellationToken ct) =>
        HttpRetry.SendForStringAsync(_http, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields)
        }, Global.MaxRetries, Global.RequestDelayMs, ct);

    private static Uri Resolve(Uri baseUri, string? href) =>
        string.IsNullOrWhiteSpace(href) ? baseUri : new Uri(baseUri, href);

    // ----------------------------------------------------------------- formulaire
    private static Dictionary<string, string> CollectFields(IHtmlFormElement form)
    {
        var fields = new Dictionary<string, string>();
        foreach (var el in form.QuerySelectorAll("input,select,textarea"))
        {
            var name = el.GetAttribute("name");
            if (string.IsNullOrEmpty(name)) continue;
            switch (el)
            {
                case IHtmlInputElement inp:
                    var type = (inp.Type ?? "text").ToLowerInvariant();
                    if (type is "submit" or "button" or "image" or "reset" or "file") continue;
                    if (type is "checkbox" or "radio" && !inp.IsChecked) continue;
                    fields[name] = inp.Value ?? "";
                    break;
                case IHtmlSelectElement sel:
                    fields[name] = sel.SelectedOptions.FirstOrDefault()?.Value ?? sel.Options.FirstOrDefault()?.Value ?? "";
                    break;
                case IHtmlTextAreaElement ta:
                    fields[name] = ta.Value ?? "";
                    break;
            }
        }
        return fields;
    }

    private void ApplyConfiguredFields(Dictionary<string, string> fields, Dictionary<string, string> tokens)
    {
        foreach (var (name, raw) in Cfg.FormFields)
        {
            var v = raw;
            foreach (var (token, value) in tokens) v = v.Replace(token, value, StringComparison.Ordinal);
            fields[name] = v;
        }
    }

    private void ApplySubmit(Dictionary<string, string> fields, string? submitName)
    {
        if (string.IsNullOrWhiteSpace(submitName)) return;
        if (!string.IsNullOrWhiteSpace(Cfg.PostbackTargetField)) fields[Cfg.PostbackTargetField] = submitName;
        else fields[submitName] = "1";
    }

    // ----------------------------------------------------------------- lignes
    private Notice? ParseRow(IElement row, Uri pageUri)
    {
        string? F(string key) => Cfg.Fields.TryGetValue(key, out var spec) ? Extract(row, spec) : null;

        var title = F("title");
        var reference = F("reference");
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(reference)) return null;

        var buyer = F("buyer") ?? "";
        if (_keepBuyer.Count > 0)
        {
            var nb = TextNormalizer.Normalize(buyer);
            if (!_keepBuyer.Any(rx => rx.IsMatch(nb))) return null;
        }

        var deadlineRaw = F("deadline");
        if (string.IsNullOrWhiteSpace(reference))
            reference = Notice.ComputeId(SourceId, $"{title}|{buyer}|{deadlineRaw}")[..16];

        var dept = Geo.ExtractDepartment(F("department"));
        var description = F("description") ?? title ?? "";

        return new Notice
        {
            SourceId = SourceId,
            Reference = reference,
            Title = title ?? reference,
            Description = description,
            BuyerName = buyer,
            Department = dept,
            Region = Geo.RegionOf(dept) ?? Cfg.DefaultRegion,
            PublicationDate = DateParsing.ParseFrench(F("publication"), Cfg.DateFormats),
            DeadlineDate = DateParsing.ParseFrench(deadlineRaw, Cfg.DateFormats),
            UrlNotice = ToAbsolute(pageUri, F("url")),
            UrlDce = ToAbsolute(pageUri, F("dce"))
        };
    }

    private static string? ToAbsolute(Uri baseUri, string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        return Uri.TryCreate(baseUri, href, out var u) ? u.ToString() : href;
    }

    /// <summary>« sélecteur CSS » (texte) ou « sélecteur CSS@attribut » ; sélecteur vide = la ligne elle-même.</summary>
    private static string? Extract(IElement row, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var sel = spec;
        string? attr = null;
        var at = spec.LastIndexOf('@');
        if (at >= 0) { sel = spec[..at]; attr = spec[(at + 1)..]; }
        var el = string.IsNullOrWhiteSpace(sel) ? row : row.QuerySelector(sel);
        if (el == null) return null;
        var v = attr == null ? Spaces.Replace(el.TextContent ?? "", " ").Trim() : el.GetAttribute(attr)?.Trim();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
