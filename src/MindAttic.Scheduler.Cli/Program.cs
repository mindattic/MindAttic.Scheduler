using MindAttic.Scheduler.Cli;

// mindattic-scheduler (M.SCHD-T42): the run wrapper, the overnight arranger and the run log.
try
{
    return args.Length == 0 ? Usage(0) : args[0].ToLowerInvariant() switch
    {
        "run" => RunCommand.Execute(args, Environment.CommandLine),
        "arrange" => ArrangeCommand.Execute(new Flags(args.Skip(1))),
        "db" => DbCommand.Execute(args.Skip(1).ToArray()),
        "runs" => RunsCommand.Execute(new Flags(args.Skip(1))),
        "help" or "--help" or "-h" or "/?" => Usage(0),
        _ => Usage(2),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return Usage(2);
}
catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or IOException or FormatException)
{
    // The run wrapper never gets here (it records nothing rather than fail a job); this is arrange, db and runs.
    Console.Error.WriteLine($"mindattic-scheduler: {ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}");
    return 1;
}

static int Usage(int code)
{
    var w = code == 0 ? Console.Out : Console.Error;
    w.WriteLine("""
        mindattic-scheduler - MindAttic.Scheduler's run wrapper, overnight arranger and run log

          run <job> [--exclusive] -- <command line>
              Runs the command, records the run (start, end, exit code) and exits with the
              command's own exit code. --exclusive waits for the overnight lock first, so only one
              arranged job runs at a time. If the database is unreachable the rows are spooled and
              sent by the next run that can connect.

          arrange [--optimize] [--dry-run] [--json] [--no-install]
              Places the "daily": "auto" jobs: heaviest first from 00:00, staggered, around pinned
              jobs, done by 08:00. Without --optimize, today's slots are kept while they're sound.
              Writes arrangement.json and reruns install.ps1 (not with --dry-run or --no-install).

          db migrate | status | flush
              migrate: creates the schema (the only DDL). status: where it connects and the schema
              version. flush: sends spooled rows.

          runs [--job <name>] [--since <date>] [--limit N] [--summary] [--night <yyyy-MM-dd>] [--json]
              The run history, each job's last result and usual duration (--summary), or one night's
              arranged jobs against what actually ran (--night; "last" for today's).
        """);
    return code;
}
