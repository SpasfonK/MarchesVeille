using System.Net;

namespace MarchesVeille.Core.Providers;

internal static class HttpFactory
{
    /// <summary>Client avec cookies de session (un par connecteur), décompression et en-têtes réalistes.</summary>
    public static HttpClient Create(string userAgent)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        return client;
    }
}

internal static class HttpRetry
{
    /// <summary>Envoie la requête avec reprises (backoff linéaire) sur erreurs réseau, 429 et 5xx.</summary>
    public static async Task<(string Body, Uri FinalUri)> SendForStringAsync(
        HttpClient http, Func<HttpRequestMessage> factory, int maxAttempts, int baseDelayMs, CancellationToken ct)
    {
        maxAttempts = Math.Max(1, maxAttempts);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = factory();
                var requested = req.RequestUri!;
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                if ((code == 429 || code >= 500) && attempt < maxAttempts)
                {
                    await Task.Delay(baseDelayMs * attempt, ct).ConfigureAwait(false);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (body, resp.RequestMessage?.RequestUri ?? requested);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await Task.Delay(baseDelayMs * attempt, ct).ConfigureAwait(false);
            }
        }
    }
}

internal sealed record FetchResult(Uri FinalUri, string? Html, string? TempFile, string? FileName, long Size);

internal static class HttpDownload
{
    /// <summary>
    /// Envoie la requête. Réponse HTML : renvoyée en texte. Réponse binaire (pièce jointe, zip, pdf…) : écrite dans tempDir
    /// en flux, avec plafond de taille.
    /// </summary>
    public static async Task<FetchResult> SendAsync(
        HttpClient http, Func<HttpRequestMessage> factory, string tempDir, long maxBytes,
        int maxAttempts, int baseDelayMs, CancellationToken ct)
    {
        maxAttempts = Math.Max(1, maxAttempts);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = factory();
                var requested = req.RequestUri!;
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                if ((code == 429 || code >= 500) && attempt < maxAttempts)
                {
                    await Task.Delay(baseDelayMs * attempt, ct).ConfigureAwait(false);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                var finalUri = resp.RequestMessage?.RequestUri ?? requested;

                var media = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                var disp = resp.Content.Headers.ContentDisposition;
                var dispName = disp?.FileNameStar ?? disp?.FileName;
                bool attachment = disp != null &&
                    (string.Equals(disp.DispositionType, "attachment", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(dispName));
                bool isHtml = media is "text/html" or "application/xhtml+xml" or "text/plain" or "";

                if (isHtml && !attachment)
                {
                    var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return new FetchResult(finalUri, html, null, null, html.Length);
                }

                var fileName = SafeName(dispName?.Trim('"') ?? Path.GetFileName(finalUri.AbsolutePath));
                if (string.IsNullOrWhiteSpace(fileName)) fileName = media.Contains("zip") ? "dce.zip" : "dce.bin";
                Directory.CreateDirectory(tempDir);
                var temp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.part");
                long total = 0;
                try
                {
                    await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > maxBytes) throw new InvalidOperationException($"Fichier trop volumineux (> {maxBytes / (1024 * 1024)} Mo).");
                            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    try { File.Delete(temp); } catch { }
                    throw;
                }
                return new FetchResult(finalUri, null, temp, fileName, total);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await Task.Delay(baseDelayMs * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    public static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length > 120 ? s[^120..] : s;
    }
}
