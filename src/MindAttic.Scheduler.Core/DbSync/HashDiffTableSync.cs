using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>
/// What a hashdiff-fallback sync did to one table. <see cref="Method"/> is <c>unchanged</c>, <c>append</c>,
/// <c>hashdiff</c> or <c>full</c>; <see cref="State"/> is the table's bookkeeping for <see cref="WatermarkStore.Commit"/>
/// once the whole run has succeeded.
/// </summary>
public sealed record HashDiffResult(string Table, string Method, long Deleted, long Inserted, string Reason, DbSyncTableState State);

/// <summary>
/// The sync of a table Change Tracking cannot cover (no primary key, temporal history), ported from Sync-ToAzure.ps1
/// (M.SCHD-T67). A cheap verdict calls the table unchanged without a scan where the source's write statistics allow;
/// otherwise both sides are fingerprinted. A table that differs is brought equal, in one target transaction, by the
/// cheapest correct method: an append-only tail is copied alone; anything else is diffed by a NULL-safe, length-prefixed
/// SHA2-256 per row in key ranges, the target rows whose hash is not on the source deleted and the source rows whose hash
/// is not on the target inserted. Rows are compared as a multiset, so duplicate rows are kept exactly.
/// </summary>
public static class HashDiffTableSync
{
    public const string Method = "hashdiff";

    private static readonly HashSet<string> FixedTypes = ["bigint", "int", "smallint", "tinyint", "bit", "float", "real", "decimal", "numeric",
        "money", "smallmoney", "date", "time", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "uniqueidentifier"];
    private static readonly HashSet<string> LobTypes = ["text", "ntext", "image", "xml", "json", "vector", "sql_variant", "geography", "geometry", "hierarchyid"];
    private static readonly HashSet<string> RangeTypes = ["bigint", "int", "smallint", "tinyint", "bit", "char", "varchar", "nchar", "nvarchar", "date",
        "datetime", "datetime2", "smalldatetime", "datetimeoffset", "time", "uniqueidentifier", "decimal", "numeric", "binary", "varbinary", "money", "smallmoney"];
    private static readonly HashSet<string> WatermarkTypes = ["bigint", "int", "smallint", "tinyint", "date", "datetime", "datetime2", "smalldatetime",
        "datetimeoffset", "time", "decimal", "numeric"];
    private static readonly HashSet<string> JsonKeyTypes = ["bigint", "int", "smallint", "tinyint", "bit", "char", "varchar", "nchar", "nvarchar", "date",
        "datetime", "datetime2", "smalldatetime", "datetimeoffset", "time", "uniqueidentifier", "decimal", "numeric"];

    private const string HashIn = "IN (SELECT CONVERT(BINARY(32), j.value, 2) FROM OPENJSON(@hj) AS j)";

    /// <summary>What <see cref="DbSyncTableState.StateJson"/> holds: the source's clock when the last successful sync began.</summary>
    private sealed record State(DateTime SourceClock);

