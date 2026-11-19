using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core;

/// <summary>One recorded job run.</summary>
public sealed record JobRunRecord(
    long Id,
    Guid RunKey,
    string JobName,
    string Host,
    int? Pid,
    bool Exclusive,
    DateTimeOffset QueuedAt,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode,
    string Outcome)
{
    /// <summary>How long it waited for the overnight lock.</summary>
    public TimeSpan Wait => StartedAt - QueuedAt;

    /// <summary>How long the command ran (not counting the wait), once it has ended.</summary>
    public TimeSpan? Duration => EndedAt is { } e ? e - StartedAt : null;
}

/// <summary>A job's last run and usual duration.</summary>
public sealed record JobSummary(string JobName, JobRunRecord? Last, TimeSpan? UsualDuration, int MeasuredRuns);

/// <summary>
/// Records job runs (M.SCHD-T42). Every write goes to the database when it can be reached and to
/// <c>spool.jsonl</c> when it can't, so logging never breaks a job; the next run that can connect
/// flushes the spool first. Once a run has gone to the spool, the rest of its rows follow it there
/// so they reach the database in order.
/// </summary>
public sealed class RunLog
{
    /// <summary>A row counts as "measured" toward the usual duration among the last this many successful runs.</summary>
    public const int MeasuredWindow = 10;

    private const string SpoolMutexName = @"Local\MindAttic.Scheduler.Spool";

    private static readonly JsonSerializerOptions SpoolJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SchedulerDb? db;
    private readonly string spoolPath;
    private readonly HashSet<Guid> spooled = [];

    /// <summary>Uses <paramref name="db"/> (null: spool only) and <paramref name="spoolPath"/> (default: <see cref="SchedulerPaths.Spool"/>).</summary>
    public RunLog(SchedulerDb? db, string? spoolPath = null)
    {
        this.db = db;
        this.spoolPath = spoolPath ?? SchedulerPaths.Spool;
    }

    /// <summary>The last database error, when a write fell back to the spool.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>This machine's name, as recorded in <see cref="JobRunRecord.Host"/>.</summary>
    public static string HostName => Environment.MachineName;

    /// <summary>Records a run as queued and running; returns its key.</summary>
    public Guid Start(string job, bool exclusive, DateTimeOffset queuedAt, int? pid = null)
    {
        var key = Guid.NewGuid();
        pid ??= Environment.ProcessId;
        var op = new SpoolOp("start", key, queuedAt) { Job = job, Host = HostName, Pid = pid, Exclusive = exclusive };
        Write(op, c =>
        {
            TryFlush(c);
            Apply(c, null, op);
        });
        return key;
    }

    /// <summary>Records when the command actually started (after any lock wait).</summary>
    public void MarkStarted(Guid key, DateTimeOffset startedAt) =>
        Write(new SpoolOp("started", key, startedAt), null);

    /// <summary>Records the end of a run: succeeded for exit code 0, failed otherwise.</summary>
    public void End(Guid key, DateTimeOffset endedAt, int exitCode) =>
        Write(new SpoolOp("end", key, endedAt) { ExitCode = exitCode }, null);

    private void Write(SpoolOp op, Action<SqlConnection>? viaDb)
    {
        if (db is not null && !spooled.Contains(op.Key))
        {
            try
            {
                using var c = db.Open();
                if (viaDb is not null) viaDb(c);
                else Apply(c, null, op);
                return;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or TimeoutException or Azure.Identity.AuthenticationFailedException)
            {
                LastError = ex;
            }
        }
        spooled.Add(op.Key);
        AppendSpool(op);
    }

    /// <summary>Sends every spooled row to the database. Returns how many rows were sent; 0 when there were none or it can't connect.</summary>
    public int FlushSpool()
    {
        if (db is null) return 0;
        try
        {
            using var c = db.Open();
            return TryFlush(c);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException)
        {
            LastError = ex;
            return 0;
        }
    }

