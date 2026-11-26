using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>
/// Every user table of one database (ported from Sync-ToAzure.ps1's Get-Meta): columns, keys,
/// identity, temporal pairs, VECTOR columns, constraints and triggers. Reading it only runs a
/// SELECT, so it is safe on the source.
/// </summary>
public sealed class TableCatalog
{
    private const string MetaSql = """
        SELECT d.compatibility_level, d.collation_name FROM sys.databases d WHERE d.name = DB_NAME();
        SELECT t.object_id, s.name, t.name, t.temporal_type, hs.name, ht.name, t.is_memory_optimized, t.is_external, t.is_node, t.is_edge, t.ledger_type,
               t.history_retention_period, t.history_retention_period_unit_desc,
               ISNULL((SELECT SUM(ps.row_count) FROM sys.dm_db_partition_stats ps WHERE ps.object_id = t.object_id AND ps.index_id IN (0, 1)), 0),
               ISNULL((SELECT SUM(ps.used_page_count) FROM sys.dm_db_partition_stats ps WHERE ps.object_id = t.object_id), 0) / 128,
               CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = t.object_id AND i.type = 5) THEN 1 ELSE 0 END,
               CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.referenced_object_id = t.object_id) THEN 1 ELSE 0 END
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
        LEFT JOIN sys.tables ht ON ht.object_id = t.history_table_id LEFT JOIN sys.schemas hs ON hs.schema_id = ht.schema_id
        WHERE t.is_ms_shipped = 0;
        SELECT c.object_id, c.column_id, c.name,
               CASE WHEN ty.is_user_defined = 1 AND ty.is_assembly_type = 0 THEN TYPE_NAME(ty.system_type_id) ELSE ty.name END,
               c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, c.generated_always_type, c.is_hidden, c.collation_name,
               CASE WHEN c.encryption_type IS NULL THEN 0 ELSE 1 END
        FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        WHERE t.is_ms_shipped = 0 ORDER BY c.object_id, c.column_id;
        SELECT i.object_id, i.index_id, i.type, i.is_primary_key, i.is_unique, c.name, ic.key_ordinal, ic.column_store_order_ordinal
        FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id JOIN sys.tables t ON t.object_id = i.object_id
        WHERE t.is_ms_shipped = 0 AND (i.is_primary_key = 1 OR i.type IN (1, 5)) AND (ic.key_ordinal > 0 OR ic.column_store_order_ordinal > 0)
        ORDER BY i.object_id, i.index_id, ic.key_ordinal, ic.column_store_order_ordinal;
        SELECT p.object_id, sc.name, ec.name FROM sys.periods p
        JOIN sys.columns sc ON sc.object_id = p.object_id AND sc.column_id = p.start_column_id
        JOIN sys.columns ec ON ec.object_id = p.object_id AND ec.column_id = p.end_column_id;
        SELECT 'FK', fk.name COLLATE DATABASE_DEFAULT, fk.parent_object_id, fk.referenced_object_id, fk.is_disabled, fk.is_not_trusted,
               (SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), pc.name + N'>' + rc.name), N',') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
                FROM sys.foreign_key_columns fkc JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                WHERE fkc.constraint_object_id = fk.object_id) COLLATE DATABASE_DEFAULT
        FROM sys.foreign_keys fk
        UNION ALL SELECT 'CK', ck.name COLLATE DATABASE_DEFAULT, ck.parent_object_id, NULL, ck.is_disabled, ck.is_not_trusted, CONVERT(NVARCHAR(MAX), ck.definition) COLLATE DATABASE_DEFAULT FROM sys.check_constraints ck;
        SELECT tr.parent_id, tr.name, tr.is_disabled FROM sys.triggers tr JOIN sys.tables t ON t.object_id = tr.parent_id WHERE tr.parent_class = 1;
        SELECT ic.object_id, CONVERT(VARCHAR(40), ic.last_value) FROM sys.identity_columns ic JOIN sys.tables t ON t.object_id = ic.object_id WHERE t.is_ms_shipped = 0;
        """;

    public int CompatibilityLevel { get; private init; }
    public string Collation { get; private init; } = "";

    /// <summary>Tables by <c>schema.name</c>.</summary>
    public IReadOnlyDictionary<string, CatalogTable> Tables => tables;

