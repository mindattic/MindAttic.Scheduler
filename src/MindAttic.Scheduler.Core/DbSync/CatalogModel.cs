namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>One column of a user table, as sys.columns describes it.</summary>
public sealed record CatalogColumn(
    int Id,
    string Name,
    string Type,
    int MaxLength,
    int Precision,
    int Scale,
    bool Nullable,
    bool Identity,
    bool Computed,
    int Generated,
    bool Hidden,
    string? Collation,
    bool Encrypted)
{
    /// <summary>False for computed and rowversion columns, which are never written.</summary>
    public bool Copyable => !Computed && Type != "timestamp";

    /// <summary>True for a VECTOR column.</summary>
    public bool IsVector => Type == "vector";

    /// <summary>A VECTOR's dimension count (float32 elements after an 8-byte header); 0 for other types.</summary>
    public int VectorDimensions => IsVector ? (MaxLength - 8) / 4 : 0;

    /// <summary>The column's type as it is declared: <c>nvarchar(50)</c>, <c>decimal(18,2)</c>, <c>vector(3)</c>.</summary>
    public string TypeDecl => Type switch
    {
        "char" or "varchar" or "binary" or "varbinary" => $"{Type}({(MaxLength == -1 ? "MAX" : MaxLength.ToString())})",
        "nchar" or "nvarchar" => $"{Type}({(MaxLength == -1 ? "MAX" : (MaxLength / 2).ToString())})",
        "decimal" or "numeric" => $"{Type}({Precision},{Scale})",
        "datetime2" or "time" or "datetimeoffset" => $"{Type}({Scale})",
        "vector" => $"vector({VectorDimensions})",
        _ => Type,
    };
}

/// <summary>A DML trigger on a table.</summary>
public sealed record CatalogTrigger(string Name, bool Disabled);

/// <summary>
/// A foreign key (<c>FK</c>) or check constraint (<c>CK</c>). <see cref="Definition"/> is the FK's
/// <c>parent&gt;referenced</c> column pairs, or the check's expression.
/// </summary>
public sealed record CatalogConstraint(
    string Kind,
    string Name,
    string TableKey,
    string? ReferencedTableKey,
    bool Disabled,
    bool NotTrusted,
    string Definition);

/// <summary>A system-versioned table and its history table, with the period columns.</summary>
public sealed record TemporalPair(string CurrentKey, string HistoryKey, string PeriodStart, string PeriodEnd);

/// <summary>One user table.</summary>
public sealed class CatalogTable
{
    public required int ObjectId { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }

    /// <summary><c>schema.name</c>, unquoted; the catalog's key.</summary>
    public string Key => $"{Schema}.{Name}";

    /// <summary><c>[schema].[name]</c>.</summary>
    public string QName => $"{TableCatalog.Quote(Schema)}.{TableCatalog.Quote(Name)}";

    /// <summary>sys.tables.temporal_type: 0 not versioned, 1 history, 2 system-versioned.</summary>
    public required int TemporalType { get; init; }

    /// <summary>The history table's key, on a system-versioned table.</summary>
    public string? HistoryKey { get; init; }

    /// <summary>Features the sync cannot handle: memory-optimized, external, graph, ledger.</summary>
    public required IReadOnlyList<string> Unsupported { get; init; }

    public int? HistoryRetentionPeriod { get; init; }
    public string HistoryRetentionUnit { get; init; } = "";
    public long Rows { get; init; }
    public long MB { get; init; }
    public bool HasClusteredColumnstore { get; init; }
    public bool ReferencedByForeignKey { get; init; }

    public List<CatalogColumn> Columns { get; } = [];
    public List<string> PrimaryKey { get; } = [];
    public List<string> ClusteredKey { get; } = [];
    public List<string> ColumnstoreOrder { get; } = [];
    public string? PeriodStart { get; internal set; }
    public string? PeriodEnd { get; internal set; }
    public List<CatalogTrigger> Triggers { get; } = [];

    /// <summary>The identity column's last value as text, when it has one.</summary>
    public string? IdentityLastValue { get; internal set; }

    /// <summary>The column by name, or null.</summary>
    public CatalogColumn? Column(string name) => Columns.FirstOrDefault(c => c.Name == name);

    public CatalogColumn? IdentityColumn => Columns.FirstOrDefault(c => c.Identity);

    /// <summary>The columns a copy reads and writes: computed and rowversion columns excluded.</summary>
    public IReadOnlyList<CatalogColumn> CopyColumns => Columns.Where(c => c.Copyable).ToList();

    public IReadOnlyList<CatalogColumn> VectorColumns => Columns.Where(c => c.Copyable && c.IsVector).ToList();

    /// <summary>Copyable columns the sync cannot carry (spatial, hierarchyid, Always Encrypted), as <c>name (type)</c>.</summary>
    public IReadOnlyList<string> CopyUnsupported => Columns
        .Where(c => c.Copyable && (c.Type is "geography" or "geometry" or "hierarchyid" || c.Encrypted))
        .Select(c => $"{c.Name} ({c.Type})").ToList();
}

/// <summary>
/// Source and target schemas differ. The sync refuses to touch such a pair: it never creates,
/// alters or drops schema, so the target has to be migrated first.
/// </summary>
public sealed record SchemaMismatch(string SourceName, string TargetName, IReadOnlyList<string> Differences)
{
    public override string ToString() =>
        $"schema differs between {SourceName} and {TargetName} ({Differences.Count} difference(s)), first: {Differences[0]}";
}
