using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using MarchesVeille.Core.Models;

namespace MarchesVeille.Core.Data;

/// <summary>Archives de DCE (table dce_archives + index plein texte dce_fts) et profils de collecte.</summary>
public sealed class ArchiveRepository
{
    private readonly Database _db;
    public ArchiveRepository(Database db) => _db = db;

    private static string? Iso(DateTime? d) => NoticeRepository.Iso(d);
    private static DateTime? ParseIso(string? s) => NoticeRepository.ParseIso(s);
    private static object Db(string? s) => NoticeRepository.Db(s);

    // ------------------------------------------------------------ écriture
    /// <summary>Enregistre une tentative ou une archive ; incrémente le nombre de tentatives.</summary>
    public void Save(DceArchive a)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
INSERT INTO dce_archives (notice_id, source_id, reference, title, buyer_name, region, department, market_type, is_pi,
    publication_date, deadline_date, url_notice, status, error, attempts, file_name, file_path, size_bytes, sha256,
    file_list, content_text, downloaded_at, last_attempt_at)
VALUES ($notice_id, $source_id, $reference, $title, $buyer_name, $region, $department, $market_type, $is_pi,
    $publication_date, $deadline_date, $url_notice, $status, $error, 1, $file_name, $file_path, $size_bytes, $sha256,
    $file_list, $content_text, $downloaded_at, $last_attempt_at)
ON CONFLICT(notice_id) DO UPDATE SET
    source_id = excluded.source_id, reference = excluded.reference, title = excluded.title, buyer_name = excluded.buyer_name,
    region = excluded.region, department = excluded.department, market_type = excluded.market_type, is_pi = excluded.is_pi,
    publication_date = excluded.publication_date, deadline_date = excluded.deadline_date, url_notice = excluded.url_notice,
    status = excluded.status, error = excluded.error, attempts = dce_archives.attempts + 1,
    file_name = excluded.file_name, file_path = excluded.file_path, size_bytes = excluded.size_bytes, sha256 = excluded.sha256,
    file_list = excluded.file_list, content_text = excluded.content_text,
    downloaded_at = COALESCE(excluded.downloaded_at, dce_archives.downloaded_at), last_attempt_at = excluded.last_attempt_at;
""";
        cmd.Parameters.AddWithValue("$notice_id", a.NoticeId);
        cmd.Parameters.AddWithValue("$source_id", a.SourceId);
        cmd.Parameters.AddWithValue("$reference", a.Reference);
        cmd.Parameters.AddWithValue("$title", a.Title);
        cmd.Parameters.AddWithValue("$buyer_name", a.BuyerName);
        cmd.Parameters.AddWithValue("$region", Db(a.Region));
        cmd.Parameters.AddWithValue("$department", Db(a.Department));
        cmd.Parameters.AddWithValue("$market_type", Db(a.MarketType));
        cmd.Parameters.AddWithValue("$is_pi", a.IsPi ? 1 : 0);
        cmd.Parameters.AddWithValue("$publication_date", Db(Iso(a.PublicationDate)));
        cmd.Parameters.AddWithValue("$deadline_date", Db(Iso(a.DeadlineDate)));
        cmd.Parameters.AddWithValue("$url_notice", Db(a.UrlNotice));
        cmd.Parameters.AddWithValue("$status", a.Status);
        cmd.Parameters.AddWithValue("$error", Db(a.Error));
        cmd.Parameters.AddWithValue("$file_name", a.FileName);
        cmd.Parameters.AddWithValue("$file_path", a.FilePath);
        cmd.Parameters.AddWithValue("$size_bytes", a.SizeBytes);
        cmd.Parameters.AddWithValue("$sha256", Db(a.Sha256));
        cmd.Parameters.AddWithValue("$file_list", a.FileList);
        cmd.Parameters.AddWithValue("$content_text", a.ContentText);
        cmd.Parameters.AddWithValue("$downloaded_at", Db(Iso(a.DownloadedAt)));
        cmd.Parameters.AddWithValue("$last_attempt_at", NoticeRepository.Iso(a.LastAttemptAt));
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dce_archives WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ candidats
    /// <summary>Avis sans archive, ou dont la tentative a échoué (&lt; maxAttempts), ou bloqués si retryBlocked.</summary>
    public IReadOnlyList<Notice> ListCandidates(DateTime publishedSince, int maxAttempts, bool retryBlocked)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
SELECT n.* FROM notices n LEFT JOIN dce_archives a ON a.notice_id = n.id
WHERE (n.publication_date IS NULL OR n.publication_date >= $since)
  AND (a.id IS NULL
       OR (a.status = 'failed' AND a.attempts < $max)
       OR ($retry = 1 AND a.status IN ('restricted', 'unsupported')))
ORDER BY n.publication_date DESC, n.rid DESC;
""";
        cmd.Parameters.AddWithValue("$since", Iso(publishedSince));
        cmd.Parameters.AddWithValue("$max", maxAttempts);
        cmd.Parameters.AddWithValue("$retry", retryBlocked ? 1 : 0);
        var list = new List<Notice>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(NoticeRepository.ReadNotice(r));
        return list;
    }

    public HashSet<string> ArchivedNoticeIds()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT notice_id FROM dce_archives WHERE status = 'ok'";
        var set = new HashSet<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public (int Ok, int Failed, int Restricted, int Unsupported) CountByStatus()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, COUNT(*) FROM dce_archives GROUP BY status";
        int ok = 0, failed = 0, restricted = 0, unsupported = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var n = r.GetInt32(1);
            switch (r.GetString(0))
            {
                case DceStatus.Ok: ok = n; break;
                case DceStatus.Failed: failed = n; break;
                case DceStatus.Restricted: restricted = n; break;
                case DceStatus.Unsupported: unsupported = n; break;
            }
        }
        return (ok, failed, restricted, unsupported);
    }

    // ------------------------------------------------------------ recherche
    public IReadOnlyList<ArchiveHit> Search(ArchiveFilters f) =>
        WithFts(f, (conn, fts) => QueryHits(conn, f, fts), (IReadOnlyList<ArchiveHit>)Array.Empty<ArchiveHit>());

    public int Count(ArchiveFilters f) =>
        WithFts(f, (conn, fts) =>
        {
            using var cmd = conn.CreateCommand();
            var from = BuildFrom(f, cmd, fts);
            cmd.CommandText = "SELECT COUNT(*)" + from;
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, 0);

    public DceArchive? GetByNotice(string noticeId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM dce_archives WHERE notice_id = $id";
        cmd.Parameters.AddWithValue("$id", noticeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadArchive(r) : null;
    }

    private T WithFts<T>(ArchiveFilters f, Func<SqliteConnection, string?, T> run, T empty)
    {
        using var conn = _db.Open();
        var core = FtsQueryBuilder.Build(f.Text);
        var fts = core == null ? null : WrapScope(core, f.Scope);
        try { return run(conn, fts); }
        catch (SqliteException) when (fts != null)
        {
            var safe = FtsQueryBuilder.BuildSafe(f.Text);
            return safe == null ? empty : run(conn, WrapScope(safe, f.Scope));
        }
    }

    private static string WrapScope(string q, ArchiveScope scope) => scope switch
    {
        ArchiveScope.Metadata => $"{{title buyer_name reference}} : ({q})",
        ArchiveScope.FileNames => $"{{file_list}} : ({q})",
        ArchiveScope.Content => $"{{content_text}} : ({q})",
        _ => q
    };

    private static IReadOnlyList<ArchiveHit> QueryHits(SqliteConnection conn, ArchiveFilters f, string? fts)
    {
        using var cmd = conn.CreateCommand();
        var from = BuildFrom(f, cmd, fts);
        var select = fts != null
            ? "SELECT a.*, bm25(dce_fts, 10.0, 6.0, 8.0, 3.0, 1.0) AS score, " +
              "highlight(dce_fts, 0, '[[', ']]') AS t_hl, " +
              "snippet(dce_fts, 4, '[[', ']]', '…', 40) AS c_hl, " +
              "snippet(dce_fts, 3, '[[', ']]', '…', 20) AS f_hl"
            : "SELECT a.*, 0.0 AS score, a.title AS t_hl, '' AS c_hl, '' AS f_hl";
        var order = f.Sort switch
        {
            ArchiveSort.DownloadedDesc => " ORDER BY a.downloaded_at DESC, a.id DESC",
            ArchiveSort.PublishedDesc => " ORDER BY a.publication_date DESC, a.id DESC",
            _ => fts != null ? " ORDER BY score, a.id DESC" : " ORDER BY a.publication_date DESC, a.id DESC"
        };
        cmd.CommandText = select + from + order + " LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(f.Limit, 1, 5000));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, f.Offset));

        var list = new List<ArchiveHit>();
        using var r = cmd.ExecuteReader();
        int iS = r.GetOrdinal("score"), iT = r.GetOrdinal("t_hl"), iC = r.GetOrdinal("c_hl"), iF = r.GetOrdinal("f_hl");
        while (r.Read())
        {
            var a = ReadArchive(r);
            list.Add(new ArchiveHit(a, r.GetDouble(iS),
                r.IsDBNull(iT) ? a.Title : r.GetString(iT),
                r.IsDBNull(iC) ? "" : r.GetString(iC),
                r.IsDBNull(iF) ? "" : r.GetString(iF)));
        }
        return list;
    }

    private static string BuildFrom(ArchiveFilters f, SqliteCommand cmd, string? fts)
    {
        var sb = new StringBuilder();
        if (fts != null)
        {
            sb.Append(" FROM dce_fts CROSS JOIN dce_archives a ON a.id = dce_fts.rowid WHERE dce_fts MATCH $q");
            cmd.Parameters.AddWithValue("$q", fts);
        }
        else sb.Append(" FROM dce_archives a WHERE 1=1");

        if (!string.IsNullOrWhiteSpace(f.Status)) { sb.Append(" AND a.status = $status"); cmd.Parameters.AddWithValue("$status", f.Status); }
        if (!string.IsNullOrWhiteSpace(f.Buyer)) { sb.Append(" AND a.buyer_name LIKE $buyer ESCAPE '\\'"); cmd.Parameters.AddWithValue("$buyer", $"%{Esc(f.Buyer)}%"); }
        if (!string.IsNullOrWhiteSpace(f.Reference)) { sb.Append(" AND a.reference LIKE $ref ESCAPE '\\'"); cmd.Parameters.AddWithValue("$ref", $"%{Esc(f.Reference)}%"); }
        if (!string.IsNullOrWhiteSpace(f.Region)) { sb.Append(" AND a.region = $region"); cmd.Parameters.AddWithValue("$region", f.Region); }
        if (!string.IsNullOrWhiteSpace(f.MarketType))
        {
            if (f.MarketType.Equals("PI", StringComparison.OrdinalIgnoreCase)) sb.Append(" AND a.is_pi = 1");
            else { sb.Append(" AND a.market_type = $mt"); cmd.Parameters.AddWithValue("$mt", f.MarketType); }
        }
        if (f.PublishedFrom is DateTime pf) { sb.Append(" AND a.publication_date >= $pf"); cmd.Parameters.AddWithValue("$pf", Iso(pf.Date)); }
        if (f.PublishedTo is DateTime pt) { sb.Append(" AND a.publication_date < $pt"); cmd.Parameters.AddWithValue("$pt", Iso(pt.Date.AddDays(1))); }
        if (f.DownloadedFrom is DateTime df) { sb.Append(" AND a.downloaded_at >= $df"); cmd.Parameters.AddWithValue("$df", Iso(df.Date)); }
        if (f.DownloadedTo is DateTime dt) { sb.Append(" AND a.downloaded_at < $dt"); cmd.Parameters.AddWithValue("$dt", Iso(dt.Date.AddDays(1))); }
        return sb.ToString();
    }

    private static string Esc(string s) => s.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static DceArchive ReadArchive(SqliteDataReader r)
    {
        string? Str(string c) { var i = r.GetOrdinal(c); return r.IsDBNull(i) ? null : r.GetString(i); }
        long Lng(string c) => r.GetInt64(r.GetOrdinal(c));
        return new DceArchive
        {
            Id = Lng("id"),
            NoticeId = Str("notice_id") ?? "",
            SourceId = Str("source_id") ?? "",
            Reference = Str("reference") ?? "",
            Title = Str("title") ?? "",
            BuyerName = Str("buyer_name") ?? "",
            Region = Str("region"),
            Department = Str("department"),
            MarketType = Str("market_type"),
            IsPi = Lng("is_pi") == 1,
            PublicationDate = ParseIso(Str("publication_date")),
            DeadlineDate = ParseIso(Str("deadline_date")),
            UrlNotice = Str("url_notice"),
            Status = Str("status") ?? DceStatus.Failed,
            Error = Str("error"),
            Attempts = (int)Lng("attempts"),
            FileName = Str("file_name") ?? "",
            FilePath = Str("file_path") ?? "",
            SizeBytes = Lng("size_bytes"),
            Sha256 = Str("sha256"),
            FileList = Str("file_list") ?? "",
            ContentText = Str("content_text") ?? "",
            DownloadedAt = ParseIso(Str("downloaded_at")),
            LastAttemptAt = ParseIso(Str("last_attempt_at")) ?? DateTime.UtcNow
        };
    }

    // ------------------------------------------------------------ profils de collecte
    public IReadOnlyList<DceProfile> ListProfiles()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, enabled, json FROM dce_profiles ORDER BY name";
        var list = new List<DceProfile>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var p = DceProfile.FromJson(r.GetString(3));
            p.Id = r.GetInt64(0);
            p.Name = r.GetString(1);
            p.Enabled = r.GetInt32(2) == 1;
            list.Add(p);
        }
        return list;
    }

    /// <summary>Crée (Id = 0) ou met à jour un profil ; renvoie son identifiant.</summary>
    public long SaveProfile(DceProfile p)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        if (p.Id == 0)
        {
            cmd.CommandText = "INSERT INTO dce_profiles (name, enabled, json, created_at) VALUES ($n, $e, $j, $c)";
            cmd.Parameters.AddWithValue("$c", NoticeRepository.Iso(DateTime.UtcNow));
        }
        else
        {
            cmd.CommandText = "UPDATE dce_profiles SET name = $n, enabled = $e, json = $j WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", p.Id);
        }
        cmd.Parameters.AddWithValue("$n", p.Name);
        cmd.Parameters.AddWithValue("$e", p.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$j", p.ToJson());
        cmd.ExecuteNonQuery();
        if (p.Id != 0) return p.Id;
        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(idCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void DeleteProfile(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dce_profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
