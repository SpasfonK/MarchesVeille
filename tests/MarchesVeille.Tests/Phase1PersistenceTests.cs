using System.Diagnostics;
using MarchesVeille.Core.Data;
using MarchesVeille.Core.Models;

namespace MarchesVeille.Tests;

public class Phase1PersistenceTests
{
    [Fact]
    public void Migration_creates_schema_and_is_idempotent()
    {
        using var t = new TestDb();
        t.Db.Migrate(); // deuxième appel : sans effet
        using var conn = t.Db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','trigger') ORDER BY name";
        var names = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
        Assert.Contains("notices", names);
        Assert.Contains("notices_fts", names);
        Assert.Contains("sources", names);
        Assert.Contains("saved_searches", names);
        Assert.Contains("notices_ai", names);
        Assert.Contains("notices_ad", names);
        Assert.Contains("notices_au", names);
        Assert.Equal(3, t.Repo.ListSources().Count);
    }

    [Fact]
    public void Bulk_insert_10000_notices_and_fulltext_search_under_10ms()
    {
        using var t = new TestDb();
        var data = SampleData.Generate(10_000);

        var res = t.Repo.BulkUpsert(data);
        Assert.Equal(10_000, res.Inserted);
        Assert.Equal(0, res.Updated);

        var f = new SearchFilters { Text = "écluse travaux", Limit = 50, OnlyActive = false };
        Assert.NotEmpty(t.Repo.Search(f)); // échauffement

        var times = new List<double>();
        for (int i = 0; i < 25; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var hits = t.Repo.Search(f);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Assert.NotEmpty(hits);
        }
        times.Sort();
        var median = times[times.Count / 2];
        Assert.True(median < 10.0, $"Médiane de recherche FTS5 = {median:F2} ms (objectif < 10 ms)");
    }

    [Fact]
    public void Search_is_accent_and_case_insensitive_and_supports_prefix()
    {
        using var t = new TestDb();
        t.Repo.BulkUpsert(new[] { SampleData.One("A1", "Réfection de l'ÉCLUSE n°12") });

        foreach (var q in new[] { "ecluse", "ÉCLUSE", "refection", "ECLUSE", "eclu" })
            Assert.Single(t.Repo.Search(new SearchFilters { Text = q }));
        Assert.Empty(t.Repo.Search(new SearchFilters { Text = "barrage" }));
    }

    [Fact]
    public void Search_supports_boolean_operators_and_phrases()
    {
        using var t = new TestDb();
        t.Repo.BulkUpsert(new[]
        {
            SampleData.One("1", "Travaux écluse du canal de la Marne au Rhin"),
            SampleData.One("2", "Travaux barrage et écluse"),
            SampleData.One("3", "Fourniture de papier")
        });

        Assert.Equal(2, t.Repo.Search(new SearchFilters { Text = "travaux AND ecluse" }).Count);
        Assert.Single(t.Repo.Search(new SearchFilters { Text = "ecluse AND NOT barrage" }));
        Assert.Equal(3, t.Repo.Search(new SearchFilters { Text = "papier OR travaux" }).Count);
        Assert.Single(t.Repo.Search(new SearchFilters { Text = "\"canal de la marne\"" }));
        Assert.Empty(t.Repo.Search(new SearchFilters { Text = "\"marne canal\"" }));
    }

    [Fact]
    public void Malformed_query_never_throws()
    {
        using var t = new TestDb();
        t.Repo.BulkUpsert(new[] { SampleData.One("1", "Travaux écluse") });
        foreach (var q in new[] { "NOT travaux", "travaux AND", "\"travaux", "((", "écluse -", "*", "OR OR" })
            _ = t.Repo.Search(new SearchFilters { Text = q });
    }

    [Fact]
    public void Results_expose_highlighted_title()
    {
        using var t = new TestDb();
        t.Repo.BulkUpsert(new[] { SampleData.One("1", "Travaux de l'écluse de Nancy", description: "Rénovation complète de l'écluse") });
        var hit = Assert.Single(t.Repo.Search(new SearchFilters { Text = "ecluse" }));
        Assert.Contains("[[", hit.TitleHighlight);
        Assert.Contains("]]", hit.TitleHighlight);
        Assert.Contains("[[", hit.DescriptionHighlight);
    }

