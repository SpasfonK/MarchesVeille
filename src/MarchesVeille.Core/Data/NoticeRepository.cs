using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MarchesVeille.Core.Models;

namespace MarchesVeille.Core.Data;

public sealed class NoticeRepository
{
    private readonly Database _db;
    public NoticeRepository(Database db) => _db = db;

    // ------------------------------------------------------------------ dates
    public static string Iso(DateTime d)
    {
        if (d.Kind == DateTimeKind.Unspecified) d = DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    internal static string? Iso(DateTime? d) => d.HasValue ? Iso(d.Value) : null;

    internal static DateTime? ParseIso(string? s) =>
        !string.IsNullOrEmpty(s) &&
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    internal static object Db(string? s) => (object?)s ?? DBNull.Value;

    // ------------------------------------------------------------ bulk upsert
    private const string UpsertSql = """
INSERT INTO notices (id, source_id, reference, title, description, buyer_name, buyer_siret, department, region,
                     buyer_category, market_type, is_pi, publication_date, deadline_date, url_notice, url_dce, created_at)
VALUES ($id, $source_id, $reference, $title, $description, $buyer_name, $buyer_siret, $department, $region,
        $buyer_category, $market_type, $is_pi, $publication_date, $deadline_date, $url_notice, $url_dce, $created_at)
ON CONFLICT(id) DO UPDATE SET
    title            = excluded.title,
    description      = excluded.description,
    buyer_name       = excluded.buyer_name,
    buyer_siret      = COALESCE(excluded.buyer_siret, buyer_siret),
    department       = COALESCE(excluded.department, department),
    region           = COALESCE(excluded.region, region),
    buyer_category   = excluded.buyer_category,
    market_type      = COALESCE(excluded.market_type, market_type),
    is_pi            = excluded.is_pi,
    publication_date = COALESCE(excluded.publication_date, publication_date),
    deadline_date    = COALESCE(excluded.deadline_date, deadline_date),
    url_notice       = COALESCE(excluded.url_notice, url_notice),
    url_dce          = COALESCE(excluded.url_dce, url_dce);
""";

    private static readonly string[] UpsertParams =
    {
        "$id", "$source_id", "$reference", "$title", "$description", "$buyer_name", "$buyer_siret", "$department",
        "$region", "$buyer_category", "$market_type", "$is_pi", "$publication_date", "$deadline_date", "$url_notice", "$url_dce", "$created_at"
    };

    /// <summary>Insertion en masse dans une transaction. Les favoris / lus des avis existants sont conservés.</summary>
    public UpsertResult BulkUpsert(IEnumerable<Notice> notices)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        using var exists = conn.CreateCommand();
        exists.Transaction = tx;
        exists.CommandText = "SELECT 1 FROM notices WHERE id = $id";
        var pExists = exists.Parameters.Add("$id", SqliteType.Text);

        using var up = conn.CreateCommand();
        up.Transaction = tx;
        up.CommandText = UpsertSql;
        var p = UpsertParams.ToDictionary(n => n, n => up.Parameters.Add(n, n == "$is_pi" ? SqliteType.Integer : SqliteType.Text));

        int inserted = 0, updated = 0;
        foreach (var n in notices)
        {
            n.EnsureId();
            pExists.Value = n.Id;
            var found = exists.ExecuteScalar() != null;

            p["$id"].Value = n.Id;
            p["$source_id"].Value = n.SourceId;
            p["$reference"].Value = n.Reference;
            p["$title"].Value = n.Title ?? "";
            p["$description"].Value = n.Description ?? "";
            p["$buyer_name"].Value = n.BuyerName ?? "";
            p["$buyer_siret"].Value = Db(n.BuyerSiret);
            p["$department"].Value = Db(n.Department);
            p["$region"].Value = Db(n.Region);
            p["$buyer_category"].Value = Db(n.BuyerCategory);
            p["$market_type"].Value = Db(n.MarketType);
            p["$is_pi"].Value = n.IsPi ? 1 : 0;
            p["$publication_date"].Value = Db(Iso(n.PublicationDate));
            p["$deadline_date"].Value = Db(Iso(n.DeadlineDate));
            p["$url_notice"].Value = Db(n.UrlNotice);
            p["$url_dce"].Value = Db(n.UrlDce);
            p["$created_at"].Value = Iso(n.CreatedAt);
            up.ExecuteNonQuery();

            if (found) updated++; else inserted++;
        }
        tx.Commit();
        return new UpsertResult(inserted, updated);
    }

    // ----------------------------------------------------------------- search
    public IReadOnlyList<SearchResult> Search(SearchFilters f) =>
        WithFts(f, (conn, fts) => QueryResults(conn, f, fts), (IReadOnlyList<SearchResult>)Array.Empty<SearchResult>());