    private int TryFlush(SqlConnection c)
    {
        if (!File.Exists(spoolPath)) return 0;
        return WithSpoolLock(() =>
        {
            if (!File.Exists(spoolPath)) return 0;
            var ops = File.ReadAllLines(spoolPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => { try { return JsonSerializer.Deserialize<SpoolOp>(l, SpoolJson); } catch (JsonException) { return null; } })
                .OfType<SpoolOp>()
                .ToList();
            using (var tx = c.BeginTransaction())
            {
                foreach (var op in ops) Apply(c, tx, op);
                tx.Commit();
            }
            File.Delete(spoolPath);
            return ops.Count;
        });
    }

    private static void Apply(SqlConnection c, SqlTransaction? tx, SpoolOp op)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("@k", op.Key);
        cmd.Parameters.AddWithValue("@at", op.At);
        switch (op.Op)
        {
            case "start":
                cmd.CommandText =
                    """
                    IF NOT EXISTS (SELECT 1 FROM dbo.JobRun WHERE RunKey = @k)
                    INSERT dbo.JobRun (RunKey, JobName, Host, Pid, Exclusive, QueuedAt, StartedAt, Outcome)
                    VALUES (@k, @job, @host, @pid, @ex, @at, @at, 'running')
                    """;
                cmd.Parameters.AddWithValue("@job", op.Job ?? "");
                cmd.Parameters.AddWithValue("@host", op.Host ?? HostName);
                cmd.Parameters.AddWithValue("@pid", (object?)op.Pid ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ex", op.Exclusive ?? false);
                break;
            case "started":
                cmd.CommandText = "UPDATE dbo.JobRun SET StartedAt = @at WHERE RunKey = @k";
                break;
            case "end":
                cmd.CommandText =
                    "UPDATE dbo.JobRun SET EndedAt = @at, ExitCode = @code, Outcome = CASE WHEN @code = 0 THEN 'succeeded' ELSE 'failed' END WHERE RunKey = @k";
                cmd.Parameters.AddWithValue("@code", op.ExitCode ?? -1);
                break;
            default:
                return;
        }
        cmd.ExecuteNonQuery();
    }

    private void AppendSpool(SpoolOp op)
    {
        try
        {
            WithSpoolLock(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(spoolPath)!);
                File.AppendAllText(spoolPath, JsonSerializer.Serialize(op, SpoolJson) + Environment.NewLine);
                return 0;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging never breaks a job: a run that can reach neither the database nor the disk goes unrecorded.
            LastError = ex;
        }
    }

    private static T WithSpoolLock<T>(Func<T> body)
    {
        using var m = new Mutex(false, SpoolMutexName);
        var held = false;
        try
        {
            try { held = m.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { held = true; }
            return body();
        }
        finally { if (held) m.ReleaseMutex(); }
    }

    /// <summary>
    /// Marks this machine's <c>running</c> rows whose process is gone as <c>abandoned</c> (the task
    /// was killed, or the machine went down mid-run). Returns how many it marked.
    /// </summary>
    public int SweepAbandoned()
    {
        if (db is null) return 0;
        using var c = db.Open();
        var dead = new List<long>();
        using (var q = new SqlCommand("SELECT Id, Pid, QueuedAt FROM dbo.JobRun WHERE Outcome = 'running' AND Host = @h", c))
        {
            q.Parameters.AddWithValue("@h", HostName);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                int? pid = r.IsDBNull(1) ? null : r.GetInt32(1);
                if (!IsAlive(pid, r.GetDateTimeOffset(2))) dead.Add(r.GetInt64(0));
            }
        }
        foreach (var id in dead)
        {
            using var u = new SqlCommand("UPDATE dbo.JobRun SET Outcome = 'abandoned' WHERE Id = @id AND Outcome = 'running'", c);
            u.Parameters.AddWithValue("@id", id);
            u.ExecuteNonQuery();
        }
        return dead.Count;
    }

