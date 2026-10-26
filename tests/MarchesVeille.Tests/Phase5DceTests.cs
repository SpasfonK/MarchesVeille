using System.IO.Compression;
using MarchesVeille.Core.Cli;
using MarchesVeille.Core.Config;
using MarchesVeille.Core.Data;
using MarchesVeille.Core.Ingestion;
using MarchesVeille.Core.Models;
using MarchesVeille.Core.Providers;
using MarchesVeille.Core.Util;

namespace MarchesVeille.Tests;

public class Phase5DceTests
{
    // ------------------------------------------------------------------ schéma
    [Fact]
    public void Migration_v2_adds_archive_tables_and_market_columns()
    {
        using var t = new TestDb();
        using var conn = t.Db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','trigger')";
        var names = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
        foreach (var n in new[] { "dce_archives", "dce_fts", "dce_profiles", "dce_ai", "dce_ad", "dce_au" }) Assert.Contains(n, names);

        using var v = conn.CreateCommand();
        v.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, (long)v.ExecuteScalar()!);
    }

    // ------------------------------------------------------------------ classification
    [Theory]
    [InlineData("Travaux de rénovation de l'écluse n°4", "Travaux", false)]
    [InlineData("Fourniture de matériel informatique", "Fournitures", false)]
    [InlineData("Prestation de nettoyage des locaux", "Services", false)]
    [InlineData("Mission de maîtrise d'œuvre pour la réfection du pont", "Travaux", true)]
    [InlineData("Étude de franchissement piscicole", "Services", true)]
    [InlineData("Assistance à maîtrise d'ouvrage - diagnostic des ouvrages", "Services", true)]
    public void Classifier_detects_market_type_and_pi(string title, string type, bool pi)
    {
        var c = new NoticeClassifier(ConnectorsConfig.Default().Classification);
        var n = new Notice { Title = title };
        c.Apply(n);
        Assert.Equal(type, n.MarketType);
        Assert.Equal(pi, n.IsPi);
    }

    [Fact]
    public void Classifier_keeps_the_type_given_by_the_source()
    {
        var c = new NoticeClassifier(ConnectorsConfig.Default().Classification);
        var n = new Notice { Title = "Fourniture et pose de bornes", MarketType = "Travaux" };
        c.Apply(n);
        Assert.Equal("Travaux", n.MarketType);
        Assert.Equal("Travaux", NoticeClassifier.NormalizeMarketType("TRAVAUX"));
        Assert.Equal("Fournitures", NoticeClassifier.NormalizeMarketType("Fournitures courantes"));
        Assert.Null(NoticeClassifier.NormalizeMarketType("autre"));
    }

    [Fact]
    public void Ingestion_stores_market_type_and_pi_and_search_filters_on_them()
    {
        using var t = new TestDb();
        var classifier = new NoticeClassifier(ConnectorsConfig.Default().Classification);
        var a = SampleData.One("1", "Travaux de dragage"); classifier.Apply(a);
        var b = SampleData.One("2", "Étude hydraulique du canal"); classifier.Apply(b);
        var c = SampleData.One("3", "Fourniture de papier"); classifier.Apply(c);
        t.Repo.BulkUpsert(new[] { a, b, c });

        Assert.Equal(1, t.Repo.Count(new SearchFilters { MarketType = "Travaux" }));
        Assert.Equal(1, t.Repo.Count(new SearchFilters { MarketType = "PI" }));
        Assert.Equal(1, t.Repo.Count(new SearchFilters { MarketType = "Services" }));
        Assert.Equal(1, t.Repo.Count(new SearchFilters { MarketType = "Fournitures" }));
    }

    // ------------------------------------------------------------------ profils de collecte
    [Fact]
    public void Profile_matching_is_accent_insensitive_and_combines_criteria()
    {
        var n = new Notice
        {
            Title = "Rénovation de l'écluse n°12", BuyerName = "Voies navigables de France", Region = "Grand Est",
            BuyerCategory = "VNF-DTNE", MarketType = "Travaux", IsPi = false
        };
        Assert.True(new DceProfile { Keywords = { "ECLUSE" } }.Matches(n));
        Assert.True(new DceProfile { Buyers = { "voies navigables" }, MarketTypes = { "Travaux" } }.Matches(n));
        Assert.False(new DceProfile { Keywords = { "barrage" } }.Matches(n));
        Assert.False(new DceProfile { Keywords = { "ecluse" }, ExcludeKeywords = { "renovation" } }.Matches(n));
        Assert.False(new DceProfile { MarketTypes = { "Fournitures" } }.Matches(n));
        Assert.False(new DceProfile { MarketTypes = { "PI" } }.Matches(n));
        Assert.True(new DceProfile { Regions = { "grand est" }, Categories = { "VNF-DTNE" } }.Matches(n));
        Assert.True(new DceProfile().Matches(n));
        Assert.True(new DceProfile().MatchesEverything);

        n.IsPi = true;
        Assert.True(new DceProfile { MarketTypes = { "Fournitures", "PI" } }.Matches(n));
    }

    [Fact]
    public void Profiles_roundtrip_through_the_database()
    {
        using var t = new TestDb();
        var repo = new ArchiveRepository(t.Db);
        var id = repo.SaveProfile(new DceProfile { Name = "VNF travaux", Keywords = { "écluse" }, MarketTypes = { "Travaux", "PI" } });
        var p = Assert.Single(repo.ListProfiles());
        Assert.Equal(id, p.Id);
        Assert.Equal("VNF travaux", p.Name);
        Assert.Equal(new[] { "Travaux", "PI" }, p.MarketTypes);

        p.Enabled = false;
        repo.SaveProfile(p);
        Assert.False(Assert.Single(repo.ListProfiles()).Enabled);
        repo.DeleteProfile(id);
        Assert.Empty(repo.ListProfiles());
    }

    // ------------------------------------------------------------------ extraction de contenu
    [Fact]
    public void Content_extractor_lists_files_and_reads_text_docx_and_nested_zip()
    {
        var inner = ZipHelper.Make(("annexe/planning.txt", "Planning prévisionnel des travaux de curage"));
        var outer = ZipHelper.MakeBytes(
            ("CCTP.txt", System.Text.Encoding.UTF8.GetBytes("Cahier des clauses techniques : remplacement des portes d'écluse")),
            ("RC/reglement.docx", ZipHelper.Docx("Règlement de consultation : critères de jugement")),
            ("pieces.zip", inner),
            ("plans/plan.dwg", new byte[] { 1, 2, 3, 4 }));

        var path = Path.Combine(Directory.CreateTempSubdirectory("mv-ext-").FullName, "dce.zip");
        File.WriteAllBytes(path, outer);
        try
        {
            var r = ContentExtractor.ExtractFromFile(path);
            Assert.Contains("CCTP.txt", r.Files);
            Assert.Contains("RC/reglement.docx", r.Files);
            Assert.Contains("pieces.zip", r.Files);
            Assert.Contains("pieces.zip/annexe/planning.txt", r.Files);
            Assert.Contains("plans/plan.dwg", r.Files);
            Assert.Contains("portes d'écluse", r.Text);
            Assert.Contains("critères de jugement", r.Text);
            Assert.Contains("curage", r.Text);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public void Content_extractor_survives_corrupt_files()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("mv-bad-").FullName, "dce.zip");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04, 9, 9, 9, 9, 9 });
        try
        {
            var r = ContentExtractor.ExtractFromFile(path);
            Assert.NotNull(r);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    // ------------------------------------------------------------------ parcours anonyme HTML
    private static HtmlConnectorConfig DceConfig() => new()
    {
        Id = "place", Name = "Place test", BaseUrl = "https://example.test/", SearchUrl = "/list",
        Dce =
        {
            Enabled = true,
            Steps =
            {
                new DceStepConfig { Type = "get", Url = "{dceUrl}" },
                new DceStepConfig { Type = "link", Selector = "a.dce" },
                new DceStepConfig { Type = "form", FormSelector = "form#anon", Fields = { ["choix"] = "anonyme" }, SubmitName = "valider" },
                new DceStepConfig { Type = "link", Selector = "a.zip" }
            }
        }
    };

    private static Notice DceNotice() => new()
    {
        SourceId = "place", Reference = "2026-001", Title = "Travaux d'écluse", BuyerName = "VNF",
        UrlNotice = "https://example.test/avis/1"
    };

    [Fact]
    public async Task Dce_flow_downloads_anonymously_without_sending_identity()
    {
        var zip = ZipHelper.Make(("cctp.txt", "contenu"));
        var handler = new FakeHandler((req, body) => (req.Method.Method, req.RequestUri!.PathAndQuery) switch
        {
            ("GET", "/avis/1") => FakeHandler.Html("<a class='dce' href='/dce/demande?id=1'>Télécharger le dossier</a>"),
            ("GET", "/dce/demande?id=1") => FakeHandler.Html(
                "<form id='anon' action='/dce/choix' method='post'><input type='hidden' name='TOKEN' value='T1'/>" +
                "<input type='radio' name='choix' value='anonyme'/><input type='radio' name='choix' value='identifie'/>" +
                "<input type='text' name='email' value=''/><input type='submit' name='valider' value='Valider'/></form>"),
            ("POST", "/dce/choix") => FakeHandler.Html("<a class='zip' href='/dce/get/1.zip'>DCE complet</a>"),
            ("GET", "/dce/get/1.zip") => FakeHandler.File(zip, "DCE_2026-001.zip"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        var cfg = ConnectorsConfig.Default(); cfg.RequestDelayMs = 0;
        var provider = new HtmlFormProvider(DceConfig(), cfg, new HttpClient(handler));
        var notice = DceNotice();
        Assert.True(provider.CanDownload(notice));

        var temp = Directory.CreateTempSubdirectory("mv-dl-").FullName;
        try
        {
            var res = await provider.DownloadDceAsync(notice, temp, 10_000_000, null, CancellationToken.None);
            Assert.Equal(DceOutcome.Downloaded, res.Outcome);
            Assert.Equal("DCE_2026-001.zip", res.FileName);
            Assert.True(File.Exists(res.FilePath));
            Assert.Equal(zip.Length, new FileInfo(res.FilePath!).Length);

            var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
            Assert.Contains("TOKEN=T1", post.Body);
            Assert.Contains("choix=anonyme", post.Body);
            Assert.Contains("valider=1", post.Body);
            Assert.Contains("email=&", post.Body + "&");     // champ resté vide : aucune coordonnée envoyée
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    [Fact]
    public async Task Dce_flow_reports_restricted_access_and_missing_links()
    {
        var cfg = ConnectorsConfig.Default(); cfg.RequestDelayMs = 0;
        var temp = Directory.CreateTempSubdirectory("mv-dl2-").FullName;
        try
        {
            var restricted = new FakeHandler((_, _) => FakeHandler.Html("<html><body>Identifiez-vous avec votre mot de passe</body></html>"));
            var c1 = new HtmlConnectorConfig { Id = "place", BaseUrl = "https://example.test/", Dce = { Enabled = true } };
            var p1 = new HtmlFormProvider(c1, cfg, new HttpClient(restricted));
            var r1 = await p1.DownloadDceAsync(DceNotice(), temp, 1_000_000, null, CancellationToken.None);
            Assert.Equal(DceOutcome.Restricted, r1.Outcome);

            var noLink = new FakeHandler((_, _) => FakeHandler.Html("<html><body>rien</body></html>"));
            var p2 = new HtmlFormProvider(DceConfig(), cfg, new HttpClient(noLink));
            var r2 = await p2.DownloadDceAsync(DceNotice(), temp, 1_000_000, null, CancellationToken.None);
            Assert.Equal(DceOutcome.Failed, r2.Outcome);
            Assert.Contains("lien introuvable", r2.Error);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    [Fact]
    public async Task Dce_flow_enforces_size_limit_and_leaves_no_partial_file()
    {
        var big = new byte[200_000];
        var handler = new FakeHandler((req, _) => FakeHandler.File(big, "gros.zip"));
        var cfg = ConnectorsConfig.Default(); cfg.RequestDelayMs = 0;
        var c = new HtmlConnectorConfig { Id = "place", BaseUrl = "https://example.test/", Dce = { Enabled = true } };
        var provider = new HtmlFormProvider(c, cfg, new HttpClient(handler));
        var temp = Directory.CreateTempSubdirectory("mv-dl3-").FullName;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.DownloadDceAsync(DceNotice(), temp, 1_000, Path.Combine(temp, "diag"), CancellationToken.None));
            Assert.Empty(Directory.GetFiles(temp, "*.part"));      // pas de fichier partiel
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    // ------------------------------------------------------------------ collecte + archives
    private static Notice N(string reference, string title, string buyer, string? type, bool pi = false) => new()
    {
        SourceId = "boamp", Reference = reference, Title = title, BuyerName = buyer, MarketType = type, IsPi = pi,
        Region = "Grand Est", PublicationDate = DateTime.UtcNow.AddDays(-2), DeadlineDate = DateTime.UtcNow.AddDays(20)
    };

    private static (DceCollector Collector, ArchiveRepository Archives, FakeDceProvider Provider) Setup(TestDb t, int maxAttempts = 3, int maxPerRun = 300)
    {
        var archives = new ArchiveRepository(t.Db);
        var provider = new FakeDceProvider();
        var settings = new DceSettings { Folder = Path.Combine(t.Dir, "DCE"), DelayMs = 0, MaxAttempts = maxAttempts, MaxPerRun = maxPerRun };
        return (new DceCollector(archives, new IDceDownloadProvider[] { provider }, settings, t.Dir), archives, provider);
    }

    [Fact]
    public async Task Collector_downloads_only_matching_notices_once_and_archives_are_searchable()
    {
        using var t = new TestDb();
        t.Repo.BulkUpsert(new[]
        {
            N("A", "Rénovation de l'écluse n°4", "Voies navigables de France", "Travaux"),
            N("B", "Fourniture de papier", "Région Grand Est", "Fournitures"),
            N("C", "Étude hydraulique", "Voies navigables de France", "Services", pi: true)
        });
        var (collector, archives, provider) = Setup(t);
        var profiles = new List<DceProfile> { new() { Name = "VNF travaux", Buyers = { "voies navigables" }, MarketTypes = { "Travaux" } } };

        var s1 = await collector.RunAsync(profiles, new DceRunOptions(), null, CancellationToken.None);
        Assert.Equal(3, s1.Candidates);
        Assert.Equal(1, s1.Matched);
        Assert.Equal(1, s1.Downloaded);
        Assert.Equal(1, provider.Calls);

        var s2 = await collector.RunAsync(profiles, new DceRunOptions(), null, CancellationToken.None);
        Assert.Equal(2, s2.Candidates);          // A est archivé : plus candidat
        Assert.Equal(0, s2.Downloaded);
        Assert.Equal(1, provider.Calls);

        var hit = Assert.Single(archives.Search(new ArchiveFilters { Text = "ecluse", Scope = ArchiveScope.Content }));
        var a = hit.Archive;
        Assert.Equal(DceStatus.Ok, a.Status);
        Assert.True(File.Exists(collector.FullPath(a)));
        Assert.DoesNotContain('\\', a.FilePath);
        Assert.Equal(2, a.FileCount);
        Assert.NotEmpty(a.Sha256!);
        Assert.Contains("[[", hit.ContentSnippet);
        Assert.Contains(ArchiveHitIds(archives), id => id == a.NoticeId);
    }

    private static IEnumerable<string> ArchiveHitIds(ArchiveRepository r) => r.ArchivedNoticeIds();

    [Fact]
    public async Task Archive_search_scopes_filters_and_dates()
    {
        using var t = new TestDb();
        var a = N("A", "Rénovation de l'écluse n°4", "Voies navigables de France", "Travaux");
        var b = N("B", "Étude hydraulique du canal", "Région Grand Est", "Services", pi: true);
        b.PublicationDate = DateTime.UtcNow.AddDays(-200);
        t.Repo.BulkUpsert(new[] { a, b });
        var (collector, archives, _) = Setup(t);
        await collector.RunAsync(new List<DceProfile> { new() { Name = "Tout" } },
            new DceRunOptions { PublishedWithinDays = 365 }, null, CancellationToken.None);

        int Count(ArchiveFilters f) => archives.Search(f).Count;

        Assert.Equal(2, Count(new ArchiveFilters { Text = "bordereau" }));                            // contenu des deux archives
        Assert.Equal(0, Count(new ArchiveFilters { Text = "bordereau", Scope = ArchiveScope.Metadata }));
        Assert.Equal(2, Count(new ArchiveFilters { Text = "bpu", Scope = ArchiveScope.FileNames }));
        Assert.Equal(1, Count(new ArchiveFilters { Text = "hydraulique", Scope = ArchiveScope.Metadata }));
        Assert.Equal(1, Count(new ArchiveFilters { Text = "ecluse AND NOT hydraulique" }));
        Assert.Equal(1, Count(new ArchiveFilters { Buyer = "voies navigables" }));
        Assert.Equal(1, Count(new ArchiveFilters { MarketType = "PI" }));
        Assert.Equal(1, Count(new ArchiveFilters { MarketType = "Travaux" }));
        Assert.Equal(1, Count(new ArchiveFilters { Reference = "B" }));
        Assert.Equal(1, Count(new ArchiveFilters { PublishedFrom = DateTime.UtcNow.AddDays(-30) }));
        Assert.Equal(1, Count(new ArchiveFilters { PublishedTo = DateTime.UtcNow.AddDays(-100) }));
        Assert.Equal(2, Count(new ArchiveFilters { DownloadedFrom = DateTime.UtcNow.AddDays(-1), DownloadedTo = DateTime.UtcNow.AddDays(1) }));
        Assert.Equal(0, Count(new ArchiveFilters { DownloadedTo = DateTime.UtcNow.AddDays(-10) }));
        Assert.Equal(2, Count(new ArchiveFilters()));                                                   // sans critère : tout, quelle que soit la date de récupération
        Assert.Equal(2, archives.Count(new ArchiveFilters()));
        Assert.Equal(2, Count(new ArchiveFilters { Text = "ecluse AND" }));                             // requête mal formée : pas d'exception
    }

    [Fact]
    public async Task Collector_handles_restricted_failed_attempts_and_limits()
    {
        using var t = new TestDb();
        t.Repo.BulkUpsert(new[]
        {
            N("A", "Travaux 1", "Acheteur", "Travaux"), N("B", "Travaux 2", "Acheteur", "Travaux"), N("C", "Travaux 3", "Acheteur", "Travaux")
        });
        var all = new List<DceProfile> { new() { Name = "Tout" } };

        // limite par exécution
        var (c1, _, p1) = Setup(t, maxPerRun: 2);
        var s1 = await c1.RunAsync(all, new DceRunOptions(), null, CancellationToken.None);
        Assert.Equal(2, s1.Downloaded);
        Assert.Equal(1, s1.SkippedByLimit);
        var s1b = await c1.RunAsync(all, new DceRunOptions(), null, CancellationToken.None);
        Assert.Equal(1, s1b.Downloaded);                        // le reste est repris au lancement suivant
    }

    [Fact]
    public async Task Collector_retries_failures_up_to_max_attempts_and_blocked_only_on_request()
    {
        using var t = new TestDb();
        t.Repo.BulkUpsert(new[] { N("F", "Travaux en échec", "Acheteur", "Travaux"), N("R", "Travaux restreints", "Acheteur", "Travaux") });
        var (collector, archives, provider) = Setup(t, maxAttempts: 2);
        provider.Decide = n => n.Reference == "F" ? DceOutcome.Failed : DceOutcome.Restricted;
        var all = new List<DceProfile> { new() { Name = "Tout" } };

        var s1 = await collector.RunAsync(all, new DceRunOptions(), null, CancellationToken.None);
        Assert.Equal(1, s1.Failed);
        Assert.Equal(1, s1.Restricted);

        await collector.RunAsync(all, new DceRunOptions(), null, CancellationToken.None);   // F réessayé (tentative 2), R non
        Assert.Equal(3, provider.Calls);
        await collector.RunAsync(all, new DceRunOptions(), null, CancellationToken.None);   // F a atteint 2 tentatives
        Assert.Equal(3, provider.Calls);

        provider.Decide = _ => DceOutcome.Downloaded;
        var s4 = await collector.RunAsync(all, new DceRunOptions { RetryBlocked = true }, null, CancellationToken.None);
        Assert.Equal(1, s4.Downloaded);                         // R récupéré ; F reste abandonné
        var counts = archives.CountByStatus();
        Assert.Equal(1, counts.Ok);
        Assert.Equal(1, counts.Failed);
    }

    [Fact]
    public async Task Collector_without_active_profile_does_nothing()
    {
        using var t = new TestDb();
        t.Repo.BulkUpsert(new[] { N("A", "Travaux", "Acheteur", "Travaux") });
        var (collector, _, provider) = Setup(t);
        var s = await collector.RunAsync(new List<DceProfile> { new() { Name = "Off", Enabled = false } }, new DceRunOptions(), null, CancellationToken.None);
        Assert.Equal(0, provider.Calls);
        Assert.NotEmpty(s.Messages);
    }

    [Fact]
    public async Task Single_download_marks_unsupported_when_no_connector_matches()
    {
        using var t = new TestDb();
        var archives = new ArchiveRepository(t.Db);
        var collector = new DceCollector(archives, Array.Empty<IDceDownloadProvider>(),
            new DceSettings { Folder = Path.Combine(t.Dir, "DCE"), DelayMs = 0 }, t.Dir);
        var n = N("A", "Travaux", "Acheteur", "Travaux");
        t.Repo.BulkUpsert(new[] { n });
        var a = await collector.DownloadSingleAsync(n, false, CancellationToken.None);
        Assert.Equal(DceStatus.Unsupported, a.Status);
    }

    // ------------------------------------------------------------------ CLI
    [Fact]
    public async Task Cli_dce_only_downloads_everything_with_dce_all()
    {
        using var t = new TestDb();
        t.Repo.BulkUpsert(new[]
        {
            N("A", "Travaux 1", "Acheteur", "Travaux"), N("B", "Fourniture 2", "Autre", "Fournitures")
        });
        File.WriteAllText(Path.Combine(t.Dir, "connectors.json"),
            ConnectorsConfig.DefaultJson.Replace("\"delayMs\": 1500", "\"delayMs\": 0"));
        var provider = new FakeDceProvider();

        var output = new StringWriter();
        var code = await CliRunner.RunAsync(new[] { "--dce-only", "--dce-all" }, output, t.Dir,
            dceProviderFactory: _ => new IDceDownloadProvider[] { provider });
        Assert.Equal(0, code);
        Assert.Contains("téléchargés=2", output.ToString());
        Assert.Equal(2, provider.Calls);

        // deuxième exécution : rien de nouveau
        var out2 = new StringWriter();
        Assert.Equal(0, await CliRunner.RunAsync(new[] { "--dce-only", "--dce-all" }, out2, t.Dir,
            dceProviderFactory: _ => new IDceDownloadProvider[] { provider }));
        Assert.Contains("téléchargés=0", out2.ToString());
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Cli_dce_without_profile_explains_how_to_proceed()
    {
        using var t = new TestDb();
        var provider = new FakeDceProvider();
        var output = new StringWriter();
        var code = await CliRunner.RunAsync(new[] { "--dce-only" }, output, t.Dir,
            dceProviderFactory: _ => new IDceDownloadProvider[] { provider });
        Assert.Equal(0, code);
        Assert.Contains("Aucun profil", output.ToString());
        Assert.Equal(0, provider.Calls);

        Assert.Equal(2, await CliRunner.RunAsync(new[] { "--dce-only", "--dce-days=abc" }, new StringWriter(), t.Dir));
        Assert.True(CliRunner.IsCliInvocation(new[] { "--dce-only" }));
    }
}
