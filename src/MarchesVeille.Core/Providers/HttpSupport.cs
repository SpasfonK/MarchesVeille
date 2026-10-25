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
