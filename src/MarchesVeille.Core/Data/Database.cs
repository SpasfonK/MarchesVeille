using Microsoft.Data.Sqlite;

namespace MarchesVeille.Core.Data;

/// <summary>Accès SQLite + migrations automatiques (PRAGMA user_version).</summary>
public sealed class Database
{
    public string Path { get; }
    private readonly string _connectionString;

    public Database(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    /// <summary>marches.db à côté de l'exécutable.</summary>
    public static Database OpenDefault() =>
        new(System.IO.Path.Combine(Util.AppPaths.DataDir, "marches.db"));

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public static void ReleasePools() => SqliteConnection.ClearAllPools();

    public void Migrate()
    {
        using var conn = Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        long version;
        using (var v = conn.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version;";
            version = (long)(v.ExecuteScalar() ?? 0L);
        }

        if (version < 1)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = SchemaV1;
            cmd.ExecuteNonQuery();
            tx.Commit();
            using var setV = conn.CreateCommand();
            setV.CommandText = "PRAGMA user_version = 1;";
            setV.ExecuteNonQuery();
        }
    }

    private const string SchemaV1 = """
CREATE TABLE sources (
    id             TEXT PRIMARY KEY,
    name           TEXT NOT NULL,
    type           TEXT NOT NULL,
    base_url       TEXT,
    is_active      INTEGER NOT NULL DEFAULT 1,
    last_synced_at TEXT
);

CREATE TABLE notices (
    rid              INTEGER PRIMARY KEY AUTOINCREMENT,
    id               TEXT NOT NULL UNIQUE,
    source_id        TEXT NOT NULL,
    reference        TEXT NOT NULL,
    title            TEXT NOT NULL DEFAULT '',
    description      TEXT NOT NULL DEFAULT '',
    buyer_name       TEXT NOT NULL DEFAULT '',
    buyer_siret      TEXT,
    department       TEXT,
    region           TEXT,
    buyer_category   TEXT,
    publication_date TEXT,
    deadline_date    TEXT,
    url_notice       TEXT,
    url_dce          TEXT,
    is_favorite      INTEGER NOT NULL DEFAULT 0,
    is_read          INTEGER NOT NULL DEFAULT 0,
    created_at       TEXT NOT NULL
);

CREATE INDEX ix_notices_pub      ON notices(publication_date DESC);
CREATE INDEX ix_notices_deadline ON notices(deadline_date);
CREATE INDEX ix_notices_category ON notices(buyer_category);
CREATE INDEX ix_notices_region   ON notices(region);
CREATE INDEX ix_notices_source   ON notices(source_id);
CREATE INDEX ix_notices_created  ON notices(created_at);

CREATE VIRTUAL TABLE notices_fts USING fts5(
    title, description, buyer_name, reference,
    content='notices', content_rowid='rid',
    tokenize='unicode61 remove_diacritics 2',
    prefix='2 3 4'
);

CREATE TRIGGER notices_ai AFTER INSERT ON notices BEGIN
    INSERT INTO notices_fts(rowid, title, description, buyer_name, reference)
    VALUES (new.rid, new.title, new.description, new.buyer_name, new.reference);
END;

CREATE TRIGGER notices_ad AFTER DELETE ON notices BEGIN
    INSERT INTO notices_fts(notices_fts, rowid, title, description, buyer_name, reference)
    VALUES ('delete', old.rid, old.title, old.description, old.buyer_name, old.reference);
END;

CREATE TRIGGER notices_au AFTER UPDATE OF title, description, buyer_name, reference ON notices
WHEN old.title IS NOT new.title OR old.description IS NOT new.description
  OR old.buyer_name IS NOT new.buyer_name OR old.reference IS NOT new.reference
BEGIN
    INSERT INTO notices_fts(notices_fts, rowid, title, description, buyer_name, reference)
    VALUES ('delete', old.rid, old.title, old.description, old.buyer_name, old.reference);
    INSERT INTO notices_fts(rowid, title, description, buyer_name, reference)
    VALUES (new.rid, new.title, new.description, new.buyer_name, new.reference);
END;

CREATE TABLE saved_searches (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT NOT NULL,
    filters_json  TEXT NOT NULL DEFAULT '{}',
    alert_enabled INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT NOT NULL,
    last_run_at   TEXT
);

INSERT INTO sources (id, name, type, base_url) VALUES
 ('boamp',   'BOAMP (DILA)',                   'API',          'https://boamp-datadila.opendatasoft.com'),
 ('place',   'PLACE (marches-publics.gouv.fr)', 'HTML_SCRAPER', 'https://www.marches-publics.gouv.fr/'),
 ('grandest','Région Grand Est',               'HTML_SCRAPER', 'https://marchespublics.grandest.fr/');
""";
}
