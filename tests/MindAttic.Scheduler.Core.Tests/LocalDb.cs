namespace MindAttic.Scheduler.Core.Tests;

/// <summary>Shared test probe for LocalDB (SqlLocalDB.exe).</summary>
internal static class LocalDb
{
    // IgnoreInaccessible: a full SQL Server instance installed alongside LocalDB has folders a normal
    // user can't list (MSSQL16.MSSQLSERVER\MSSQL\Backup), and SearchOption.AllDirectories throws on them.
    public static bool Installed()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server");
        return Directory.Exists(root) &&
            Directory.EnumerateFiles(root, "SqlLocalDB.exe",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Any();
    }
}