    /// <summary>Foreign keys and check constraints.</summary>
    public IReadOnlyList<CatalogConstraint> Constraints => constraints;

    private readonly Dictionary<string, CatalogTable> tables = new(StringComparer.Ordinal);
    private readonly List<CatalogConstraint> constraints = [];

    /// <summary>Each system-versioned table with its history table and period columns.</summary>
    public IReadOnlyList<TemporalPair> TemporalPairs => tables.Values
        .Where(t => t.TemporalType == 2 && t.HistoryKey is not null && t.PeriodStart is not null && t.PeriodEnd is not null)
        .OrderBy(t => t.Key, StringComparer.Ordinal)
        .Select(t => new TemporalPair(t.Key, t.HistoryKey!, t.PeriodStart!, t.PeriodEnd!))
        .ToList();

    /// <summary>Foreign keys declared on <paramref name="tableKey"/> or referencing it.</summary>
    public IReadOnlyList<CatalogConstraint> ForeignKeysTouching(string tableKey) => constraints
        .Where(c => c.Kind == "FK" && (c.TableKey == tableKey || c.ReferencedTableKey == tableKey))
        .ToList();

    /// <summary><c>[name]</c>, with <c>]</c> doubled.</summary>
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>Reads the catalog of the database <paramref name="connection"/> is open on.</summary>
    public static TableCatalog Read(SqlConnection connection)
    {
        using var cmd = new SqlCommand(MetaSql, connection) { CommandTimeout = 0 };
        using var rd = cmd.ExecuteReader();

        rd.Read();
        var catalog = new TableCatalog { CompatibilityLevel = Convert.ToInt32(rd[0]), Collation = rd.GetString(1) };
        var byId = new Dictionary<int, CatalogTable>();

        rd.NextResult();
        while (rd.Read())
        {
            var unsupported = new List<string>();
            if (rd.GetBoolean(6)) unsupported.Add("memory-optimized");
            if (rd.GetBoolean(7)) unsupported.Add("external");
            if (rd.GetBoolean(8) || rd.GetBoolean(9)) unsupported.Add("graph");
            if (!rd.IsDBNull(10) && Convert.ToInt32(rd[10]) != 0) unsupported.Add("ledger");
            var t = new CatalogTable
            {
                ObjectId = rd.GetInt32(0),
                Schema = rd.GetString(1),
                Name = rd.GetString(2),
                TemporalType = Convert.ToInt32(rd[3]),
                HistoryKey = rd.IsDBNull(5) ? null : $"{rd.GetString(4)}.{rd.GetString(5)}",
                Unsupported = unsupported,
                HistoryRetentionPeriod = rd.IsDBNull(11) ? null : Convert.ToInt32(rd[11]),
                HistoryRetentionUnit = rd.IsDBNull(12) ? "" : rd.GetString(12),
                Rows = Convert.ToInt64(rd[13]),
                MB = Convert.ToInt64(rd[14]),
                HasClusteredColumnstore = Convert.ToInt32(rd[15]) == 1,
                ReferencedByForeignKey = Convert.ToInt32(rd[16]) == 1,
            };
            catalog.tables[t.Key] = t;
            byId[t.ObjectId] = t;
        }

        rd.NextResult();
        while (rd.Read())
        {
            if (!byId.TryGetValue(rd.GetInt32(0), out var t)) continue;
            t.Columns.Add(new CatalogColumn(
                Id: rd.GetInt32(1),
                Name: rd.GetString(2),
                Type: rd.GetString(3).ToLowerInvariant(),
                MaxLength: Convert.ToInt32(rd[4]),
                Precision: Convert.ToInt32(rd[5]),
                Scale: Convert.ToInt32(rd[6]),
                Nullable: rd.GetBoolean(7),
                Identity: rd.GetBoolean(8),
                Computed: rd.GetBoolean(9),
                Generated: rd.IsDBNull(10) ? 0 : Convert.ToInt32(rd[10]),
                Hidden: !rd.IsDBNull(11) && rd.GetBoolean(11),
                Collation: rd.IsDBNull(12) ? null : rd.GetString(12),
                Encrypted: Convert.ToInt32(rd[13]) == 1));
        }

        rd.NextResult();
        while (rd.Read())
        {
            if (!byId.TryGetValue(rd.GetInt32(0), out var t)) continue;
            var type = Convert.ToInt32(rd[2]);
            var column = rd.GetString(5);
            if (rd.GetBoolean(3)) t.PrimaryKey.Add(column);
            if (type == 1) t.ClusteredKey.Add(column);
            if (type == 5 && !rd.IsDBNull(7) && Convert.ToInt32(rd[7]) > 0) t.ColumnstoreOrder.Add(column);
        }

        rd.NextResult();
        while (rd.Read())
        {
            if (!byId.TryGetValue(rd.GetInt32(0), out var t)) continue;
            t.PeriodStart = rd.GetString(1);
            t.PeriodEnd = rd.GetString(2);
        }

        rd.NextResult();
        while (rd.Read())
        {
            if (!byId.TryGetValue(rd.GetInt32(2), out var t)) continue;
            var referenced = rd.IsDBNull(3) ? null : byId.GetValueOrDefault(rd.GetInt32(3));
            catalog.constraints.Add(new CatalogConstraint(
                Kind: rd.GetString(0),
                Name: rd.GetString(1),
                TableKey: t.Key,
                ReferencedTableKey: referenced?.Key,
                Disabled: rd.GetBoolean(4),
                NotTrusted: rd.GetBoolean(5),
                Definition: rd.IsDBNull(6) ? "" : rd.GetString(6)));
        }

        rd.NextResult();
        while (rd.Read())
            if (byId.TryGetValue(rd.GetInt32(0), out var t)) t.Triggers.Add(new CatalogTrigger(rd.GetString(1), rd.GetBoolean(2)));

        rd.NextResult();
        while (rd.Read())
            if (byId.TryGetValue(rd.GetInt32(0), out var t) && !rd.IsDBNull(1)) t.IdentityLastValue = rd.GetString(1);

        return catalog;
    }

