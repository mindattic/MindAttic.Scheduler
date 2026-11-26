using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>What one incremental table sync wrote to the target.</summary>
public sealed record TableSyncResult(string Table, int Upserted, int Deleted);

/// <summary>
/// Incremental sync of one change-tracked table (M.SCHD-T65): every key
/// <c>CHANGETABLE(CHANGES t, @lastVersion)</c> names is read with the source row LEFT JOINed on the
/// primary key. A key whose row still exists is upserted with its current values; a key whose row is
/// gone is deleted. Both happen in one target transaction, through a staged MERGE. Upserts are
/// idempotent, so a row changed after the run's start version and delivered again next run is harmless.
/// Temporal period columns are not written (TemporalPairSync owns them).
/// </summary>
public static class ChangeTrackedTableSync
{
    private const string Stage = "#dbsync_stage";
    private const string Deleted = "#dbsync_deleted";

    /// <summary>
    /// Applies the source's changes to <paramref name="table"/> since <paramref name="lastVersion"/>
    /// to the target. Throws when the table cannot be synced this way or <paramref name="lastVersion"/>
    /// is older than the source's change retention.
    /// </summary>
    public static TableSyncResult Apply(SqlConnection source, SqlConnection target, CatalogTable table, long lastVersion, DbSyncSettings settings)
    {
        if (table.PrimaryKey.Count == 0)
            throw new InvalidOperationException($"{table.Key} has no primary key, so change tracking cannot sync it.");
        if (table.CopyUnsupported.Count > 0)
            throw new InvalidOperationException($"{table.Key} has columns the sync cannot copy: {string.Join(", ", table.CopyUnsupported)}.");

        CheckVersion(source, table, lastVersion, settings);

        var columns = table.CopyColumns.Where(c => c.Generated == 0).ToList();
        var key = table.PrimaryKey.Select(k => table.Column(k)!).ToList();
        var native = NativeVectorColumns(source, table, columns, settings);

        using var tx = target.BeginTransaction();
        Exec(target, tx, settings,
            $"DROP TABLE IF EXISTS {Stage}; DROP TABLE IF EXISTS {Deleted};" +
            $"SELECT TOP 0 {string.Join(", ", columns.Select(c => c.IsVector ? $"CAST(NULL AS NVARCHAR(MAX)) AS {Q(c.Name)}" : Q(c.Name)))} INTO {Stage} FROM {table.QName};" +
            $"SELECT TOP 0 {string.Join(", ", key.Select(c => Q(c.Name)))} INTO {Deleted} FROM {table.QName};");

        var upserts = NewBuffer(columns);
        var deletes = NewBuffer(key);
        using (var cmd = new SqlCommand(ChangesSql(table, columns, key, native), source) { CommandTimeout = settings.CommandTimeoutSeconds })
        {
            cmd.Parameters.Add("@v", SqlDbType.BigInt).Value = lastVersion;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                if (rd.GetInt32(0) == 1)
                {
                    var row = upserts.NewRow();
                    for (var i = 0; i < columns.Count; i++)
                        row[i] = ReadValue(rd, 1 + key.Count + i, native.Contains(columns[i].Name));
                    upserts.Rows.Add(row);
                    if (upserts.Rows.Count >= settings.BulkBatchSize) Flush(target, tx, Stage, upserts, settings);
                }
                else
                {
                    var row = deletes.NewRow();
                    for (var i = 0; i < key.Count; i++) row[i] = rd.GetValue(1 + i);
                    deletes.Rows.Add(row);
                    if (deletes.Rows.Count >= settings.BulkBatchSize) Flush(target, tx, Deleted, deletes, settings);
                }
            }
        }
        Flush(target, tx, Stage, upserts, settings);
        Flush(target, tx, Deleted, deletes, settings);

