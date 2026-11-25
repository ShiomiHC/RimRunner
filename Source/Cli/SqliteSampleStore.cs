using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RimRunnerCli;

internal sealed class SqliteSampleStore : IDisposable
{
    internal const string FileName = "samples.db";
    internal const int SchemaVersion = 2;
    private const int UpgradeBatch = 5000;
    private const int BusyTimeoutMs = 60_000;

    private readonly SqliteConnection connection;

    private SqliteSampleStore(SqliteConnection connection, string path)
    {
        this.connection = connection;
        Path = path;
    }

    internal string Path { get; }

    internal sealed record StoredLine(long Ord, string CaseId, string RunId, bool IsSeal, string Line);

    internal static SqliteSampleStore Open(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        try
        {
            Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMs}");
            Execute(connection, "PRAGMA journal_mode = WAL");
            Execute(connection, "PRAGMA synchronous = NORMAL");
            var store = new SqliteSampleStore(connection, path);
            store.EnsureSchema();
            return store;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose() => connection.Dispose();

    private void EnsureSchema()
    {
        using SqliteTransaction transaction = Begin();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS scopes (id INTEGER PRIMARY KEY, hash BLOB NOT NULL UNIQUE, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS mvids (id INTEGER PRIMARY KEY, hash BLOB NOT NULL UNIQUE, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS fields (id INTEGER PRIMARY KEY, hash BLOB NOT NULL UNIQUE, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS records (
              ord INTEGER PRIMARY KEY,
              case_id TEXT NOT NULL, run_id TEXT NOT NULL,
              timestamp TEXT, series TEXT, segment TEXT, window_ticks INTEGER, mod_list TEXT,
              mvids INTEGER, scope INTEGER,
              raw TEXT,
              line_hash BLOB NOT NULL,
              UNIQUE (case_id, run_id, line_hash));
            CREATE TABLE IF NOT EXISTS rows (
              record INTEGER NOT NULL, ord INTEGER NOT NULL, key TEXT NOT NULL,
              fields INTEGER NOT NULL, vals TEXT NOT NULL,
              PRIMARY KEY (record, ord));
            CREATE TABLE IF NOT EXISTS seals (
              ord INTEGER PRIMARY KEY,
              case_id TEXT NOT NULL, run_id TEXT NOT NULL,
              seal TEXT, segments INTEGER, timestamp TEXT,
              line TEXT NOT NULL,
              UNIQUE (case_id, run_id));
            CREATE INDEX IF NOT EXISTS records_run ON records (case_id, run_id);
            """, transaction);
        string? version = Scalar(connection, "SELECT value FROM meta WHERE key = 'schema'", transaction) as string;
        if (version == null)
        {
            Execute(connection, $"INSERT INTO meta (key, value) VALUES ('schema', '{SchemaVersion}')", transaction);
        }
        else if (version == "1")
        {
            UpgradeFromV1(transaction);
        }
        else if (version != SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new InvalidDataException($"读数库 {Path} 的表结构版本是 {version}，本 RimRunner 只支持 {SchemaVersion}");
        }

        transaction.Commit();
    }

    private void UpgradeFromV1(SqliteTransaction transaction)
    {
        Execute(connection, "ALTER TABLE records RENAME COLUMN mod_set TO mod_list", transaction);
        long last = (long)Scalar(connection, "SELECT coalesce(max(ord), 0) FROM records", transaction)!;
        using SqliteCommand setRaw = Command("UPDATE records SET raw = $raw WHERE ord = $o", [("$raw", ""), ("$o", 0L)], transaction);
        using SqliteCommand setHash = Command("UPDATE records SET line_hash = $h WHERE ord = $o", [("$h", Array.Empty<byte>()), ("$o", 0L)], transaction);
        for (long from = 1; from <= last; from += UpgradeBatch)
        {
            List<(string, object?)> range = [("$from", from), ("$to", from + UpgradeBatch)];
            var renamed = new List<(long Ord, string Raw)>();
            using (SqliteCommand raws = Command(
                       "SELECT ord, raw FROM records WHERE raw IS NOT NULL AND ord >= $from AND ord < $to", range, transaction))
            using (SqliteDataReader reader = raws.ExecuteReader())
            {
                while (reader.Read())
                {
                    string raw = reader.GetString(1);
                    string current = SampleLine.RenameLegacyModListKey(raw);
                    if (current != raw) renamed.Add((reader.GetInt64(0), current));
                }
            }

            foreach ((long ord, string raw) in renamed)
            {
                setRaw.Parameters["$raw"].Value = raw;
                setRaw.Parameters["$o"].Value = ord;
                setRaw.ExecuteNonQuery();
            }

            List<(long Ord, byte[] Hash)> hashes =
            [
                .. ReadLines("WHERE @ord >= $from AND @ord < $to", range, transaction).Where(line => !line.IsSeal)
                    .Select(line => (line.Ord, SHA1.HashData(Encoding.UTF8.GetBytes(line.Line)))),
            ];
            foreach ((long ord, byte[] hash) in hashes)
            {
                setHash.Parameters["$h"].Value = hash;
                setHash.Parameters["$o"].Value = ord;
                setHash.ExecuteNonQuery();
            }
        }

        Execute(connection, $"UPDATE meta SET value = '{SchemaVersion}' WHERE key = 'schema'", transaction);
    }

    internal void Append(string caseId, string line) => AppendMany([(caseId, line)]);

    internal void AppendMany(IEnumerable<(string CaseId, string Line)> lines)
    {
        using SqliteTransaction transaction = Begin();
        long next = NextOrd(transaction);
        foreach ((string caseId, string line) in lines)
        {
            if (SampleLine.IsSeal(line, out _))
            {
                if (AppendSeal(caseId, line, next, transaction)) next++;
            }
            else if (AppendRecord(caseId, line, next, transaction))
            {
                next++;
            }
        }

        transaction.Commit();
    }

    private bool AppendRecord(string caseId, string line, long ord, SqliteTransaction transaction)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(line));
        SampleLine.Record record = SampleLine.ParseRecord(line);
        if (record.Raw == null && record.CaseId != caseId) record = record with { Raw = line };
        if (Scalar(connection, "SELECT 1 FROM records WHERE case_id = $c AND run_id = $r AND line_hash = $h",
                transaction, ("$c", caseId), ("$r", record.RunId), ("$h", hash)) != null)
            return false;

        Execute(connection, """
            INSERT INTO records (ord, case_id, run_id, timestamp, series, segment, window_ticks, mod_list,
                                 mvids, scope, raw, line_hash)
            VALUES ($o, $c, $r, $t, $se, $sg, $w, $m, $mv, $sc, $raw, $h)
            """, transaction,
            ("$o", ord), ("$c", caseId), ("$r", record.RunId), ("$t", record.Timestamp), ("$se", record.Series),
            ("$sg", record.Segment), ("$w", record.WindowTicks), ("$m", record.ModList),
            ("$mv", record.MvidsJson == null ? null : Intern("mvids", record.MvidsJson, transaction)),
            ("$sc", record.ScopeJson == null ? null : Intern("scopes", record.ScopeJson, transaction)),
            ("$raw", record.Raw), ("$h", hash));
        for (int index = 0; index < record.Rows.Count; index++)
        {
            SampleLine.Row row = record.Rows[index];
            Execute(connection, "INSERT INTO rows (record, ord, key, fields, vals) VALUES ($r, $o, $k, $f, $v)",
                transaction, ("$r", ord), ("$o", index), ("$k", row.Key),
                ("$f", Intern("fields", row.FieldsJson, transaction)), ("$v", row.ValuesJson));
        }

        return true;
    }

    private bool AppendSeal(string caseId, string line, long ord, SqliteTransaction transaction)
    {
        SampleLine.Seal seal = SampleLine.ParseSeal(line);
        if (Scalar(connection, "SELECT line FROM seals WHERE case_id = $c AND run_id = $r", transaction,
                ("$c", caseId), ("$r", seal.RunId)) is string existing)
        {
            if (existing == line) return false;
            throw new InvalidDataException($"用例 {caseId} 的 runId {seal.RunId} 已有一条内容不匹配的封批行");
        }

        Execute(connection, """
            INSERT INTO seals (ord, case_id, run_id, seal, segments, timestamp, line)
            VALUES ($o, $c, $r, $s, $n, $t, $l)
            """, transaction,
            ("$o", ord), ("$c", caseId), ("$r", seal.RunId), ("$s", seal.Status), ("$n", seal.Segments),
            ("$t", seal.Timestamp), ("$l", line));
        return true;
    }

    private long Intern(string table, string json, SqliteTransaction transaction)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(json));
        if (Scalar(connection, $"SELECT id FROM {table} WHERE hash = $h", transaction, ("$h", hash)) is long id)
            return id;
        Execute(connection, $"INSERT INTO {table} (hash, json) VALUES ($h, $j)", transaction, ("$h", hash), ("$j", json));
        return (long)Scalar(connection, "SELECT last_insert_rowid()", transaction)!;
    }

    private long NextOrd(SqliteTransaction transaction) =>
        (long)Scalar(connection,
            "SELECT max(coalesce((SELECT max(ord) FROM records), 0), coalesce((SELECT max(ord) FROM seals), 0)) + 1",
            transaction)!;

    internal IReadOnlyList<string> CaseIds()
    {
        var cases = new List<string>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT case_id FROM records UNION SELECT case_id FROM seals ORDER BY 1";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) cases.Add(reader.GetString(0));
        return cases;
    }

    internal IEnumerable<StoredLine> Lines(string? caseId = null, IReadOnlyCollection<string>? runIds = null)
    {
        string filter = Filter(caseId, runIds, out List<(string, object?)> parameters);
        using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
        foreach (StoredLine line in ReadLines(filter, parameters, snapshot)) yield return line;
    }

    private IEnumerable<StoredLine> ReadLines(string filter, List<(string, object?)> parameters, SqliteTransaction snapshot)
    {
        using SqliteCommand records = Command($"""
            SELECT r.ord, r.case_id, r.run_id, r.timestamp, r.series, r.segment, r.window_ticks, r.mod_list,
                   m.json, s.json, r.raw
            FROM records r LEFT JOIN mvids m ON m.id = r.mvids LEFT JOIN scopes s ON s.id = r.scope
            {filter.Replace("@", "r.")} ORDER BY r.ord
            """, parameters, snapshot);
        using SqliteCommand rows = Command($"""
            SELECT w.record, w.key, f.json, w.vals
            FROM rows w JOIN records r ON r.ord = w.record JOIN fields f ON f.id = w.fields
            {filter.Replace("@", "r.")} ORDER BY w.record, w.ord
            """, parameters, snapshot);
        using SqliteCommand seals = Command($"""
            SELECT ord, case_id, run_id, line FROM seals {filter.Replace("@", "")} ORDER BY ord
            """, parameters, snapshot);
        using SqliteDataReader recordReader = records.ExecuteReader();
        using SqliteDataReader rowReader = rows.ExecuteReader();
        using SqliteDataReader sealReader = seals.ExecuteReader();

        bool hasRow = rowReader.Read();
        bool hasSeal = sealReader.Read();
        while (recordReader.Read())
        {
            long ord = recordReader.GetInt64(0);
            while (hasSeal && sealReader.GetInt64(0) < ord)
            {
                yield return SealOf(sealReader);
                hasSeal = sealReader.Read();
            }

            var rowList = new List<SampleLine.Row>();
            while (hasRow && rowReader.GetInt64(0) == ord)
            {
                rowList.Add(new SampleLine.Row(rowReader.GetString(1), rowReader.GetString(2), rowReader.GetString(3)));
                hasRow = rowReader.Read();
            }

            var record = new SampleLine.Record(recordReader.GetString(2), Text(recordReader, 3), recordReader.GetString(1),
                Text(recordReader, 4), Text(recordReader, 5),
                recordReader.IsDBNull(6) ? null : recordReader.GetInt64(6), Text(recordReader, 7),
                Text(recordReader, 8), Text(recordReader, 9), rowList, Text(recordReader, 10));
            yield return new StoredLine(ord, recordReader.GetString(1), recordReader.GetString(2), false,
                SampleLine.Render(record));
        }

        while (hasSeal)
        {
            yield return SealOf(sealReader);
            hasSeal = sealReader.Read();
        }
    }

    private static StoredLine SealOf(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), true, reader.GetString(3));

    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static string Filter(string? caseId, IReadOnlyCollection<string>? runIds,
        out List<(string, object?)> parameters)
    {
        parameters = [];
        var clauses = new List<string>();
        if (caseId != null)
        {
            clauses.Add("@case_id = $case");
            parameters.Add(("$case", caseId));
        }

        if (runIds != null)
        {
            var names = new List<string>();
            foreach (string runId in runIds)
            {
                string name = "$run" + names.Count;
                names.Add(name);
                parameters.Add((name, runId));
            }

            clauses.Add(names.Count == 0 ? "0" : $"@run_id IN ({string.Join(", ", names)})");
        }

        return clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
    }

    private SqliteCommand Command(string sql, List<(string, object?)> parameters, SqliteTransaction transaction)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private SqliteTransaction Begin() => connection.BeginTransaction(deferred: false);

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        object? result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }
}