    /// <summary>
    /// Compares the source's tables and columns with the target's (Compare-Schema). Null when they
    /// match; otherwise every difference, named. A target that is not versioned where the source is
    /// temporal is allowed: that is the state an interrupted run leaves, and the sync repairs it.
    /// </summary>
    public static SchemaMismatch? Compare(TableCatalog source, TableCatalog target, string sourceName = "source", string targetName = "target")
    {
        var sn = sourceName;
        var tn = targetName;
        var errors = new List<string>();
        var sourceKeys = source.tables.Keys.Order(StringComparer.Ordinal).ToList();

        foreach (var k in sourceKeys)
            if (!target.tables.ContainsKey(k)) errors.Add($"table {k} exists in {sn} but not in {tn}");
        foreach (var k in target.tables.Keys.Order(StringComparer.Ordinal))
            if (!source.tables.ContainsKey(k)) errors.Add($"table {k} exists in {tn} but not in {sn}");

        foreach (var k in sourceKeys)
        {
            if (!target.tables.TryGetValue(k, out var tt)) continue;
            var st = source.tables[k];
            foreach (var c in st.Columns)
            {
                if (tt.Column(c.Name) is not { } d)
                {
                    errors.Add($"column {k}.{c.Name} exists in {sn} but not in {tn}");
                    continue;
                }
                void Check<T>(string property, T a, T b)
                {
                    if (!EqualityComparer<T>.Default.Equals(a, b))
                        errors.Add($"column {k}.{c.Name}: {property} is '{a}' in {sn} but '{b}' in {tn}");
                }
                Check("Type", c.Type, d.Type);
                Check("MaxLength", c.MaxLength, d.MaxLength);
                Check("Precision", c.Precision, d.Precision);
                Check("Scale", c.Scale, d.Scale);
                Check("Nullable", c.Nullable, d.Nullable);
                Check("Identity", c.Identity, d.Identity);
                Check("Computed", c.Computed, d.Computed);
            }
            foreach (var c in tt.Columns)
                if (st.Column(c.Name) is null) errors.Add($"column {k}.{c.Name} exists in {tn} but not in {sn}");

            if (st.TemporalType != tt.TemporalType && tt.TemporalType != 0)
                errors.Add($"table {k}: temporal_type is {st.TemporalType} in {sn} but {tt.TemporalType} in {tn}");
        }

        return errors.Count == 0 ? null : new SchemaMismatch(sn, tn, errors);
    }
}
