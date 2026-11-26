using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>
/// VECTOR values carried exactly as text (Sync-ToAzure.ps1's Get-VectorExact), shared by every table sync: the server's
/// own text form keeps only 8 significant digits, which does not round-trip a float32, so a VECTOR is read either
/// natively (when SqlClient receives it as <see cref="SqlVector{T}"/>) and printed round-trip, or as the exact text
/// <see cref="Expr"/> builds, and written by staging the text in an NVARCHAR(MAX) column and casting it back.
/// </summary>
internal static class VectorText
{
    private const string Stage = "#dbsync_vstage";

    /// <summary>The VECTOR columns among <paramref name="columns"/> that SqlClient receives natively from this server.</summary>
    public static HashSet<string> NativeColumns(SqlConnection source, CatalogTable table, IEnumerable<CatalogColumn> columns, DbSyncSettings settings)
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

    /// <summary>The unit-vector variables <see cref="Expr"/> needs, declared once at the head of the batch.</summary>
    public static string Declare(IEnumerable<CatalogColumn> columns)
    {
        var declare = new StringBuilder();
        foreach (var c in columns.Where(c => c.IsVector))
        {
            var dims = c.VectorDimensions;
            for (var i = 0; i < dims; i++)
                declare.Append($"DECLARE {Unit(c, i)} VECTOR({dims}) = CAST('[' + REPLICATE(CAST('0,' AS VARCHAR(MAX)), {i}) + '1' + REPLICATE(CAST(',0' AS VARCHAR(MAX)), {dims - 1 - i}) + ']' AS VECTOR({dims}));\n");
        }
        return declare.ToString();
    }

    /// <summary>
    /// The exact text of <c>t.column</c> (NULL for NULL): element i is recovered exactly as the dot product with unit
    /// vector i and printed with 17 digits (style 3). Needs <see cref="Declare"/> in the same batch.
    /// </summary>
    public static string Expr(CatalogColumn c)
    {
        var dims = c.VectorDimensions;
        var q = "t." + Q(c.Name);
        var chunks = new List<string>();
        for (var s = 0; s < dims; s += 200)
        {
            var terms = Enumerable.Range(s, Math.Min(dims, s + 200) - s)
                .Select(i => $"CONVERT(VARCHAR(30), -VECTOR_DISTANCE('dot', {q}, {Unit(c, i)}), 3)");
            chunks.Add($"CONCAT_WS(',', {string.Join(", ", terms)})");
        }
        return $"CASE WHEN {q} IS NULL THEN NULL ELSE CAST('[' AS VARCHAR(MAX)) + {string.Join(" + ',' + ", chunks)} + ']' END";
    }

    /// <summary>The select-list item for <paramref name="c"/> read from <c>table AS t</c>.</summary>
    public static string Select(CatalogColumn c, HashSet<string> native) =>
        c.IsVector && !native.Contains(c.Name) ? $"{Expr(c)} AS {Q(c.Name)}" : $"t.{Q(c.Name)}";

    /// <summary>A column value as the sync writes it to a stage: a VECTOR as its exact text.</summary>
    public static object Read(SqlDataReader rd, int ordinal, bool native)
    {
        if (rd.IsDBNull(ordinal)) return DBNull.Value;
        if (!native) return rd.GetValue(ordinal);

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

    /// <summary>The stage column for <paramref name="c"/> in a <c>SELECT TOP 0 … INTO</c>: a VECTOR becomes NVARCHAR(MAX).</summary>
    public static string StageColumn(CatalogColumn c) => c.IsVector ? $"CAST(NULL AS NVARCHAR(MAX)) AS {Q(c.Name)}" : Q(c.Name);

    /// <summary>The staged value of <paramref name="c"/> as the target column's type.</summary>
    public static string FromStage(CatalogColumn c, string alias) => c.IsVector ? $"CAST({alias}.{Q(c.Name)} AS {c.TypeDecl})" : $"{alias}.{Q(c.Name)}";

    /// <summary>
    /// Inserts every row of <paramref name="rd"/> (the <paramref name="columns"/> in order, VECTORs as <see cref="Select"/>
    /// reads them) into <paramref name="table"/> on the target, in batches staged through a temp table so each VECTOR
    /// is cast from its exact text. Identity values are kept. Returns how many rows were inserted.
    /// </summary>
    public static long CopyThroughStage(SqlDataReader rd, SqlConnection target, SqlTransaction? tx, CatalogTable table,
        IReadOnlyList<CatalogColumn> columns, HashSet<string> native, DbSyncSettings settings)
    {
        Exec(target, tx, settings, $"DROP TABLE IF EXISTS {Stage}; SELECT TOP 0 {string.Join(", ", columns.Select(StageColumn))} INTO {Stage} FROM {table.QName};");

        var identity = columns.Any(c => c.Identity);
        var insert = (identity ? $"SET IDENTITY_INSERT {table.QName} ON;\n" : "") +
                     $"INSERT {table.QName} ({string.Join(", ", columns.Select(c => Q(c.Name)))}) " +
                     $"SELECT {string.Join(", ", columns.Select(c => FromStage(c, "s")))} FROM {Stage} AS s;\n" +
                     (identity ? $"SET IDENTITY_INSERT {table.QName} OFF;\n" : "") +
                     $"TRUNCATE TABLE {Stage};";

        var buffer = new DataTable();
        foreach (var c in columns) buffer.Columns.Add(c.Name, typeof(object));
        long total = 0;
        void Flush()
        {
            if (buffer.Rows.Count == 0) return;
            using (var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, tx)
                   {
                       DestinationTableName = Stage,
                       BatchSize = settings.BulkBatchSize,
                       BulkCopyTimeout = settings.CommandTimeoutSeconds,
                   })
            {
                foreach (DataColumn c in buffer.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
                bulk.WriteToServer(buffer);
            }
            Exec(target, tx, settings, insert);
            total += buffer.Rows.Count;
            buffer.Clear();
        }

        while (rd.Read())
        {
            var row = buffer.NewRow();
            for (var i = 0; i < columns.Count; i++) row[i] = Read(rd, i, native.Contains(columns[i].Name));
            buffer.Rows.Add(row);
            if (buffer.Rows.Count >= settings.BulkBatchSize) Flush();
        }
        Flush();
        Exec(target, tx, settings, $"DROP TABLE {Stage};");
        return total;
    }

    private static string Unit(CatalogColumn c, int i) => $"@sv{c.Id}_{i}";

    private static int Exec(SqlConnection c, SqlTransaction? tx, DbSyncSettings settings, string sql)
    {
        using var cmd = new SqlCommand(sql, c, tx) { CommandTimeout = settings.CommandTimeoutSeconds };
        return cmd.ExecuteNonQuery();
    }

    private static string Q(string name) => TableCatalog.Quote(name);
}
