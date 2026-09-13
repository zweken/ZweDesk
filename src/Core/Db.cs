using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ZweDesk.Core;

/// <summary>
/// Embedded SQLite store. Schema is created/upgraded by numbered idempotent migrations on every start,
/// so copying zwedesk.db to another machine is all that is needed to move the configuration.
/// </summary>
public sealed class Db
{
    private readonly object _writeGate = new();

    public string FilePath { get; }

    public Db(string path) => FilePath = path;

    // ------------------------------------------------------------------ connection

    private SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        p.ExecuteNonQuery();
        return c;
    }

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private int Exec(string sql, params (string, object?)[] args)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var cmd = Cmd(c, sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    private long ExecInsert(string sql, params (string, object?)[] args)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var cmd = Cmd(c, sql + "; SELECT last_insert_rowid();", args);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Cmd(c, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Cmd(c, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    // ------------------------------------------------------------------ value helpers

    private static string Iso(DateTime d) => (d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime()).ToString("o");
    private static string? Iso(DateTime? d) => d.HasValue ? Iso(d.Value) : null;

    private static DateTime? ParseDate(string? s) =>
        string.IsNullOrEmpty(s) ? null : DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string S(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? "" : r.GetString(r.GetOrdinal(col));
    private static string? SN(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
    private static long L(SqliteDataReader r, string col) => r.GetInt64(r.GetOrdinal(col));
    private static long? LN(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt64(r.GetOrdinal(col));
    private static bool B(SqliteDataReader r, string col) => !r.IsDBNull(r.GetOrdinal(col)) && r.GetInt64(r.GetOrdinal(col)) != 0;
    private static bool? BN(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt64(r.GetOrdinal(col)) != 0;
    private static DateTime D(SqliteDataReader r, string col) => ParseDate(S(r, col)) ?? DateTime.MinValue;
    private static DateTime? DN(SqliteDataReader r, string col) => ParseDate(SN(r, col));

    public static string Key(string name) => name.Trim().ToLowerInvariant();

    // ------------------------------------------------------------------ migrations

    private static readonly (int Version, string Sql)[] Migrations =
    {
        (1, """
            CREATE TABLE IF NOT EXISTS domains (
              id            INTEGER PRIMARY KEY AUTOINCREMENT,
              name          TEXT NOT NULL UNIQUE COLLATE NOCASE,
              netbios       TEXT NOT NULL DEFAULT '',
              dfs_root      TEXT NOT NULL DEFAULT '',
              dfs_host_fqdn TEXT NOT NULL DEFAULT '',
              created_at    TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS servers (
              id             INTEGER PRIMARY KEY AUTOINCREMENT,
              domain_id      INTEGER NOT NULL REFERENCES domains(id) ON DELETE CASCADE,
              name           TEXT NOT NULL,
              fqdn           TEXT NOT NULL,
              share_name     TEXT,
              local_root     TEXT NOT NULL,
              enabled        INTEGER NOT NULL DEFAULT 1,
              last_scan_at   TEXT,
              last_error     TEXT,
              fsrm_installed INTEGER,
              UNIQUE(domain_id, name COLLATE NOCASE));
            CREATE TABLE IF NOT EXISTS folders (
              id                INTEGER PRIMARY KEY AUTOINCREMENT,
              server_id         INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
              name              TEXT NOT NULL,
              name_key          TEXT NOT NULL,
              path              TEXT NOT NULL,
              owner             TEXT,
              user_rights       TEXT,
              extra_access_json TEXT NOT NULL DEFAULT '[]',
              aces_json         TEXT NOT NULL DEFAULT '[]',
              quota_bytes       INTEGER,
              used_bytes        INTEGER,
              last_write_utc    TEXT,
              scanned_at        TEXT NOT NULL,
              UNIQUE(server_id, name_key));
            CREATE INDEX IF NOT EXISTS ix_folders_name_key ON folders(name_key);
            CREATE TABLE IF NOT EXISTS dfs_links (
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              domain_id    INTEGER NOT NULL REFERENCES domains(id) ON DELETE CASCADE,
              name         TEXT NOT NULL,
              name_key     TEXT NOT NULL,
              link_path    TEXT NOT NULL,
              targets_json TEXT NOT NULL DEFAULT '[]',
              state        TEXT,
              scanned_at   TEXT NOT NULL,
              UNIQUE(domain_id, name_key));
            CREATE TABLE IF NOT EXISTS issues (
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              domain_id    INTEGER NOT NULL REFERENCES domains(id) ON DELETE CASCADE,
              kind         TEXT NOT NULL,
              name_key     TEXT NOT NULL,
              details_json TEXT NOT NULL DEFAULT '{}',
              detected_at  TEXT NOT NULL,
              resolved_at  TEXT);
            CREATE INDEX IF NOT EXISTS ix_issues_domain ON issues(domain_id, kind);
            CREATE TABLE IF NOT EXISTS audit (
              id          INTEGER PRIMARY KEY AUTOINCREMENT,
              ts          TEXT NOT NULL,
              actor       TEXT NOT NULL,
              domain      TEXT,
              server      TEXT,
              op          TEXT NOT NULL,
              target      TEXT,
              params_json TEXT,
              ok          INTEGER NOT NULL,
              error       TEXT);
            CREATE INDEX IF NOT EXISTS ix_audit_ts ON audit(ts);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);
            INSERT OR IGNORE INTO settings(key, value) VALUES ('default_quota_bytes', '5368709120');
            INSERT OR IGNORE INTO settings(key, value) VALUES ('theme', 'dark');
            """),
        (2, """
            ALTER TABLE domains ADD COLUMN dc_fqdn TEXT NOT NULL DEFAULT '';
            ALTER TABLE domains ADD COLUMN horizon_url TEXT NOT NULL DEFAULT '';
            ALTER TABLE domains ADD COLUMN horizon_auth TEXT NOT NULL DEFAULT 'none';
            ALTER TABLE domains ADD COLUMN horizon_user TEXT NOT NULL DEFAULT '';
            ALTER TABLE domains ADD COLUMN horizon_domain TEXT NOT NULL DEFAULT '';
            ALTER TABLE domains ADD COLUMN horizon_ignore_cert INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE domains ADD COLUMN horizon_entitle_mode TEXT NOT NULL DEFAULT 'group';
            ALTER TABLE domains ADD COLUMN default_ou_dn TEXT NOT NULL DEFAULT '';
            ALTER TABLE domains ADD COLUMN default_groups_json TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE domains ADD COLUMN default_pool_id TEXT NOT NULL DEFAULT '';
            UPDATE domains SET dc_fqdn = dfs_host_fqdn WHERE dc_fqdn = '';
            CREATE TABLE IF NOT EXISTS horizon_pools (
              id                   INTEGER PRIMARY KEY AUTOINCREMENT,
              domain_id            INTEGER NOT NULL REFERENCES domains(id) ON DELETE CASCADE,
              pool_id              TEXT NOT NULL,
              name                 TEXT NOT NULL,
              display_name         TEXT,
              type                 TEXT,
              enabled              INTEGER NOT NULL DEFAULT 1,
              entitled_groups_json TEXT NOT NULL DEFAULT '[]',
              entitled_users       INTEGER NOT NULL DEFAULT 0,
              fetched_at           TEXT NOT NULL,
              UNIQUE(domain_id, pool_id));
            INSERT OR IGNORE INTO settings(key, value) VALUES ('must_change_default', '1');
            """),
    };

    /// <summary>Applies pending migrations. The database starts empty: environments come from the machine's domain membership or the user.</summary>
    public void Migrate()
    {
        lock (_writeGate)
        {
            using var c = Open();
            using (var v = Cmd(c, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);"))
                v.ExecuteNonQuery();

            var applied = new HashSet<long>();
            using (var q = Cmd(c, "SELECT version FROM schema_version"))
            using (var r = q.ExecuteReader())
                while (r.Read()) applied.Add(r.GetInt64(0));

            foreach (var (version, sql) in Migrations)
            {
                if (applied.Contains(version)) continue;
                using var tx = c.BeginTransaction();
                using (var m = Cmd(c, sql)) { m.Transaction = tx; m.ExecuteNonQuery(); }
                using (var m = Cmd(c, "INSERT INTO schema_version(version, applied_at) VALUES ($v, $t)", ("$v", version), ("$t", Iso(DateTime.UtcNow))))
                { m.Transaction = tx; m.ExecuteNonQuery(); }
                tx.Commit();
                Log.Info($"db: migration {version} applied");
            }
        }
    }

    // ------------------------------------------------------------------ settings

    public string? GetSetting(string key) => Scalar("SELECT value FROM settings WHERE key=$k", ("$k", key)) as string;

    public void SetSetting(string key, string? value)
    {
        if (value is null) Exec("DELETE FROM settings WHERE key=$k", ("$k", key));
        else Exec("INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
    }

    public Dictionary<string, string> AllSettings() =>
        Query("SELECT key, value FROM settings", r => (S(r, "key"), S(r, "value"))).ToDictionary(t => t.Item1, t => t.Item2);

    public long DefaultQuotaBytes
    {
        get => long.TryParse(GetSetting("default_quota_bytes"), out var v) && v > 0 ? v : 5L * 1024 * 1024 * 1024;
        set => SetSetting("default_quota_bytes", value.ToString(CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ domains

    private static DomainRec MapDomain(SqliteDataReader r) =>
        new(L(r, "id"), S(r, "name"), S(r, "netbios"), S(r, "dfs_root"), S(r, "dfs_host_fqdn"), D(r, "created_at"),
            S(r, "dc_fqdn"), S(r, "horizon_url"), S(r, "horizon_auth"), S(r, "horizon_user"), S(r, "horizon_domain"), B(r, "horizon_ignore_cert"), S(r, "horizon_entitle_mode"),
            S(r, "default_ou_dn"), S(r, "default_groups_json"), S(r, "default_pool_id"));

    public List<DomainRec> ListDomains() => Query("SELECT * FROM domains ORDER BY name", MapDomain);

    public DomainRec? GetDomain(long id) => Query("SELECT * FROM domains WHERE id=$id", MapDomain, ("$id", id)).FirstOrDefault();

    public long SaveDomain(long? id, string name, string netbios, string dfsRoot, string dfsHostFqdn)
    {
        if (id is > 0)
        {
            Exec("UPDATE domains SET name=$n, netbios=$nb, dfs_root=$r, dfs_host_fqdn=$h WHERE id=$id",
                ("$n", name), ("$nb", netbios), ("$r", dfsRoot), ("$h", dfsHostFqdn), ("$id", id));
            return id.Value;
        }
        return ExecInsert("INSERT INTO domains(name, netbios, dfs_root, dfs_host_fqdn, dc_fqdn, created_at) VALUES ($n, $nb, $r, $h, $h, $t)",
            ("$n", name), ("$nb", netbios), ("$r", dfsRoot), ("$h", dfsHostFqdn), ("$t", Iso(DateTime.UtcNow)));
    }

    /// <summary>Saves every environment field except the timestamps (Id 0 inserts).</summary>
    public long SaveEnvironment(DomainRec d)
    {
        if (d.Id > 0)
        {
            Exec("""
                UPDATE domains SET name=$n, netbios=$nb, dfs_root=$r, dfs_host_fqdn=$h, dc_fqdn=$dc,
                  horizon_url=$hu, horizon_auth=$ha, horizon_user=$hus, horizon_domain=$hd, horizon_ignore_cert=$hc, horizon_entitle_mode=$hm,
                  default_ou_dn=$ou, default_groups_json=$gj, default_pool_id=$pool
                WHERE id=$id
                """,
                ("$n", d.Name), ("$nb", d.Netbios), ("$r", d.DfsRoot), ("$h", d.DfsHostFqdn), ("$dc", d.DcFqdn),
                ("$hu", d.HorizonUrl), ("$ha", d.HorizonAuth), ("$hus", d.HorizonUser), ("$hd", d.HorizonDomain), ("$hc", d.HorizonIgnoreCert ? 1 : 0), ("$hm", d.HorizonEntitleMode),
                ("$ou", d.DefaultOuDn), ("$gj", d.DefaultGroupsJson), ("$pool", d.DefaultPoolId), ("$id", d.Id));
            return d.Id;
        }
        return ExecInsert("""
            INSERT INTO domains(name, netbios, dfs_root, dfs_host_fqdn, dc_fqdn, horizon_url, horizon_auth, horizon_user, horizon_domain, horizon_ignore_cert, horizon_entitle_mode,
                                default_ou_dn, default_groups_json, default_pool_id, created_at)
            VALUES ($n, $nb, $r, $h, $dc, $hu, $ha, $hus, $hd, $hc, $hm, $ou, $gj, $pool, $t)
            """,
            ("$n", d.Name), ("$nb", d.Netbios), ("$r", d.DfsRoot), ("$h", d.DfsHostFqdn), ("$dc", d.DcFqdn),
            ("$hu", d.HorizonUrl), ("$ha", d.HorizonAuth), ("$hus", d.HorizonUser), ("$hd", d.HorizonDomain), ("$hc", d.HorizonIgnoreCert ? 1 : 0), ("$hm", d.HorizonEntitleMode),
            ("$ou", d.DefaultOuDn), ("$gj", d.DefaultGroupsJson), ("$pool", d.DefaultPoolId), ("$t", Iso(DateTime.UtcNow)));
    }

    public void SaveProvisionDefaults(long id, string ouDn, string groupsJson, string poolId) =>
        Exec("UPDATE domains SET default_ou_dn=$ou, default_groups_json=$gj, default_pool_id=$pool WHERE id=$id",
            ("$ou", ouDn), ("$gj", groupsJson), ("$pool", poolId), ("$id", id));

    // ------------------------------------------------------------------ horizon pools (cache of the last successful fetch)

    private static HorizonPoolRec MapPool(SqliteDataReader r) =>
        new(L(r, "id"), L(r, "domain_id"), S(r, "pool_id"), S(r, "name"), SN(r, "display_name"), SN(r, "type"), B(r, "enabled"),
            S(r, "entitled_groups_json"), (int)L(r, "entitled_users"), D(r, "fetched_at"));

    public List<HorizonPoolRec> ListPools(long domainId) =>
        Query("SELECT * FROM horizon_pools WHERE domain_id=$d ORDER BY name COLLATE NOCASE", MapPool, ("$d", domainId));

    public void ReplacePools(long domainId, IEnumerable<HorizonPool> pools)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using (var del = Cmd(c, "DELETE FROM horizon_pools WHERE domain_id=$d", ("$d", domainId))) { del.Transaction = tx; del.ExecuteNonQuery(); }
            var now = Iso(DateTime.UtcNow);
            foreach (var p in pools)
            {
                using var cmd = Cmd(c, "INSERT INTO horizon_pools(domain_id, pool_id, name, display_name, type, enabled, entitled_groups_json, entitled_users, fetched_at) VALUES ($d, $p, $n, $dn, $t, $e, $g, $u, $now)",
                    ("$d", domainId), ("$p", p.Id), ("$n", p.Name), ("$dn", p.DisplayName), ("$t", p.Type), ("$e", p.Enabled ? 1 : 0),
                    ("$g", System.Text.Json.JsonSerializer.Serialize(p.EntitledGroups)), ("$u", p.EntitledUsers), ("$now", now));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void DeleteDomain(long id) => Exec("DELETE FROM domains WHERE id=$id", ("$id", id));

    // ------------------------------------------------------------------ servers

    private static ServerRec MapServer(SqliteDataReader r) =>
        new(L(r, "id"), L(r, "domain_id"), S(r, "name"), S(r, "fqdn"), SN(r, "share_name"), S(r, "local_root"), B(r, "enabled"),
            DN(r, "last_scan_at"), SN(r, "last_error"), BN(r, "fsrm_installed"));

    public List<ServerRec> ListServers(long domainId) =>
        Query("SELECT * FROM servers WHERE domain_id=$d ORDER BY name", MapServer, ("$d", domainId));

    public List<ServerRec> ListAllServers() => Query("SELECT * FROM servers ORDER BY domain_id, name", MapServer);

    public ServerRec? GetServer(long id) => Query("SELECT * FROM servers WHERE id=$id", MapServer, ("$id", id)).FirstOrDefault();

    public long SaveServer(long? id, long domainId, string name, string fqdn, string localRoot, string? shareName, bool enabled)
    {
        if (id is > 0)
        {
            Exec("UPDATE servers SET domain_id=$d, name=$n, fqdn=$f, local_root=$r, share_name=$s, enabled=$e WHERE id=$id",
                ("$d", domainId), ("$n", name), ("$f", fqdn), ("$r", localRoot), ("$s", shareName), ("$e", enabled ? 1 : 0), ("$id", id));
            return id.Value;
        }
        return ExecInsert("INSERT INTO servers(domain_id, name, fqdn, local_root, share_name, enabled) VALUES ($d, $n, $f, $r, $s, $e)",
            ("$d", domainId), ("$n", name), ("$f", fqdn), ("$r", localRoot), ("$s", shareName), ("$e", enabled ? 1 : 0));
    }

    public void DeleteServer(long id) => Exec("DELETE FROM servers WHERE id=$id", ("$id", id));

    public void SetServerScanState(long id, DateTime? lastScanAt, string? error) =>
        Exec("UPDATE servers SET last_scan_at=COALESCE($t, last_scan_at), last_error=$e WHERE id=$id", ("$t", Iso(lastScanAt)), ("$e", error), ("$id", id));

    public void SetServerFsrm(long id, bool? installed) =>
        Exec("UPDATE servers SET fsrm_installed=$v WHERE id=$id", ("$v", installed.HasValue ? (installed.Value ? 1 : 0) : null), ("$id", id));

    /// <summary>Outcome of the last DFS link scan of a domain (kept in settings; the links table only ever holds a successful read).</summary>
    public void SetDfsScanState(long domainId, DateTime? at, string? error)
    {
        if (at.HasValue) SetSetting("dfs_scan_" + domainId, Iso(at.Value));
        SetSetting("dfs_scan_error_" + domainId, error);
    }

    /// <summary>True when the links of the domain were read successfully and the last attempt did not fail.</summary>
    public bool DfsScanOk(long domainId) => GetSetting("dfs_scan_" + domainId) is not null && GetSetting("dfs_scan_error_" + domainId) is null;

    public void SetServerShare(long id, string? shareName) =>
        Exec("UPDATE servers SET share_name=$s WHERE id=$id", ("$s", shareName), ("$id", id));

    // ------------------------------------------------------------------ folders

    private static FolderRec MapFolder(SqliteDataReader r) =>
        new(L(r, "id"), L(r, "server_id"), S(r, "name"), S(r, "name_key"), S(r, "path"), SN(r, "owner"), SN(r, "user_rights"),
            S(r, "extra_access_json"), S(r, "aces_json"), LN(r, "quota_bytes"), LN(r, "used_bytes"), DN(r, "last_write_utc"), D(r, "scanned_at"));

    public List<FolderRec> ListFolders(long domainId) =>
        Query("SELECT f.* FROM folders f JOIN servers s ON s.id=f.server_id WHERE s.domain_id=$d ORDER BY f.name COLLATE NOCASE, s.name",
            MapFolder, ("$d", domainId));

    public List<FolderRec> ListFoldersByServer(long serverId) =>
        Query("SELECT * FROM folders WHERE server_id=$s ORDER BY name COLLATE NOCASE", MapFolder, ("$s", serverId));

    public FolderRec? GetFolder(long id) => Query("SELECT * FROM folders WHERE id=$id", MapFolder, ("$id", id)).FirstOrDefault();

    /// <summary>Replaces the folder rows of one server with a fresh scan (rows that disappeared are removed).</summary>
    public void ReplaceFolders(long serverId, IEnumerable<FolderRec> rows)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var now = Iso(DateTime.UtcNow);
            var keep = new List<string>();
            foreach (var f in rows)
            {
                keep.Add(f.NameKey);
                using var cmd = Cmd(c, """
                    INSERT INTO folders(server_id, name, name_key, path, owner, user_rights, extra_access_json, aces_json, quota_bytes, used_bytes, last_write_utc, scanned_at)
                    VALUES ($sid, $n, $k, $p, $o, $ur, $ex, $aces, $q, $u, $lw, $t)
                    ON CONFLICT(server_id, name_key) DO UPDATE SET
                      name=excluded.name, path=excluded.path, owner=excluded.owner, user_rights=excluded.user_rights,
                      extra_access_json=excluded.extra_access_json, aces_json=excluded.aces_json,
                      quota_bytes=excluded.quota_bytes,
                      used_bytes=COALESCE(excluded.used_bytes, CASE WHEN excluded.quota_bytes IS NULL THEN folders.used_bytes ELSE NULL END),
                      last_write_utc=excluded.last_write_utc, scanned_at=excluded.scanned_at
                    """,
                    ("$sid", serverId), ("$n", f.Name), ("$k", f.NameKey), ("$p", f.Path), ("$o", f.Owner), ("$ur", f.UserRights),
                    ("$ex", f.ExtraAccessJson), ("$aces", f.AcesJson), ("$q", f.QuotaBytes), ("$u", f.UsedBytes), ("$lw", Iso(f.LastWriteUtc)), ("$t", now));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            using (var del = Cmd(c, "DELETE FROM folders WHERE server_id=$sid AND scanned_at<>$t", ("$sid", serverId), ("$t", now)))
            { del.Transaction = tx; del.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    public void SetFolderQuota(long folderId, long? quotaBytes, long? usedBytes) =>
        Exec("UPDATE folders SET quota_bytes=$q, used_bytes=$u WHERE id=$id", ("$q", quotaBytes), ("$u", usedBytes), ("$id", folderId));

    public void SetFolderUsed(long folderId, long usedBytes) =>
        Exec("UPDATE folders SET used_bytes=$u WHERE id=$id", ("$u", usedBytes), ("$id", folderId));

    // ------------------------------------------------------------------ dfs links

    private static DfsLinkRec MapLink(SqliteDataReader r) =>
        new(L(r, "id"), L(r, "domain_id"), S(r, "name"), S(r, "name_key"), S(r, "link_path"), S(r, "targets_json"), SN(r, "state"), D(r, "scanned_at"));

    public List<DfsLinkRec> ListLinks(long domainId) =>
        Query("SELECT * FROM dfs_links WHERE domain_id=$d ORDER BY name COLLATE NOCASE", MapLink, ("$d", domainId));

    public void ReplaceLinks(long domainId, IEnumerable<DfsLinkRec> rows)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var now = Iso(DateTime.UtcNow);
            foreach (var l in rows)
            {
                using var cmd = Cmd(c, """
                    INSERT INTO dfs_links(domain_id, name, name_key, link_path, targets_json, state, scanned_at)
                    VALUES ($d, $n, $k, $p, $t, $s, $now)
                    ON CONFLICT(domain_id, name_key) DO UPDATE SET
                      name=excluded.name, link_path=excluded.link_path, targets_json=excluded.targets_json, state=excluded.state, scanned_at=excluded.scanned_at
                    """,
                    ("$d", domainId), ("$n", l.Name), ("$k", l.NameKey), ("$p", l.LinkPath), ("$t", l.TargetsJson), ("$s", l.State), ("$now", now));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            using (var del = Cmd(c, "DELETE FROM dfs_links WHERE domain_id=$d AND scanned_at<>$now", ("$d", domainId), ("$now", now)))
            { del.Transaction = tx; del.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    // ------------------------------------------------------------------ issues

    private static IssueRec MapIssue(SqliteDataReader r) =>
        new(L(r, "id"), L(r, "domain_id"), S(r, "kind"), S(r, "name_key"), S(r, "details_json"), D(r, "detected_at"));

    public List<IssueRec> ListIssues(long domainId) =>
        Query("SELECT * FROM issues WHERE domain_id=$d AND resolved_at IS NULL ORDER BY kind, name_key", MapIssue, ("$d", domainId));

    public void ReplaceIssues(long domainId, IEnumerable<IssueRec> rows)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            // keep the original detection time of issues that still exist
            var previous = new Dictionary<string, string>();
            using (var q = Cmd(c, "SELECT kind, name_key, detected_at FROM issues WHERE domain_id=$d", ("$d", domainId)))
            using (var r = q.ExecuteReader())
                while (r.Read()) previous[r.GetString(0) + "|" + r.GetString(1)] = r.GetString(2);
            using (var del = Cmd(c, "DELETE FROM issues WHERE domain_id=$d", ("$d", domainId))) { del.Transaction = tx; del.ExecuteNonQuery(); }
            var now = Iso(DateTime.UtcNow);
            foreach (var i in rows)
            {
                var detected = previous.TryGetValue(i.Kind + "|" + i.NameKey, out var t) ? t : now;
                using var cmd = Cmd(c, "INSERT INTO issues(domain_id, kind, name_key, details_json, detected_at) VALUES ($d, $k, $n, $j, $t)",
                    ("$d", domainId), ("$k", i.Kind), ("$n", i.NameKey), ("$j", i.DetailsJson), ("$t", detected));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ------------------------------------------------------------------ audit

    private static AuditRec MapAudit(SqliteDataReader r) =>
        new(L(r, "id"), D(r, "ts"), S(r, "actor"), SN(r, "domain"), SN(r, "server"), S(r, "op"), SN(r, "target"), SN(r, "params_json"), B(r, "ok"), SN(r, "error"));

    public void AddAudit(string actor, string? domain, string? server, string op, string? target, string? paramsJson, bool ok, string? error) =>
        Exec("INSERT INTO audit(ts, actor, domain, server, op, target, params_json, ok, error) VALUES ($t, $a, $d, $s, $o, $g, $p, $ok, $e)",
            ("$t", Iso(DateTime.UtcNow)), ("$a", actor), ("$d", domain), ("$s", server), ("$o", op), ("$g", target), ("$p", paramsJson), ("$ok", ok ? 1 : 0), ("$e", error));

    public (List<AuditRec> Rows, long Total) ListAudit(int limit, int offset, string? filter)
    {
        var where = string.IsNullOrWhiteSpace(filter) ? "" : " WHERE op LIKE $f OR target LIKE $f OR actor LIKE $f OR server LIKE $f OR error LIKE $f";
        var f = "%" + (filter ?? "").Trim() + "%";
        var rows = Query($"SELECT * FROM audit{where} ORDER BY id DESC LIMIT $l OFFSET $o", MapAudit, ("$f", f), ("$l", limit), ("$o", offset));
        var total = (long)(Scalar($"SELECT COUNT(*) FROM audit{where}", ("$f", f)) ?? 0L);
        return (rows, total);
    }

    public List<AuditRec> AuditForTarget(string target, int limit = 50) =>
        Query("SELECT * FROM audit WHERE target=$t ORDER BY id DESC LIMIT $l", MapAudit, ("$t", target), ("$l", limit));
}
