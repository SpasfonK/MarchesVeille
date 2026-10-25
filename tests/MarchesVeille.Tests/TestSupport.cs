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