    /// <summary>True when <paramref name="pid"/> is still the process that queued the run (a reused pid started later doesn't count).</summary>
    internal static bool IsAlive(int? pid, DateTimeOffset queuedAt)
    {
        if (pid is not { } p) return false;
        try
        {
            using var proc = Process.GetProcessById(p);
            return proc.StartTime.ToUniversalTime() <= queuedAt.UtcDateTime.AddMinutes(1);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Runs, newest first.</summary>
    public IReadOnlyList<JobRunRecord> Runs(string? job = null, DateTimeOffset? since = null, int limit = 200)
    {
        if (db is null) return [];
        using var c = db.Open();
        using var cmd = new SqlCommand(
            $"""
            SELECT TOP (@n) {Columns} FROM dbo.JobRun
            WHERE (@job IS NULL OR JobName = @job) AND (@since IS NULL OR StartedAt >= @since)
            ORDER BY StartedAt DESC, Id DESC
            """, c);
        cmd.Parameters.AddWithValue("@n", Math.Max(1, limit));
        cmd.Parameters.Add(new SqlParameter("@job", System.Data.SqlDbType.NVarChar, 200) { Value = (object?)job ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@since", System.Data.SqlDbType.DateTimeOffset) { Value = (object?)since ?? DBNull.Value });
        return Read(cmd);
    }

    /// <summary>
    /// Every job's last run and usual duration: the average of its last <see cref="MeasuredWindow"/>
    /// successful runs (lock wait not counted).
    /// </summary>
    public IReadOnlyDictionary<string, JobSummary> Summaries()
    {
        var result = new Dictionary<string, JobSummary>(StringComparer.Ordinal);
        if (db is null) return result;
        using var c = db.Open();
        var last = new Dictionary<string, JobRunRecord>(StringComparer.Ordinal);
        using (var cmd = new SqlCommand(
            $"""
            WITH l AS (SELECT {Columns}, ROW_NUMBER() OVER (PARTITION BY JobName ORDER BY StartedAt DESC, Id DESC) AS rn FROM dbo.JobRun)
            SELECT {Columns} FROM l WHERE rn = 1
            """, c))
            foreach (var r in Read(cmd)) last[r.JobName] = r;

        var usual = new Dictionary<string, (double Secs, int Count)>(StringComparer.Ordinal);
        using (var cmd = new SqlCommand(
            $"""
            WITH s AS (
                SELECT JobName, CAST(DATEDIFF_BIG(MILLISECOND, StartedAt, EndedAt) AS float) / 1000.0 AS Secs,
                       ROW_NUMBER() OVER (PARTITION BY JobName ORDER BY StartedAt DESC, Id DESC) AS rn
                FROM dbo.JobRun WHERE Outcome = 'succeeded' AND EndedAt IS NOT NULL)
            SELECT JobName, AVG(Secs), COUNT(*) FROM s WHERE rn <= {MeasuredWindow} GROUP BY JobName
            """, c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) usual[r.GetString(0)] = (r.GetDouble(1), r.GetInt32(2));

        foreach (var name in last.Keys.Union(usual.Keys))
        {
            last.TryGetValue(name, out var l);
            TimeSpan? u = usual.TryGetValue(name, out var x) ? TimeSpan.FromSeconds(x.Secs) : null;
            result[name] = new JobSummary(name, l, u, usual.TryGetValue(name, out var y) ? y.Count : 0);
        }
        return result;
    }

    private const string Columns = "Id, RunKey, JobName, Host, Pid, Exclusive, QueuedAt, StartedAt, EndedAt, ExitCode, Outcome";

    private static List<JobRunRecord> Read(SqlCommand cmd)
    {
        var list = new List<JobRunRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new JobRunRecord(
                r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt32(4), r.GetBoolean(5),
                r.GetDateTimeOffset(6), r.GetDateTimeOffset(7),
                r.IsDBNull(8) ? null : r.GetDateTimeOffset(8),
                r.IsDBNull(9) ? null : r.GetInt32(9),
                r.GetString(10)));
        return list;
    }

    /// <summary>One spooled write.</summary>
    internal sealed record SpoolOp(string Op, Guid Key, DateTimeOffset At)
    {
        public string? Job { get; init; }
        public string? Host { get; init; }
        public int? Pid { get; init; }
        public bool? Exclusive { get; init; }
        public int? ExitCode { get; init; }
    }
}