    /// <summary>
    /// Makes the target's copy of <paramref name="tableKey"/> equal to the source's. <paramref name="previous"/> is the
    /// table's state from the last successful run, or null. Writes only to <paramref name="target"/>.
    /// </summary>
    public static HashDiffResult Apply(SqlConnection source, SqlConnection target, TableCatalog sourceCatalog, TableCatalog targetCatalog,
        string tableKey, DbSyncTableState? previous, DbSyncSettings settings)
    {
        var m = Member.Create(sourceCatalog, targetCatalog, tableKey, settings);
        var clock = Convert.ToDateTime(Scalar(source, null, settings, "SELECT GETDATE()"), CultureInfo.InvariantCulture);
        var state = new DbSyncTableState(m.Table.Key, Method, null, JsonSerializer.Serialize(new State(clock)));
        HashDiffResult Result(string method, long deleted, long inserted, string reason) =>
            new(m.Table.Key, method, deleted, inserted, reason, state);

        if (CheapVerdict(source, m, previous, settings) is { } cheap) return Result("unchanged", 0, 0, cheap);

        // Catalog row counts that differ are confirmed with an exact COUNT before skipping the dearer fingerprint.
        long srcRows = -1, tgtRows = -2;
        if (m.Table.Rows != m.TargetTable.Rows)
        {
            srcRows = Count(source, m, settings);
            tgtRows = Count(target, m, settings);
        }
        if (srcRows == tgtRows || m.Table.Rows == m.TargetTable.Rows)
        {
            var ft = Fingerprint(target, m, "", "", [], settings);
            var fs = Fingerprint(source, m, "", "", [], settings);
            srcRows = fs.Count;
            tgtRows = ft.Count;
            if (fs == ft) return Result("unchanged", 0, 0, $"{(m.Strong ? "sha256-sum" : "column-aggregates")} fingerprints agree on {srcRows:n0} rows");
        }

        // Planning reads both sides; only then does the one target transaction open.
        var append = srcRows > 0 && tgtRows > 0 && srcRows > tgtRows && m.AppendSpec is not null ? TestAppend(source, target, m, settings) : null;
        var plan = srcRows > 0 && tgtRows > 0 && append is null ? HashDiffPlan(source, target, m, srcRows, settings) : null;

        using var tx = target.BeginTransaction();
        HashDiffResult result;
        if (srcRows == 0)
        {
            var deleted = Exec(target, tx, settings, $"DELETE FROM {m.Table.QName};", []);
            result = Result("full", deleted, 0, "the source table is empty");
        }
        else if (tgtRows == 0)
        {
            var inserted = CopyRows(source, target, tx, m, "", [], settings);
            result = Result("full", 0, inserted, "the target table is empty");
        }
        else if (append is not null)
        {
            var inserted = CopyRows(source, target, tx, m, append.From, append.Params, settings);
            result = Result("append", 0, inserted, append.Reason);
        }
        else
        {
            long deleted = 0, inserted = 0;
            foreach (var r in plan!.Ranges)
                foreach (var chunk in r.Delete.Chunk(settings.BulkBatchSize))
                    deleted += Exec(target, tx, settings, $"{m.Prelude}DELETE t FROM {m.Table.QName} AS t WHERE {HashWhere(r.Where, m)};",
                        [.. RangeParams(m.RangeCols, r.Lo, r.Hi), HashJson(chunk)]);
            foreach (var r in plan!.Ranges)
                foreach (var chunk in r.Insert.Chunk(settings.BulkBatchSize))
                    inserted += CopyRows(source, target, tx, m, $"WHERE {HashWhere(r.Where, m)}", [.. RangeParams(m.RangeCols, r.Lo, r.Hi), HashJson(chunk)], settings);
            result = Result(plan!.Ranges.Count == 0 ? "unchanged" : "hashdiff", deleted, inserted, plan!.Reason);
        }
        tx.Commit();
        return result;
    }

    /// <summary>
    /// "Unchanged" without a scan (Get-CheapVerdict): the catalog row counts agree and the source's write statistics,
    /// which cover the whole interval because the engine has not restarted and the database does not auto-close, show no
    /// insert, update or delete since the last successful sync began. Null when the table has to be fingerprinted.
    /// </summary>
    private static string? CheapVerdict(SqlConnection source, Member m, DbSyncTableState? previous, DbSyncSettings settings)
    {
        if (m.Table.Rows != m.TargetTable.Rows || previous?.StateJson is not { Length: > 0 } json) return null;
        State? last;
        try { last = JsonSerializer.Deserialize<State>(json); }
        catch (JsonException) { return null; }
        if (last is null) return null;

        using var cmd = new SqlCommand(
            """
            SELECT CASE WHEN (SELECT sqlserver_start_time FROM sys.dm_os_sys_info) < @clock
                         AND (SELECT is_auto_close_on FROM sys.databases WHERE database_id = DB_ID()) = 0 THEN 1 ELSE 0 END,
                   (SELECT MAX(last_user_update) FROM sys.dm_db_index_usage_stats WHERE database_id = DB_ID() AND object_id = @id)
            """, source) { CommandTimeout = settings.CommandTimeoutSeconds };
        cmd.Parameters.Add("@clock", SqlDbType.DateTime2).Value = last.SourceClock;
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = m.Table.ObjectId;
        try
        {
            using var rd = cmd.ExecuteReader();
            rd.Read();
            if (rd.GetInt32(0) != 1) return null;
            if (!rd.IsDBNull(1) && rd.GetDateTime(1) >= last.SourceClock) return null;
        }
        catch (SqlException) { return null; }
        return $"write statistics: no insert, update or delete in the source since {last.SourceClock:s}";
    }

    private sealed record Fingerprinted(long Count, string Print);

