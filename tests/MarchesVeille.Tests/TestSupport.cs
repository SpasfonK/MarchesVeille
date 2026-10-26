using System.IO.Compression;
using System.Net.Http.Headers;
using MarchesVeille.Core.Data;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Providers;

namespace MarchesVeille.Tests;

public sealed class TestDb : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("mv-").FullName;
    public Database Db { get; }
    public NoticeRepository Repo { get; }

    public TestDb()
    {
        Db = new Database(Path.Combine(Dir, "marches.db"));
        Db.Migrate();
        Repo = new NoticeRepository(Db);
    }

    public void Dispose()
    {
        Database.ReleasePools();
        try { Directory.Delete(Dir, true); } catch { /* nettoyage au mieux */ }
    }
}

public static class SampleData
{
    private static readonly string[] Objets =
    {
        "travaux de rénovation d'écluse", "fourniture de matériel informatique", "maintenance des barrages",
        "dragage du canal", "curage des berges", "étude de franchissement piscicole", "assistance à maîtrise d'ouvrage",
        "réfection de pont-canal", "entretien des espaces verts", "marché de nettoyage de bâtiments"
    };
    private static readonly string[] Lieux = { "Nancy", "Metz", "Strasbourg", "Reims", "Épinal", "Bar-le-Duc", "Troyes", "Colmar" };
    private static readonly string[] Acheteurs =
    {
        "VNF - Direction territoriale Nord-Est", "Région Grand Est", "Commune de Nancy", "Ville de Metz",
        "Département de la Meuse", "Université de Lorraine"
    };

    public static List<Notice> Generate(int count, int seed = 42)
    {
        var rnd = new Random(seed);
        var now = DateTime.UtcNow;
        var list = new List<Notice>(count);
        for (int i = 0; i < count; i++)
        {
            var objet = i % 7 == 0 ? "travaux de rénovation d'écluse" : Objets[rnd.Next(Objets.Length)];
            var lieu = Lieux[rnd.Next(Lieux.Length)];
            list.Add(new Notice
            {
                SourceId = "test",
                Reference = $"REF-{i:D6}",
                Title = $"{objet} - {lieu} lot {i % 5 + 1}",
                Description = $"Consultation n°{i} : {objet} sur le secteur de {lieu}. Les travaux comprennent la fourniture, la pose et les essais.",
                BuyerName = Acheteurs[rnd.Next(Acheteurs.Length)],
                Department = "54",
                Region = "Grand Est",
                PublicationDate = now.AddDays(-rnd.Next(0, 30)),
                DeadlineDate = now.AddDays(rnd.Next(-10, 40))
            });
        }
        return list;
    }

    public static Notice One(string reference, string title, string buyer = "Acheteur test", string description = "")
        => new() { SourceId = "test", Reference = reference, Title = title, BuyerName = buyer, Description = description };
}

public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;
    public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = new();

    public FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : "";
        Requests.Add((request.Method, request.RequestUri!, body));
        var resp = _respond(request, body);
        resp.RequestMessage = request;
        return resp;
    }

    public static HttpResponseMessage Html(string html) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };

    public static HttpResponseMessage File(byte[] bytes, string fileName, string mediaType = "application/zip")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
    }

    public static HttpResponseMessage Json(string json) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

public sealed class MockProvider : IMarketplaceProvider
{
    private readonly IReadOnlyList<Notice> _items;
    public MockProvider(string id, IReadOnlyList<Notice> items) { SourceId = id; _items = items; }
    public string SourceId { get; }
    public string Name => "Mock " + SourceId;
    public string Type => "API";
    public string? BaseUrl => null;
    public Task<IReadOnlyList<Notice>> FetchAsync(ScrapingOptions options, CancellationToken ct)
    {
        // copies : l'ingestion modifie les avis (id, tag, région)
        IReadOnlyList<Notice> copy = _items.Select(n => new Notice
        {
            SourceId = n.SourceId, Reference = n.Reference, Title = n.Title, Description = n.Description,
            BuyerName = n.BuyerName, Department = n.Department
        }).ToList();
        return Task.FromResult(copy);
    }
}

public static class ZipHelper
{
    public static byte[] Make(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new System.Text.UTF8Encoding(false));
                w.Write(content);
            }
        return ms.ToArray();
    }

    public static byte[] MakeBytes(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    /// <summary>Document Word minimal : un .docx est un zip contenant word/document.xml.</summary>
    public static byte[] Docx(string text) =>
        Make(("word/document.xml", $"<w:document><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>"));
}

public sealed class FakeDceProvider : IDceDownloadProvider
{
    public string SourceId => "boamp";
    public string? BaseUrl => null;
    public int Calls { get; private set; }
    public Func<Notice, DceOutcome> Decide { get; set; } = _ => DceOutcome.Downloaded;
    public bool CanDownload(Notice n) => true;

    public Task<DceDownloadResult> DownloadDceAsync(Notice n, string tempDir, long maxBytes, string? debugDir, CancellationToken ct)
    {
        Calls++;
        var outcome = Decide(n);
        if (outcome != DceOutcome.Downloaded)
            return Task.FromResult(new DceDownloadResult(outcome, null, null, 0, "simulé : " + outcome));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".part");
        File.WriteAllBytes(path, ZipHelper.Make(
            ("cctp.txt", "Le présent CCTP décrit la rénovation de l'écluse et le dragage."),
            ("lot1/bpu.txt", "Bordereau des prix unitaires")));
        return Task.FromResult(new DceDownloadResult(DceOutcome.Downloaded, path, "dce.zip", new FileInfo(path).Length, null));
    }
}
