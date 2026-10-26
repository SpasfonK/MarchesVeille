using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Core.Providers;

/// <summary>
/// Parcours de téléchargement anonyme d'un DCE, piloté par connectors.json (section « dce » d'un connecteur) :
/// suite d'étapes get / link / form, la dernière devant renvoyer un fichier (zip, pdf…).
/// Aucune donnée d'identité n'est envoyée : seuls les champs configurés sont postés.
/// </summary>
internal sealed class DceFlow
{
    private readonly HttpClient _http;
    private readonly ConnectorsConfig _global;
    private readonly HtmlConnectorConfig _cfg;

    public DceFlow(HttpClient http, ConnectorsConfig global, HtmlConnectorConfig cfg)
    {
        _http = http;
        _global = global;
        _cfg = cfg;
    }

    public static string Host(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        var h = u.Host.ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }

    /// <summary>Le connecteur sait traiter l'avis s'il en est la source, ou si l'avis pointe vers le même site.</summary>
    public static bool Handles(HtmlConnectorConfig cfg, Notice n)
    {
        if (!cfg.Dce.Enabled) return false;
        if (string.Equals(n.SourceId, cfg.Id, StringComparison.OrdinalIgnoreCase)) return true;
        var host = Host(cfg.BaseUrl);
        return host.Length > 0 && (Host(n.UrlDce) == host || Host(n.UrlNotice) == host);
    }

    public async Task<DceDownloadResult> RunAsync(Notice n, string tempDir, long maxBytes, string? debugDir, CancellationToken ct)
    {
        var flow = _cfg.Dce;
        var startUrl = n.UrlDce ?? n.UrlNotice;
        if (string.IsNullOrWhiteSpace(startUrl) && flow.Steps.All(s => !s.Type.Equals("get", StringComparison.OrdinalIgnoreCase)))
            return new DceDownloadResult(DceOutcome.Unsupported, null, null, 0, "Aucune URL d'avis ou de DCE connue.");

        var baseUri = new Uri(_cfg.BaseUrl, UriKind.Absolute);
        var steps = flow.Steps.Count > 0
            ? flow.Steps
            : new List<DceStepConfig> { new() { Type = "get", Url = "{dceUrl}" } };   // lien direct vers le fichier
        var parser = new HtmlParser();
        FetchResult? cur = null;

        string Tok(string s) => s.Replace("{noticeUrl}", n.UrlNotice ?? "", StringComparison.Ordinal)
                                 .Replace("{dceUrl}", n.UrlDce ?? n.UrlNotice ?? "", StringComparison.Ordinal);

        for (int k = 0; k < steps.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var st = steps[k];
            var type = st.Type.ToLowerInvariant();
            var where = $"étape {k + 1} ({type})";

            if (type == "get")
            {
                var url = Tok(string.IsNullOrWhiteSpace(st.Url) ? "{dceUrl}" : st.Url);
                if (string.IsNullOrWhiteSpace(url)) return Fail(where, "URL vide");
                var target = HtmlFormProvider.Resolve(cur?.FinalUri ?? baseUri, url);
                cur = await FetchAsync(() => new HttpRequestMessage(HttpMethod.Get, target), tempDir, maxBytes, ct);
            }
            else if (type == "link" || type == "form")
            {
                if (cur?.Html == null) return Fail(where, "la page précédente n'est pas du HTML");
                var doc = parser.ParseDocument(cur.Html);
                if (type == "link")
                {
                    var el = string.IsNullOrWhiteSpace(st.Selector) ? null : doc.QuerySelector(st.Selector);
                    var href = el?.GetAttribute("href");
                    if (string.IsNullOrWhiteSpace(href) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (st.Optional) continue;
                        return Fail(where, $"lien introuvable ({st.Selector})", cur.Html);
                    }
                    var target = HtmlFormProvider.Resolve(cur.FinalUri, href);
                    cur = await FetchAsync(() => new HttpRequestMessage(HttpMethod.Get, target), tempDir, maxBytes, ct);
                }
                else
                {
                    var form = doc.QuerySelector(string.IsNullOrWhiteSpace(st.FormSelector) ? "form" : st.FormSelector) as IHtmlFormElement;
                    if (form == null)
                    {
                        if (st.Optional) continue;
                        return Fail(where, $"formulaire introuvable ({st.FormSelector ?? "form"})", cur.Html);
                    }
                    var fields = HtmlFormProvider.CollectFields(form);
                    foreach (var (name, value) in st.Fields) fields[name] = Tok(value);
                    if (!string.IsNullOrWhiteSpace(st.SubmitName))
                    {
                        if (!string.IsNullOrWhiteSpace(st.PostbackTargetField)) fields[st.PostbackTargetField] = st.SubmitName;
                        else fields[st.SubmitName] = "1";
                    }
                    var action = HtmlFormProvider.Resolve(cur.FinalUri, form.GetAttribute("action"));
                    cur = await FetchAsync(() => new HttpRequestMessage(HttpMethod.Post, action) { Content = new FormUrlEncodedContent(fields) },
                        tempDir, maxBytes, ct);
                }
            }
            else return Fail(where, $"type d'étape inconnu « {st.Type} »");

            Dump(debugDir, k + 1, cur);
            if (cur.TempFile != null) break;                 // fichier obtenu : parcours terminé
            await Task.Delay(_global.RequestDelayMs, ct);
        }

        if (cur?.TempFile != null)
            return new DceDownloadResult(DceOutcome.Downloaded, cur.TempFile, cur.FileName, cur.Size, null);

        var norm = TextNormalizer.Normalize(cur?.Html);
        var restricted = flow.RestrictedPatterns.Any(p => norm.Contains(TextNormalizer.Normalize(p)));
        return restricted
            ? new DceDownloadResult(DceOutcome.Restricted, null, null, 0, "Accès au DCE soumis à identification ou inscription.")
            : new DceDownloadResult(DceOutcome.Failed, null, null, 0, "Le parcours n'a pas abouti à un fichier (voir le mode diagnostic).");
    }

    private Task<FetchResult> FetchAsync(Func<HttpRequestMessage> factory, string tempDir, long maxBytes, CancellationToken ct) =>
        HttpDownload.SendAsync(_http, factory, tempDir, maxBytes, _global.MaxRetries, _global.RequestDelayMs, ct);

    private static DceDownloadResult Fail(string where, string why, string? html = null) =>
        new(DceOutcome.Failed, null, null, 0, $"{where} : {why}");

    private static void Dump(string? debugDir, int step, FetchResult? r)
    {
        if (debugDir == null || r == null) return;
        try
        {
            Directory.CreateDirectory(debugDir);
            File.WriteAllText(Path.Combine(debugDir, $"etape{step}.html"),
                r.Html ?? $"<!-- fichier binaire : {r.FileName} ({r.Size} octets) -->");
        }
        catch { /* diagnostic au mieux */ }
    }
}