    public int Count(SearchFilters f) =>
        WithFts(f, (conn, fts) =>
        {
            using var cmd = conn.CreateCommand();
            var from = BuildFrom(f, cmd, fts);
            cmd.CommandText = "SELECT COUNT(*)" + from;
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, 0);

    public Notice? Get(string id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM notices WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadNotice(r) : null;
    }

    private T WithFts<T>(SearchFilters f, Func<SqliteConnection, string?, T> run, T empty)
    {
        using var conn = _db.Open();
        var fts = FtsQueryBuilder.Build(f.Text);
        try { return run(conn, fts); }
        catch (SqliteException) when (fts != null)
        {
            var safe = FtsQueryBuilder.BuildSafe(f.Text);
            return safe == null ? empty : run(conn, safe);
        }
    }

    private static IReadOnlyList<SearchResult> QueryResults(SqliteConnection conn, SearchFilters f, string? fts)
    {
        using var cmd = conn.CreateCommand();
        var from = BuildFrom(f, cmd, fts);
        var select = fts != null
            ? "SELECT n.*, bm25(notices_fts, 10.0, 2.0, 3.0, 5.0) AS score, " +
              "highlight(notices_fts, 0, '[[', ']]') AS t_hl, " +
              "snippet(notices_fts, 1, '[[', ']]', '…', 40) AS d_hl"
            : "SELECT n.*, 0.0 AS score, n.title AS t_hl, substr(n.description, 1, 400) AS d_hl";

        var order = f.Sort switch
        {
            SortMode.DeadlineAsc => " ORDER BY (n.deadline_date IS NULL), n.deadline_date ASC, n.rid DESC",
            SortMode.PublicationDesc => " ORDER BY n.publication_date DESC, n.rid DESC",
            _ => fts != null ? " ORDER BY score, n.rid DESC" : " ORDER BY n.publication_date DESC, n.rid DESC"
        };

        cmd.CommandText = select + from + order + " LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(f.Limit, 1, 5000));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, f.Offset));

        var list = new List<SearchResult>();
        using var r = cmd.ExecuteReader();
        int iScore = r.GetOrdinal("score"), iT = r.GetOrdinal("t_hl"), iD = r.GetOrdinal("d_hl");
        while (r.Read())
        {
            var n = ReadNotice(r);
            list.Add(new SearchResult(n, r.GetDouble(iScore),
                r.IsDBNull(iT) ? n.Title : r.GetString(iT),
                r.IsDBNull(iD) ? "" : r.GetString(iD)));
        }
        return list;
    }

    private static string BuildFrom(SearchFilters f, SqliteCommand cmd, string? fts)
    {
        var sb = new StringBuilder();
        if (fts != null)
        {
            sb.Append(" FROM notices_fts CROSS JOIN notices n ON n.rid = notices_fts.rowid WHERE notices_fts MATCH $q");
            cmd.Parameters.AddWithValue("$q", fts);
        }
        else sb.Append(" FROM notices n WHERE 1=1");

        var now = Iso(f.Now ?? DateTime.UtcNow);

        if (!string.IsNullOrWhiteSpace(f.Buyer))
        {
            sb.Append(" AND n.buyer_name LIKE $buyer ESCAPE '\\'");
            var esc = f.Buyer.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$buyer", $"%{esc}%");
        }
        if (!string.IsNullOrWhiteSpace(f.Region)) { sb.Append(" AND n.region = $region"); cmd.Parameters.AddWithValue("$region", f.Region); }
        if (!string.IsNullOrWhiteSpace(f.BuyerCategory)) { sb.Append(" AND n.buyer_category = $cat"); cmd.Parameters.AddWithValue("$cat", f.BuyerCategory); }
        if (!string.IsNullOrWhiteSpace(f.MarketType))
        {
            if (f.MarketType.Equals("PI", StringComparison.OrdinalIgnoreCase)) sb.Append(" AND n.is_pi = 1");
            else { sb.Append(" AND n.market_type = $mt"); cmd.Parameters.AddWithValue("$mt", f.MarketType); }
        }
        if (!string.IsNullOrWhiteSpace(f.SourceId)) { sb.Append(" AND n.source_id = $src"); cmd.Parameters.AddWithValue("$src", f.SourceId); }

        if (f.DeadlineWithinDays is int days && days > 0)
        {
            sb.Append(" AND n.deadline_date >= $now AND n.deadline_date <= $limitDate");
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$limitDate", Iso((f.Now ?? DateTime.UtcNow).AddDays(days)));
        }
        else if (f.OnlyActive)
        {
            sb.Append(" AND n.deadline_date >= $now");
            cmd.Parameters.AddWithValue("$now", now);
        }

        if (f.OnlyUnread) sb.Append(" AND n.is_read = 0");
        if (f.OnlyFavorites) sb.Append(" AND n.is_favorite = 1");
        if (f.CreatedAfter is DateTime ca) { sb.Append(" AND n.created_at > $createdAfter"); cmd.Parameters.AddWithValue("$createdAfter", Iso(ca)); }
        return sb.ToString();
    }

    internal static Notice ReadNotice(SqliteDataReader r)
    {
        string? Str(string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? null : r.GetString(i); }
        bool Flag(string c) => r.GetInt32(r.GetOrdinal(c)) == 1;
        return new Notice
        {
            Id = Str("id")!,
            SourceId = Str("source_id") ?? "",
            Reference = Str("reference") ?? "",
            Title = Str("title") ?? "",
            Description = Str("description") ?? "",
            BuyerName = Str("buyer_name") ?? "",
            BuyerSiret = Str("buyer_siret"),
            Department = Str("department"),
            Region = Str("region"),
            BuyerCategory = Str("buyer_category"),
            MarketType = Str("market_type"),
            IsPi = Flag("is_pi"),
            PublicationDate = ParseIso(Str("publication_date")),
            DeadlineDate = ParseIso(Str("deadline_date")),
            UrlNotice = Str("url_notice"),
            UrlDce = Str("url_dce"),
            IsFavorite = Flag("is_favorite"),
            IsRead = Flag("is_read"),
            CreatedAt = ParseIso(Str("created_at")) ?? DateTime.UtcNow
        };
    }

    // ------------------------------------------------------------ flags
    public void SetFavorite(string id, bool value) => SetFlag(id, "is_favorite", value);
    public void SetRead(string id, bool value) => SetFlag(id, "is_read", value);

    private void SetFlag(string id, string column, bool value)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE notices SET {column} = $v WHERE id = $id";
        cmd.Parameters.AddWithValue("$v", value ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ sources
    public void EnsureSource(string id, string name, string type, string? baseUrl)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
INSERT INTO sources (id, name, type, base_url) VALUES ($id, $name, $type, $url)
ON CONFLICT(id) DO UPDATE SET name = excluded.name, type = excluded.type, base_url = excluded.base_url;
""";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$url", Db(baseUrl));
        cmd.ExecuteNonQuery();
    }

    public void MarkSynced(string id, DateTime when)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sources SET last_synced_at = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", Iso(when));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SourceInfo> ListSources()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, type, base_url, is_active, last_synced_at FROM sources ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<SourceInfo>();
        while (r.Read())
            list.Add(new SourceInfo(r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4) == 1,
                ParseIso(r.IsDBNull(5) ? null : r.GetString(5))));
        return list;
    }

    // ------------------------------------------------------ saved searches
    public long SaveSearch(SavedSearch s)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
INSERT INTO saved_searches (name, filters_json, alert_enabled, created_at, last_run_at)
VALUES ($name, $json, $alert, $created, $last);
""";
        cmd.Parameters.AddWithValue("$name", s.Name);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(s.Filters));
        cmd.Parameters.AddWithValue("$alert", s.AlertEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", Iso(s.CreatedAt));
        cmd.Parameters.AddWithValue("$last", Db(Iso(s.LastRunAt)));
        cmd.ExecuteNonQuery();
        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(idCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<SavedSearch> ListSavedSearches()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, filters_json, alert_enabled, created_at, last_run_at FROM saved_searches ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<SavedSearch>();
        while (r.Read())
        {
            SearchFilters filters;
            try { filters = JsonSerializer.Deserialize<SearchFilters>(r.GetString(2)) ?? new(); }
            catch (JsonException) { filters = new(); }
            list.Add(new SavedSearch
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                Filters = filters,
                AlertEnabled = r.GetInt32(3) == 1,
                CreatedAt = ParseIso(r.GetString(4)) ?? DateTime.UtcNow,
                LastRunAt = ParseIso(r.IsDBNull(5) ? null : r.GetString(5))
            });
        }
        return list;
    }

    public void DeleteSavedSearch(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM saved_searches WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void TouchSavedSearch(long id, DateTime when)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE saved_searches SET last_run_at = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", Iso(when));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Nombre d'avis correspondant à la veille et ajoutés depuis sa dernière consultation.</summary>
    public int CountNew(SavedSearch s)
    {
        var f = s.Filters.Clone();
        f.CreatedAfter = s.LastRunAt ?? s.CreatedAt;
        return Count(f);
    }
}
