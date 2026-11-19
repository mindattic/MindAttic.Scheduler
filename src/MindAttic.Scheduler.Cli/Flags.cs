using System.Text.Encodings.Web;
using System.Text.Json;

namespace MindAttic.Scheduler.Cli;

/// <summary>A bad command line: the message is printed, then the usage.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>"--name value" and bare "--switch" flags.</summary>
internal sealed class Flags
{
    private readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

    public Flags(IEnumerable<string> args, params string[] valued)
    {
        var list = args.ToList();
        var takesValue = new HashSet<string>(valued.Length > 0 ? valued : ["job", "since", "limit", "night"], StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"Unexpected argument '{a}'.");
            var name = a[2..];
            if (takesValue.Contains(name))
            {
                if (i + 1 >= list.Count) throw new UsageException($"--{name} needs a value.");
                values[name] = list[++i];
            }
            else values[name] = null;
        }
    }

    public bool Has(string name) => values.ContainsKey(name);

    public string? Value(string name) => values.TryGetValue(name, out var v) ? v : null;

    public void AllowOnly(params string[] names)
    {
        var known = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var k in values.Keys)
            if (!known.Contains(k)) throw new UsageException($"Unknown flag --{k}.");
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(object value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, Options));
}