        var on = string.Join(" AND ", key.Select(c => $"t.{Q(c.Name)} = s.{Q(c.Name)}"));
        var deleted = Exec(target, tx, settings, $"DELETE t FROM {table.QName} AS t JOIN {Deleted} AS s ON {on};");
        var upserted = Exec(target, tx, settings, MergeSql(table, columns, on));
        Exec(target, tx, settings, $"DROP TABLE {Stage}; DROP TABLE {Deleted};");
        tx.Commit();

        return new TableSyncResult(table.Key, upserted, deleted);
    }

    private static void CheckVersion(SqlConnection source, CatalogTable table, long lastVersion, DbSyncSettings settings)
    {
        using var cmd = new SqlCommand("SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@t))", source) { CommandTimeout = settings.CommandTimeoutSeconds };
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 600).Value = table.QName;
        var min = cmd.ExecuteScalar();
        if (min is null or DBNull)
            throw new InvalidOperationException($"Change tracking is not enabled on {table.Key} in the source.");
        if (lastVersion < Convert.ToInt64(min, CultureInfo.InvariantCulture))
            throw new InvalidOperationException(
                $"{table.Key}: version {lastVersion} is older than the source's change retention (minimum valid version {min}); the table needs a full resync.");
    }

    /// <summary>VECTOR columns SqlClient receives natively from this server; the others are read through the exact-text path.</summary>
    private static HashSet<string> NativeVectorColumns(SqlConnection source, CatalogTable table, List<CatalogColumn> columns, DbSyncSettings settings)
    {
        var vectors = columns.Where(c => c.IsVector).ToList();
        var native = new HashSet<string>(StringComparer.Ordinal);
        if (vectors.Count == 0) return native;

        using var cmd = new SqlCommand($"SELECT TOP 0 {string.Join(", ", vectors.Select(c => Q(c.Name)))} FROM {table.QName}", source)
        {
            CommandTimeout = settings.CommandTimeoutSeconds,
        };
        using var rd = cmd.ExecuteReader();
        for (var i = 0; i < vectors.Count; i++)
            if (rd.GetFieldType(i) == typeof(SqlVector<float>) || string.Equals(rd.GetDataTypeName(i), "vector", StringComparison.OrdinalIgnoreCase))
                native.Add(vectors[i].Name);
        return native;
    }

    private static string ChangesSql(CatalogTable table, List<CatalogColumn> columns, List<CatalogColumn> key, HashSet<string> native)
    {
        var prelude = new StringBuilder();
        var select = new List<string> { $"CASE WHEN t.{Q(key[0].Name)} IS NULL THEN 0 ELSE 1 END" };
        select.AddRange(key.Select(c => $"ct.{Q(c.Name)}"));
        foreach (var c in columns)
        {
            if (c.IsVector && !native.Contains(c.Name))
            {
                var (declare, expr) = VectorExact(c);
                prelude.Append(declare);
                select.Add($"{expr} AS {Q(c.Name)}");
            }
            else select.Add($"t.{Q(c.Name)}");
        }
        var on = string.Join(" AND ", key.Select(c => $"t.{Q(c.Name)} = ct.{Q(c.Name)}"));
        return $"{prelude}SELECT {string.Join(", ", select)} FROM CHANGETABLE(CHANGES {table.QName}, @v) AS ct LEFT JOIN {table.QName} AS t ON {on};";
    }

    /// <summary>
    /// Exact text of a float32 VECTOR (Sync-ToAzure.ps1's Get-VectorExact): the server's own text form keeps
    /// only 8 significant digits, which does not round-trip, so element i is recovered exactly as the dot
    /// product with unit vector i and printed with 17 digits (style 3).
    /// </summary>
    private static (string Declare, string Expr) VectorExact(CatalogColumn c)
    {
        var dims = c.VectorDimensions;
        var prefix = $"@sv{c.Id}_";
        var q = "t." + Q(c.Name);
        var declare = new StringBuilder();
        for (var i = 0; i < dims; i++)
            declare.Append($"DECLARE {prefix}{i} VECTOR({dims}) = CAST('[' + REPLICATE(CAST('0,' AS VARCHAR(MAX)), {i}) + '1' + REPLICATE(CAST(',0' AS VARCHAR(MAX)), {dims - 1 - i}) + ']' AS VECTOR({dims}));\n");
        var chunks = new List<string>();
        for (var s = 0; s < dims; s += 200)
        {
            var terms = Enumerable.Range(s, Math.Min(dims, s + 200) - s)
                .Select(i => $"CONVERT(VARCHAR(30), -VECTOR_DISTANCE('dot', {q}, {prefix}{i}), 3)");
            chunks.Add($"CONCAT_WS(',', {string.Join(", ", terms)})");
        }
        return (declare.ToString(), $"CASE WHEN {q} IS NULL THEN NULL ELSE CAST('[' AS VARCHAR(MAX)) + {string.Join(" + ',' + ", chunks)} + ']' END");
    }

    private static object ReadValue(SqlDataReader rd, int ordinal, bool nativeVector)
    {
        if (rd.IsDBNull(ordinal)) return DBNull.Value;
        if (!nativeVector) return rd.GetValue(ordinal);

        // A float32 widened to double is exact, and its round-trip text parses back to the same float32.
        var v = rd.GetSqlVector<float>(ordinal);
        if (v.IsNull) return DBNull.Value;
        var text = new StringBuilder("[");
        var span = v.Memory.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(((double)span[i]).ToString("R", CultureInfo.InvariantCulture));
        }
        return text.Append(']').ToString();
    }

    private static string MergeSql(CatalogTable table, List<CatalogColumn> columns, string on)
    {
        string Source(CatalogColumn c) => c.IsVector ? $"CAST(s.{Q(c.Name)} AS {c.TypeDecl})" : $"s.{Q(c.Name)}";

        var set = columns.Where(c => !c.Identity && !table.PrimaryKey.Contains(c.Name)).ToList();
        var sql = new StringBuilder();
        var identity = columns.Any(c => c.Identity);
        if (identity) sql.Append($"SET IDENTITY_INSERT {table.QName} ON;\n");
        sql.Append($"MERGE {table.QName} WITH (HOLDLOCK) AS t USING {Stage} AS s ON {on}\n");
        if (set.Count > 0)
            sql.Append($"WHEN MATCHED THEN UPDATE SET {string.Join(", ", set.Select(c => $"t.{Q(c.Name)} = {Source(c)}"))}\n");
        sql.Append($"WHEN NOT MATCHED BY TARGET THEN INSERT ({string.Join(", ", columns.Select(c => Q(c.Name)))}) ")
           .Append($"VALUES ({string.Join(", ", columns.Select(Source))});\n");
        if (identity) sql.Append($"SET IDENTITY_INSERT {table.QName} OFF;\n");
        return sql.ToString();
    }

    private static DataTable NewBuffer(IEnumerable<CatalogColumn> columns)
    {
        var dt = new DataTable();
        foreach (var c in columns) dt.Columns.Add(c.Name, typeof(object));
        return dt;
    }

    private static void Flush(SqlConnection target, SqlTransaction tx, string into, DataTable rows, DbSyncSettings settings)
    {
        if (rows.Rows.Count == 0) return;
        using var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, tx)
        {
            DestinationTableName = into,
            BatchSize = settings.BulkBatchSize,
            BulkCopyTimeout = settings.CommandTimeoutSeconds,
        };
        foreach (DataColumn c in rows.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        bulk.WriteToServer(rows);
        rows.Clear();
    }

    private static int Exec(SqlConnection target, SqlTransaction tx, DbSyncSettings settings, string sql)
    {
        using var cmd = new SqlCommand(sql, target, tx) { CommandTimeout = settings.CommandTimeoutSeconds };
        return cmd.ExecuteNonQuery();
    }

    private static string Q(string name) => TableCatalog.Quote(name);
}
