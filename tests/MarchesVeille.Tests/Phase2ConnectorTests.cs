using System.Text.RegularExpressions;
using MarchesVeille.Core.Cli;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Providers;

namespace MarchesVeille.Tests;

public class Phase2ConnectorTests
{
    private static ConnectorsConfig FastConfig()
    {
        var cfg = ConnectorsConfig.Default();
        cfg.RequestDelayMs = 0;
        return cfg;
    }

    // ------------------------------------------------------------------ config
    [Fact]
    public void Default_config_is_valid_and_registry_exposes_three_sources()
    {
        var cfg = ConnectorsConfig.Default();
        Assert.True(cfg.TaggingRules.Count >= 2);
        Assert.False(string.IsNullOrWhiteSpace(cfg.Place.RowSelector));
        Assert.Equal("Grand Est", cfg.GrandEst.DefaultRegion);

        var ids = new ConnectorRegistry(cfg).All.Select(p => p.SourceId).ToList();
        Assert.Equal(new[] { "boamp", "place", "grandest" }, ids);   // le profil d'exemple est désactivé
    }

    [Fact]
    public void Registry_rejects_unknown_source_and_filters_known_ones()
    {
        var reg = new ConnectorRegistry(ConnectorsConfig.Default());
        Assert.Single(reg.Select("boamp"));
        Assert.Equal(2, reg.Select("boamp, place").Count);
        Assert.Throws<ArgumentException>(() => reg.Select("nope"));
    }

    [Fact]
    public void Custom_connector_is_loaded_from_json_without_recompiling()
    {
        var cfg = ConnectorsConfig.Parse("""
        { "customConnectors": [ { "id": "mon-profil", "name": "Mon profil", "enabled": true,
            "baseUrl": "https://x.test/", "searchUrl": "s", "rowSelector": "tr" } ] }
        """);
        Assert.Contains(new ConnectorRegistry(cfg).All, p => p.SourceId == "mon-profil");
    }

    // ------------------------------------------------------------------ tagging
    [Theory]
    [InlineData("Voies navigables de France - Direction Territoriale Nord-Est", "Travaux", "VNF-DTNE")]
    [InlineData("VNF", "Dragage du canal de la Marne au Rhin", "VNF-DTNE")]
    [InlineData("VNF - DT Nord‑Est", "Fourniture", "VNF-DTNE")]
    [InlineData("Voies Navigables de France", "Entretien d'ouvrages à Lille", "VNF")]
    [InlineData("Commune de Metz", "Travaux d'écluse", null)]
    [InlineData("Sovnfx SARL", "Quelque chose", null)]
    public void Vnf_target_profile_tags_notices(string buyer, string title, string? expected)
    {
        var profile = new VnfTargetProfile(ConnectorsConfig.Default().TaggingRules);
        var n = new Notice { SourceId = "t", Reference = "1", BuyerName = buyer, Title = title };
        Assert.Equal(expected, profile.Qualify(n));
        profile.Apply(n);
        Assert.Equal(expected, n.BuyerCategory);
    }

    [Fact]
    public void Tagging_can_match_on_siret()
    {
        var profile = new VnfTargetProfile(new[]
        {
            new TaggingRule { Category = "MON-ACHETEUR", Sirets = { "130 018 400 00012" } }
        });
        var n = new Notice { BuyerName = "Inconnu", BuyerSiret = "13001840000012" };
        Assert.Equal("MON-ACHETEUR", profile.Qualify(n));
    }

    // ------------------------------------------------------------------ BOAMP
    private const string BoampJson = """
    {"total_count":2,"results":[
      {"idweb":"26-1001","nomacheteur":"VNF - Direction territoriale Nord-Est","objet":"Travaux de dragage du canal",
       "dateparution":"2026-10-12","datelimitereponse":"2026-11-02T12:00:00+00:00","code_departement":["54"],
       "url_avis":"https://www.boamp.fr/avis/detail/26-1001",
       "donnees":"{\"INFOS\":{\"SIRET\":\"13001945500011\",\"LIEU\":\"Nancy\"}}"},
      {"idweb":"26-1002","nomacheteur":"Mairie de Paris","objet":"Fournitures de bureau",
       "dateparution":"2026-10-12","code_departement":["75"]}
    ]}
    """;