    /// <summary>
    /// COUNT plus a fingerprint of the rows of <c>table AS t {from} [WHERE {where}]</c> (Get-Fingerprint): the
    /// order-independent sum of the per-row SHA2-256 (strong), or exact per-column aggregates (medium).
    /// </summary>
    private static Fingerprinted Fingerprint(SqlConnection c, Member m, string from, string where, IReadOnlyList<SqlParameter> ps, DbSyncSettings settings)
    {
        var src = $"{m.Table.QName} AS t {from}" + (where.Length > 0 ? $" WHERE {where}" : "");
        string sql;
        if (m.Strong)
        {
            var sums = new[] { 1, 8, 15, 22 }.Select(o => $"CONVERT(VARCHAR(60), SUM(CAST(CAST(SUBSTRING(x.h, {o}, 7) AS BIGINT) AS DECIMAL(38,0))))");
            sql = $"{m.Prelude}SELECT COUNT_BIG(*), {string.Join(", ", sums)} FROM (SELECT {m.HashExpr} AS h FROM {src}) x";
        }
        else sql = "SELECT COUNT_BIG(*)" + string.Concat(m.MediumAggs.Select(a => ", " + a)) + $" FROM {src}";

        using var cmd = Command(c, null, settings, sql, ps);
        using var rd = cmd.ExecuteReader();
        rd.Read();
        var parts = Enumerable.Range(1, rd.FieldCount - 1).Select(i => rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture));
        return new Fingerprinted(rd.GetInt64(0), string.Join("|", parts));
    }

    private static long Count(SqlConnection c, Member m, DbSyncSettings settings) =>
        Convert.ToInt64(Scalar(c, null, settings, $"SELECT COUNT_BIG(*) FROM {m.Table.QName}"), CultureInfo.InvariantCulture);

    private sealed record AppendPlan(string From, SqlParameter[] Params, string Reason);

    /// <summary>
    /// An append plan when the target's rows are exactly the source's rows at or below the target's (per-group)
    /// watermark (Test-Append), verified by fingerprint; null when the table is not append-only.
    /// </summary>
    private static AppendPlan? TestAppend(SqlConnection source, SqlConnection target, Member m, DbSyncSettings settings)
    {
        var (groups, w) = m.AppendSpec!.Value;
        var wq = Q(w.Name);
        var gsel = groups.Select((g, i) => $"{Q(g.Name)} AS g{i}");
        var json = new StringBuilder();
        var sql = groups.Count > 0
            ? $"SELECT {string.Join(", ", gsel)}, MAX({wq}) AS m FROM {m.Table.QName} GROUP BY {string.Join(", ", groups.Select(g => Q(g.Name)))} FOR JSON PATH"
            : $"SELECT MAX({wq}) AS m FROM {m.Table.QName} FOR JSON PATH";
        using (var cmd = Command(target, null, settings, sql, []))
        using (var rd = cmd.ExecuteReader())
            while (rd.Read()) json.Append(rd.GetString(0));
        if (json.Length == 0) return null;

        var with = groups.Select((g, i) => $"g{i} {g.TypeDecl} '$.g{i}'").Append($"m {w.TypeDecl} '$.m'");
        var on = groups.Select((g, i) => $"t.{Q(g.Name)} = w.g{i}" + (g.Collation is { } coll ? $" COLLATE {coll}" : "")).ToList();
        var oj = $"OPENJSON(@wm) WITH ({string.Join(", ", with)}) AS w";
        var onSql = on.Count > 0 ? string.Join(" AND ", on) : "1 = 1";
        SqlParameter[] Params() => [new("@wm", SqlDbType.NVarChar, -1) { Value = json.ToString() }];
        var order = string.Join(", ", groups.Select(g => $"t.{Q(g.Name)}").Append($"t.{wq}"));
        var from = groups.Count > 0
            ? $"LEFT JOIN {oj} ON {onSql} WHERE w.m IS NULL OR t.{wq} > w.m ORDER BY {order}"
            : $"WHERE t.{wq} > (SELECT w.m FROM {oj}) ORDER BY {order}";

        var ft = Fingerprint(target, m, "", "", [], settings);
        var fs = Fingerprint(source, m, $"JOIN {oj} ON {onSql} AND t.{wq} <= w.m", "", Params(), settings);
        if (ft != fs) return null;
        var along = (groups.Count > 0 ? $"({string.Join(",", groups.Select(g => g.Name))}) / " : "") + w.Name;
        return new AppendPlan(from, Params(), $"the target's {ft.Count:n0} rows are a prefix of the source's along {along}; only the new rows are copied");
    }

    private sealed record DiffRange(string Where, object[]? Lo, object[]? Hi, List<string> Delete, List<string> Insert);

    private sealed record DiffPlan(List<DiffRange> Ranges, string Reason);

    /// <summary>
    /// Per key range (Get-HashDiffPlan), the hashes whose row counts differ between the sides: target rows to delete and
    /// source rows to insert. With more than one range, a range whose fingerprints agree is skipped unread.
    /// </summary>
    private static DiffPlan HashDiffPlan(SqlConnection source, SqlConnection target, Member m, long srcRows, DbSyncSettings settings)
    {
        var ranges = Ranges(source, m, srcRows, settings);
        var plan = new List<DiffRange>();
        long delRows = 0, insRows = 0;
        foreach (var (lo, hi) in ranges)
        {
            var where = RangePredicate(m.RangeCols, lo, hi);
            SqlParameter[] Params() => RangeParams(m.RangeCols, lo, hi);
            if (ranges.Count > 1 && Fingerprint(target, m, "", where, Params(), settings) == Fingerprint(source, m, "", where, Params(), settings))
                continue;
            var ht = HashCounts(target, m, where, Params(), settings);
            var hs = HashCounts(source, m, where, Params(), settings);
            var del = new List<string>();
            var ins = new List<string>();
            foreach (var (h, n) in ht)
                if (hs.GetValueOrDefault(h) != n) { del.Add(h); delRows += n; }
            foreach (var (h, n) in hs)
                if (ht.GetValueOrDefault(h) != n) { ins.Add(h); insRows += n; }
            if (del.Count > 0 || ins.Count > 0) plan.Add(new DiffRange(where, lo, hi, del, ins));
        }
        return new DiffPlan(plan, plan.Count == 0
            ? $"row hashes agree in all {ranges.Count} range(s)"
            : $"{ranges.Count} key range(s), {plan.Count} differ: delete {delRows:n0} target rows, insert {insRows:n0} source rows");
    }

    /// <summary>
    /// Key ranges of about <see cref="DbSyncSettings.HashDiffRangeRows"/> source rows each (Get-Ranges); one range when
    /// the table is that small or has no usable range key. A bound is a key tuple; a range is <c>(lo, hi]</c>.
    /// </summary>
    private static List<(object[]? Lo, object[]? Hi)> Ranges(SqlConnection source, Member m, long srcRows, DbSyncSettings settings)
    {
        if (srcRows <= settings.HashDiffRangeRows || m.RangeCols.Count == 0) return [(null, null)];
        var bounds = new List<object[]>();
        string sql;
        if (m.RangeKind == "btree")
        {
            var cl = string.Join(", ", m.RangeCols.Select(c => Q(c.Name)));
            sql = $"SELECT {cl} FROM (SELECT {cl}, ROW_NUMBER() OVER (ORDER BY {cl}) AS rn FROM {m.Table.QName}) q WHERE q.rn % @n = 0 ORDER BY q.rn";
        }
        else sql = $"SELECT {Q(m.RangeCols[0].Name)}, COUNT_BIG(*) FROM {m.Table.QName} GROUP BY {Q(m.RangeCols[0].Name)} ORDER BY {Q(m.RangeCols[0].Name)}";

        using (var cmd = Command(source, null, settings, sql, [new SqlParameter("@n", SqlDbType.BigInt) { Value = (long)settings.HashDiffRangeRows }]))
        using (var rd = cmd.ExecuteReader())
        {
            long acc = 0;
            while (rd.Read())
            {
                if (m.RangeKind == "btree")
                {
                    var row = new object[m.RangeCols.Count];
                    rd.GetValues(row);
                    bounds.Add(row);
                }
                else if ((acc += rd.GetInt64(1)) >= settings.HashDiffRangeRows)
                {
                    bounds.Add([rd.GetValue(0)]);
                    acc = 0;
                }
            }
        }
        var ranges = new List<(object[]?, object[]?)>();
        object[]? prev = null;
        foreach (var b in bounds) { ranges.Add((prev, b)); prev = b; }
        ranges.Add((prev, null));
        return ranges;
    }

    /// <summary>The <c>(lo, hi]</c> predicate on the key tuple, lexicographic (Get-RangePredicate); "" for the whole table.</summary>
    private static string RangePredicate(IReadOnlyList<CatalogColumn> cols, object[]? lo, object[]? hi)
    {
        var parts = new List<string>();
        foreach (var (v, p, neg) in new[] { (lo, "lo", false), (hi, "hi", true) })
        {
            if (v is null) continue;
            var ors = Enumerable.Range(0, v.Length).Select(i =>
                "(" + string.Join(" AND ", Enumerable.Range(0, i).Select(j => $"t.{Q(cols[j].Name)} = @{p}{j}").Append($"t.{Q(cols[i].Name)} > @{p}{i}")) + ")");
            var e = "(" + string.Join(" OR ", ors) + ")";
            parts.Add(neg ? $"NOT {e}" : e);
        }
        return string.Join(" AND ", parts);
    }

    private static SqlParameter[] RangeParams(IReadOnlyList<CatalogColumn> cols, object[]? lo, object[]? hi)
    {
        var ps = new List<SqlParameter>();
        foreach (var (v, p) in new[] { (lo, "lo"), (hi, "hi") })
            for (var i = 0; v is not null && i < v.Length; i++) ps.Add(TypedParam($"@{p}{i}", cols[i], v[i]));
        return [.. ps];
    }

    /// <summary>A parameter declared with the column's own type, so a bound compares at the column's precision.</summary>
    private static SqlParameter TypedParam(string name, CatalogColumn c, object value)
    {
        var type = c.Type switch
        {
            "numeric" => SqlDbType.Decimal,
            _ => Enum.Parse<SqlDbType>(c.Type, ignoreCase: true),
        };
        var p = new SqlParameter(name, type) { Value = value };
        switch (c.Type)
        {
            case "char" or "varchar" or "binary" or "varbinary": p.Size = c.MaxLength; break;
            case "nchar" or "nvarchar": p.Size = c.MaxLength / 2; break;
            case "decimal" or "numeric": p.Precision = (byte)c.Precision; p.Scale = (byte)c.Scale; break;
            case "datetime2" or "time" or "datetimeoffset": p.Scale = (byte)c.Scale; break;
        }
        return p;
    }

    /// <summary>Each row hash (upper-case hex) in the range with how many rows have it (Get-HashCounts).</summary>
    private static Dictionary<string, int> HashCounts(SqlConnection c, Member m, string where, SqlParameter[] ps, DbSyncSettings settings)
    {
        var sql = $"{m.Prelude}SELECT {m.HashExpr} FROM {m.Table.QName} AS t" + (where.Length > 0 ? $" WHERE {where}" : "");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cmd = Command(c, null, settings, sql, ps);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var h = Convert.ToHexString((byte[])rd.GetValue(0));
            counts[h] = counts.GetValueOrDefault(h) + 1;
        }
        return counts;
    }

    private static string HashWhere(string rangeWhere, Member m) =>
        (rangeWhere.Length > 0 ? $"{rangeWhere} AND " : "") + $"{m.HashExpr} {HashIn}";

    private static SqlParameter HashJson(IEnumerable<string> hashes) =>
        new("@hj", SqlDbType.NVarChar, -1) { Value = "[\"" + string.Join("\",\"", hashes) + "\"]" };

    /// <summary>Streams the source rows of <c>table AS t {from}</c> into the target table (Copy-Rows); returns how many.</summary>
    private static long CopyRows(SqlConnection source, SqlConnection target, SqlTransaction tx, Member m, string from, SqlParameter[] ps, DbSyncSettings settings)
    {
        if (m.Table.VectorColumns.Count > 0)
        {
            var native = VectorText.NativeColumns(source, m.Table, m.Columns, settings);
            using var vcmd = Command(source, null, settings,
                $"{m.Prelude}SELECT {string.Join(", ", m.Columns.Select(c => VectorText.Select(c, native)))} FROM {m.Table.QName} AS t {from}", ps);
            using var vrd = vcmd.ExecuteReader();
            return VectorText.CopyThroughStage(vrd, target, tx, m.Table, m.Columns, native, settings);
        }

        using var cmd = Command(source, null, settings, $"SELECT {string.Join(", ", m.Columns.Select(c => "t." + Q(c.Name)))} FROM {m.Table.QName} AS t {from}", ps);
        using var rd = cmd.ExecuteReader();
        using var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, tx)
        {
            DestinationTableName = m.Table.QName,
            BatchSize = settings.BulkBatchSize,
            BulkCopyTimeout = settings.CommandTimeoutSeconds,
            EnableStreaming = true,
        };
        foreach (var c in m.Columns) bulk.ColumnMappings.Add(c.Name, c.Name);
        bulk.WriteToServer(rd);
        return bulk.RowsCopied64;
    }

    private static SqlCommand Command(SqlConnection c, SqlTransaction? tx, DbSyncSettings settings, string sql, IEnumerable<SqlParameter> ps)
    {
        var cmd = new SqlCommand(sql, c, tx) { CommandTimeout = settings.CommandTimeoutSeconds };
        foreach (var p in ps) cmd.Parameters.Add(p);
        return cmd;
    }

    private static object Scalar(SqlConnection c, SqlTransaction? tx, DbSyncSettings settings, string sql)
    {
        using var cmd = Command(c, tx, settings, sql, []);
        return cmd.ExecuteScalar();
    }

    private static int Exec(SqlConnection c, SqlTransaction tx, DbSyncSettings settings, string sql, IEnumerable<SqlParameter> ps)
    {
        using var cmd = Command(c, tx, settings, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    private static string Q(string name) => TableCatalog.Quote(name);

    /// <summary>One table's SQL pieces (Initialize-Member): row hash, fingerprint aggregates, range key and append spec.</summary>
    private sealed class Member
    {
        public required CatalogTable Table { get; init; }
        public required CatalogTable TargetTable { get; init; }
        public required IReadOnlyList<CatalogColumn> Columns { get; init; }
        /// <summary>Declarations every batch that uses <see cref="HashExpr"/> starts with (the VECTOR unit vectors).</summary>
        public required string Prelude { get; init; }
        public required string HashExpr { get; init; }
        public required IReadOnlyList<string> MediumAggs { get; init; }
        public required bool Strong { get; init; }
        public required IReadOnlyList<CatalogColumn> RangeCols { get; init; }
        public string? RangeKind { get; init; }
        public (IReadOnlyList<CatalogColumn> Groups, CatalogColumn W)? AppendSpec { get; init; }

        public static Member Create(TableCatalog sourceCatalog, TableCatalog targetCatalog, string key, DbSyncSettings settings)
        {
            if (!sourceCatalog.Tables.TryGetValue(key, out var t)) throw new InvalidOperationException($"{key} is not a table in the source.");
            if (!targetCatalog.Tables.TryGetValue(key, out var tt)) throw new InvalidOperationException($"{key} is not a table in the target.");
            if (t.Unsupported.Count > 0)
                throw new InvalidOperationException($"{key} is not supported ({string.Join(", ", t.Unsupported)}).");
            if (t.CopyUnsupported.Count > 0)
                throw new InvalidOperationException($"{key} has columns the sync cannot copy: {string.Join(", ", t.CopyUnsupported)}.");

            var cols = t.CopyColumns;
            var wide = cols.Any(c => c.MaxLength == -1 || LobTypes.Contains(c.Type)) || cols.Sum(c => Math.Abs(c.MaxLength) + 9) > 7800;
            var bt = wide ? "VARBINARY(MAX)" : "VARBINARY(8000)";
            var terms = new List<string>();
            var aggs = new List<string>();
            foreach (var c in cols)
            {
                var q = "t." + Q(c.Name);
                var special = true;
                string bin;
                switch (c.Type)
                {
                    case "text": bin = $"CAST(CAST({q} AS VARCHAR(MAX)) AS VARBINARY(MAX))"; special = false; break;
                    case "ntext": bin = $"CAST(CAST({q} AS NVARCHAR(MAX)) AS VARBINARY(MAX))"; special = false; break;
                    case "xml" or "json": bin = $"CAST(CAST({q} AS NVARCHAR(MAX)) AS VARBINARY(MAX))"; break;
                    case "sql_variant":
                        bin = $"(CAST(CAST(SQL_VARIANT_PROPERTY({q}, 'BaseType') AS NVARCHAR(128)) AS VARBINARY(MAX)) + CAST(CAST({q} AS VARBINARY(8000)) AS VARBINARY(MAX)))";
                        break;
                    case "image": bin = $"CAST({q} AS VARBINARY(MAX))"; special = false; break;
                    case "vector": bin = $"CAST({VectorText.Expr(c)} AS VARBINARY(MAX))"; break;
                    default: bin = $"CAST({q} AS {bt})"; special = false; break;
                }
                // A VECTOR's exact text is bracketed, so it delimits itself; it is built once per row, not twice.
                if (c.IsVector) terms.Add($"ISNULL({bin}, 0xFFFFFFFFFFFFFFFF)");
                else if (FixedTypes.Contains(c.Type)) terms.Add(c.Nullable ? $"ISNULL(0x01 + {bin}, 0x00)" : bin);
                else terms.Add($"ISNULL(CAST(DATALENGTH({(special ? bin : q)}) AS BINARY(8)) + {bin}, 0xFFFFFFFFFFFFFFFF)");

                switch (c.Type)
                {
                    case "bigint" or "int" or "smallint" or "tinyint" or "bit": aggs.Add($"CONVERT(VARCHAR(60), SUM(CAST({q} AS DECIMAL(38,0))))"); break;
                    case "decimal" or "numeric" when c.Precision <= 28: aggs.Add($"CONVERT(VARCHAR(60), SUM(CAST({q} AS DECIMAL(38,{c.Scale}))))"); break;
                    case "money" or "smallmoney": aggs.Add($"CONVERT(VARCHAR(60), SUM(CAST({q} AS DECIMAL(38,4))))"); break;
                    case "float" or "uniqueidentifier": aggs.Add($"CONVERT(VARCHAR(60), SUM(CAST(CAST(CAST({q} AS BINARY(8)) AS BIGINT) AS DECIMAL(38,0))))"); break;
                    case "real": aggs.Add($"CONVERT(VARCHAR(60), SUM(CAST(CAST(CAST({q} AS BINARY(4)) AS INT) AS DECIMAL(38,0))))"); break;
                    case "date" or "datetime" or "datetime2" or "smalldatetime" or "time":
                        aggs.Add($"CONVERT(VARCHAR(40), MIN({q}), 121)"); aggs.Add($"CONVERT(VARCHAR(40), MAX({q}), 121)"); break;
                    case "datetimeoffset": aggs.Add($"CONVERT(VARCHAR(40), MIN({q}), 127)"); aggs.Add($"CONVERT(VARCHAR(40), MAX({q}), 127)"); break;
                    case "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" or "text" or "ntext" or "image":
                        aggs.Add($"CONVERT(VARCHAR(60), SUM(CAST(DATALENGTH({q}) AS DECIMAL(38,0))))"); break;
                }
            }

            // Range columns: how the table is cut into key ranges.
            List<string> rc = [];
            string? kind = null;
            if (t.PrimaryKey.Count > 0) { rc = t.PrimaryKey; kind = "btree"; }
            else if (t.ClusteredKey.Count > 0) { rc = t.ClusteredKey; kind = "btree"; }
            else if (t.ColumnstoreOrder.Count > 0) { rc = t.ColumnstoreOrder; kind = "cci"; }
            var rangeCols = rc.Select(n => t.Column(n)!).ToList();
            var ok = rangeCols.Count > 0 && rangeCols.All(c => !c.Nullable && c.MaxLength != -1 && RangeTypes.Contains(c.Type));
            if (!ok) { rangeCols = []; kind = null; }

            // Append spec: a watermark column plus optional group columns; the target must be a per-group prefix along it.
            (IReadOnlyList<CatalogColumn>, CatalogColumn)? append = null;
            if (t.TemporalType == 1)
            {
                var pair = sourceCatalog.TemporalPairs.FirstOrDefault(p => p.HistoryKey == key);
                if (pair is not null && t.Column(pair.PeriodEnd) is { Nullable: false } w) append = ([], w);
            }
            else if (ok)
            {
                var w = rangeCols[^1];
                var groups = rangeCols.Take(rangeCols.Count - 1).ToList();
                if (WatermarkTypes.Contains(w.Type) && groups.All(g => JsonKeyTypes.Contains(g.Type))) append = (groups, w);
            }

            return new Member
            {
                Table = t,
                TargetTable = tt,
                Columns = cols,
                Prelude = VectorText.Declare(cols),
                HashExpr = $"HASHBYTES('SHA2_256', CAST(0x AS {bt}) + {string.Join(" + ", terms)})",
                MediumAggs = aggs,
                Strong = Math.Max(t.MB, tt.MB) <= settings.StrongHashMaxMB,
                RangeCols = rangeCols,
                RangeKind = kind,
                AppendSpec = append,
            };
        }
    }
}