    [Fact]
    public void Upsert_updates_fulltext_index_and_keeps_user_flags()
    {
        using var t = new TestDb();
        var n = SampleData.One("R1", "Ancien titre dragage");
        t.Repo.BulkUpsert(new[] { n });
        var id = Notice.ComputeId("test", "R1");
        t.Repo.SetFavorite(id, true);
        t.Repo.SetRead(id, true);

        var res = t.Repo.BulkUpsert(new[] { SampleData.One("R1", "Nouveau titre curage") });
        Assert.Equal(0, res.Inserted);
        Assert.Equal(1, res.Updated);

        Assert.Empty(t.Repo.Search(new SearchFilters { Text = "dragage" }));
        var hit = Assert.Single(t.Repo.Search(new SearchFilters { Text = "curage" }));
        Assert.True(hit.Notice.IsFavorite);
        Assert.True(hit.Notice.IsRead);
    }

    [Fact]
    public void Filters_combine_with_fulltext()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        var a = SampleData.One("1", "Travaux écluse", "VNF - DT Nord-Est");
        a.Region = "Grand Est"; a.BuyerCategory = "VNF-DTNE"; a.DeadlineDate = now.AddDays(5);
        var b = SampleData.One("2", "Travaux écluse", "Commune de Paris");
        b.Region = null; b.DeadlineDate = now.AddDays(20);
        var c = SampleData.One("3", "Travaux écluse", "VNF - DT Nord-Est");
        c.Region = "Grand Est"; c.BuyerCategory = "VNF-DTNE"; c.DeadlineDate = now.AddDays(-3);
        t.Repo.BulkUpsert(new[] { a, b, c });

        Assert.Equal(3, t.Repo.Count(new SearchFilters { Text = "ecluse" }));
        Assert.Equal(2, t.Repo.Count(new SearchFilters { Text = "ecluse", OnlyActive = true }));
        Assert.Equal(2, t.Repo.Count(new SearchFilters { Text = "ecluse", Region = "Grand Est" }));
        Assert.Equal(2, t.Repo.Count(new SearchFilters { BuyerCategory = "VNF-DTNE" }));
        Assert.Equal(1, t.Repo.Count(new SearchFilters { Text = "ecluse", Buyer = "paris" }));
        Assert.Equal(1, t.Repo.Count(new SearchFilters { OnlyActive = true, BuyerCategory = "VNF-DTNE" }));
        Assert.Equal(1, t.Repo.Count(new SearchFilters { DeadlineWithinDays = 7 }));
        Assert.Equal(3, t.Repo.Search(new SearchFilters { Sort = SortMode.DeadlineAsc }).Count);
    }

    [Fact]
    public void Unread_and_favorite_filters_work()
    {
        using var t = new TestDb();
        t.Repo.BulkUpsert(new[] { SampleData.One("1", "Un"), SampleData.One("2", "Deux") });
        t.Repo.SetRead(Notice.ComputeId("test", "1"), true);
        t.Repo.SetFavorite(Notice.ComputeId("test", "2"), true);
        Assert.Equal(1, t.Repo.Count(new SearchFilters { OnlyUnread = true }));
        Assert.Equal(1, t.Repo.Count(new SearchFilters { OnlyFavorites = true }));
    }

    [Fact]
    public void Saved_searches_roundtrip_and_count_new_notices()
    {
        using var t = new TestDb();
        var created = DateTime.UtcNow.AddMinutes(-5);
        var id = t.Repo.SaveSearch(new SavedSearch
        {
            Name = "VNF", Filters = new SearchFilters { BuyerCategory = "VNF-DTNE", Text = "ecluse" },
            CreatedAt = created, LastRunAt = created
        });
        var list = t.Repo.ListSavedSearches();
        var s = Assert.Single(list);
        Assert.Equal(id, s.Id);
        Assert.Equal("VNF-DTNE", s.Filters.BuyerCategory);
        Assert.Equal(0, t.Repo.CountNew(s));

        var n = SampleData.One("9", "Travaux écluse");
        n.BuyerCategory = "VNF-DTNE";
        t.Repo.BulkUpsert(new[] { n });
        Assert.Equal(1, t.Repo.CountNew(s));

        t.Repo.DeleteSavedSearch(id);
        Assert.Empty(t.Repo.ListSavedSearches());
    }

    [Fact]
    public void Query_builder_produces_expected_fts_syntax()
    {
        Assert.Null(FtsQueryBuilder.Build("   "));
        Assert.Equal("\"ecl\"*", FtsQueryBuilder.Build("ecl"));
        Assert.Equal("\"a\"* NOT \"b\"*", FtsQueryBuilder.Build("a AND NOT b"));
        Assert.Equal("\"canal de la marne\"", FtsQueryBuilder.Build("\"canal de la marne\""));
        Assert.Equal("\"Nord Est\"*", FtsQueryBuilder.Build("Nord-Est"));
    }
}