    [Fact]
    public async Task Boamp_provider_maps_fields_and_filters_on_departments_and_vnf()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(BoampJson));
        var cfg = FastConfig();
        var provider = new BoampApiProvider(cfg.Boamp, cfg, new HttpClient(handler));

        var list = await provider.FetchAsync(new ScrapingOptions { Days = 1, Today = new DateTime(2026, 10, 12) }, CancellationToken.None);

        var n = Assert.Single(list);                     // la mairie de Paris (75) est écartée
        Assert.Equal("26-1001", n.Reference);
        Assert.Equal("boamp", n.SourceId);
        Assert.Equal("54", n.Department);
        Assert.Equal("Grand Est", n.Region);
        Assert.Equal("13001945500011", n.BuyerSiret);
        Assert.Equal(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), n.DeadlineDate);
        Assert.Contains("Nancy", n.Description);

        var req = Assert.Single(handler.Requests);
        Assert.Contains("api/explore/v2.1/catalog/datasets/boamp/records", req.Uri.AbsoluteUri);
        Assert.Contains("2026-10-12", Uri.UnescapeDataString(req.Uri.Query));
    }

    // ------------------------------------------------------------------ HTML générique
    private const string SearchPage = """
    <html><body>
    <form id="f" action="/search" method="post">
      <input type="hidden" name="PRADO_PAGESTATE" value="STATE0"/>
      <input type="text" name="dateStart" value=""/>
      <input type="text" name="dateEnd" value=""/>
      <input type="submit" name="go" value="Rechercher"/>
    </form></body></html>
    """;

    private const string Page1 = """
    <html><body><table class="res">
      <tr class="row"><td class="ref">2026-0001</td><td class="obj"><a href="/avis/1">Travaux d'écluse</a></td>
          <td class="buyer">VNF DT Nord-Est</td><td class="dept">54 - Meurthe-et-Moselle</td><td class="dl">30/10/2026 12:00</td></tr>
      <tr class="row"><td class="ref">2026-0002</td><td class="obj"><a href="/avis/2">Dragage</a></td>
          <td class="buyer">Commune de Metz</td><td class="dept">57</td><td class="dl">15/11/2026</td></tr>
    </table><a class="next" href="/page2">Suivant</a></body></html>
    """;

    private const string Page2 = """
    <html><body><table class="res">
      <tr class="row"><td class="ref">2026-0002</td><td class="obj"><a href="/avis/2">Dragage</a></td>
          <td class="buyer">Commune de Metz</td><td class="dept">57</td><td class="dl">15/11/2026</td></tr>
      <tr class="row"><td class="ref">2026-0003</td><td class="obj"><a href="/avis/3">Curage des berges</a></td>
          <td class="buyer">Région Grand Est</td><td class="dept"></td><td class="dl"></td></tr>
    </table></body></html>
    """;

    private static HtmlConnectorConfig TestHtmlConfig() => new()
    {
        Id = "test-html", Name = "Test", BaseUrl = "https://example.test/", SearchUrl = "/list",
        FormSelector = "form",
        FormFields = { ["dateStart"] = "{dateStart}", ["dateEnd"] = "{dateEnd}" },
        SubmitName = "go",
        RowSelector = "table.res tr.row",
        Fields =
        {
            ["reference"] = "td.ref", ["title"] = "td.obj a", ["buyer"] = "td.buyer", ["department"] = "td.dept",
            ["deadline"] = "td.dl", ["url"] = "td.obj a@href"
        },
        Paging = { Mode = "link", NextSelector = "a.next" },
        DefaultRegion = null
    };

    [Fact]
    public async Task Html_provider_submits_form_follows_pagination_and_dedups_across_pages()
    {
        var handler = new FakeHandler((req, body) => (req.Method.Method, req.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/list") => FakeHandler.Html(SearchPage),
            ("POST", "/search") => FakeHandler.Html(Page1),
            ("GET", "/page2") => FakeHandler.Html(Page2),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        var cfg = FastConfig();
        var provider = new HtmlFormProvider(TestHtmlConfig(), cfg, new HttpClient(handler));

        var list = await provider.FetchAsync(new ScrapingOptions { Days = 3, Today = new DateTime(2026, 10, 12) }, CancellationToken.None);

        Assert.Equal(3, list.Count);                                   // 0002 présent sur les deux pages : compté une fois
        var first = list.First(n => n.Reference == "2026-0001");
        Assert.Equal("Travaux d'écluse", first.Title);
        Assert.Equal("VNF DT Nord-Est", first.BuyerName);
        Assert.Equal("54", first.Department);
        Assert.Equal("Grand Est", first.Region);
        Assert.Equal("https://example.test/avis/1", first.UrlNotice);
        Assert.Equal(new DateTime(2026, 10, 30), first.DeadlineDate!.Value.Date);
        Assert.Null(list.First(n => n.Reference == "2026-0003").DeadlineDate);

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Contains("PRADO_PAGESTATE=STATE0", post.Body);          // champs cachés repris
        Assert.Contains("dateStart=10%2F10%2F2026", post.Body);        // 3 jours : 10, 11, 12 octobre
        Assert.Contains("dateEnd=12%2F10%2F2026", post.Body);
        Assert.Contains("go=1", post.Body);
    }

    [Fact]
    public async Task Html_provider_reports_missing_form()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Html("<html><body>rien</body></html>"));
        var provider = new HtmlFormProvider(TestHtmlConfig(), FastConfig(), new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.FetchAsync(new ScrapingOptions { Today = new DateTime(2026, 10, 12) }, CancellationToken.None));
    }

    // ------------------------------------------------------------------ CLI + ingestion
    private static List<Notice> ThreeNoticesOneDuplicate() => new()
    {
        new Notice { SourceId = "boamp", Reference = "A", Title = "Travaux écluse", BuyerName = "VNF - Direction territoriale Nord-Est", Department = "54" },
        new Notice { SourceId = "boamp", Reference = "B", Title = "Fourniture", BuyerName = "Région Grand Est", Department = "67" },
        new Notice { SourceId = "boamp", Reference = "A", Title = "Travaux écluse", BuyerName = "VNF - Direction territoriale Nord-Est", Department = "54" }
    };

    [Fact]
    public async Task Cli_scrap_ingests_reports_duplicates_and_tags_vnf()
    {
        using var t = new TestDb();
        var provider = new MockProvider("boamp", ThreeNoticesOneDuplicate());
        IReadOnlyList<IMarketplaceProvider> Factory(ConnectorsConfig _) => new IMarketplaceProvider[] { provider };

        var out1 = new StringWriter();
        var code1 = await CliRunner.RunAsync(new[] { "--scrap", "--source=boamp", "--days=3" }, out1, t.Dir, Factory);
        Assert.Equal(0, code1);
        Assert.Matches(new Regex(@"TOTAL : récupérés=3 nouveaux=2 doublons=1"), out1.ToString());

        var out2 = new StringWriter();
        var code2 = await CliRunner.RunAsync(new[] { "--scrap", "--source=boamp" }, out2, t.Dir, Factory);
        Assert.Equal(0, code2);
        Assert.Matches(new Regex(@"TOTAL : récupérés=3 nouveaux=0 doublons=3"), out2.ToString());

        var repo = new MarchesVeille.Core.Data.NoticeRepository(new MarchesVeille.Core.Data.Database(Path.Combine(t.Dir, "marches.db")));
        Assert.Equal(1, repo.Count(new SearchFilters { BuyerCategory = "VNF-DTNE" }));
        Assert.Equal(2, repo.Count(new SearchFilters { Region = "Grand Est" }));
        Assert.True(File.Exists(Path.Combine(t.Dir, "connectors.json")));   // créé au premier lancement
    }

    [Fact]
    public async Task Cli_validates_arguments()
    {
        using var t = new TestDb();
        IReadOnlyList<IMarketplaceProvider> Factory(ConnectorsConfig _) => new IMarketplaceProvider[] { new MockProvider("boamp", new()) };

        var o1 = new StringWriter();
        Assert.Equal(2, await CliRunner.RunAsync(new[] { "--scrap", "--source=inconnue" }, o1, t.Dir, Factory));
        Assert.Contains("inconnue", o1.ToString());

        Assert.Equal(2, await CliRunner.RunAsync(new[] { "--scrap", "--days=abc" }, new StringWriter(), t.Dir, Factory));
        Assert.Equal(2, await CliRunner.RunAsync(new[] { "--scrap", "--days=0" }, new StringWriter(), t.Dir, Factory));

        var help = new StringWriter();
        Assert.Equal(0, await CliRunner.RunAsync(new[] { "--help" }, help, t.Dir, Factory));
        Assert.Contains("--scrap", help.ToString());
        Assert.True(CliRunner.IsCliInvocation(new[] { "--scrap" }));
        Assert.False(CliRunner.IsCliInvocation(Array.Empty<string>()));
    }

    [Fact]
    public async Task Cli_reports_source_failure_with_exit_code_1_and_keeps_other_sources()
    {
        using var t = new TestDb();
        var failing = new ThrowingProvider("place");
        var ok = new MockProvider("boamp", ThreeNoticesOneDuplicate());
        IReadOnlyList<IMarketplaceProvider> Factory(ConnectorsConfig _) => new IMarketplaceProvider[] { failing, ok };

        var output = new StringWriter();
        var code = await CliRunner.RunAsync(new[] { "--scrap", "--source=all" }, output, t.Dir, Factory);
        Assert.Equal(1, code);
        Assert.Contains("ERREUR", output.ToString());
        Assert.Contains("boamp : récupérés=3 nouveaux=2", output.ToString());
    }

    private sealed class ThrowingProvider : IMarketplaceProvider
    {
        public ThrowingProvider(string id) => SourceId = id;
        public string SourceId { get; }
        public string Name => "Throwing";
        public string Type => "HTML_SCRAPER";
        public string? BaseUrl => null;
        public Task<IReadOnlyList<Notice>> FetchAsync(ScrapingOptions options, CancellationToken ct) =>
            throw new HttpRequestException("site indisponible");
    }
}
