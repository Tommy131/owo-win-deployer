using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WinDeploy.App.Services;

/// <summary>One persisted run-progress record (a single task execution with its steps + timings).</summary>
public sealed class RunRecord
{
    public string Time { get; set; } = "";
    public string Op { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Message { get; set; }
    public string StartedAt { get; set; } = "";
    public string EndedAt { get; set; } = "";
    public long DurationMs { get; set; }
    public List<string> Steps { get; set; } = new();
}

/// <summary>The independent run-progress log: one formatted JSON object per task in progress.jsonl
/// (separate from the audit app.log). Viewable and clearable.</summary>
public static class RunHistory
{
    private static readonly object Gate = new();
    private static string? _file;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep CJK readable
    };

    public static string FilePath => _file ??= Build();
    public static string Folder => Path.GetDirectoryName(FilePath)!;

    private static string Build()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeploy", "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "progress.jsonl");
    }

    public static void Append(RunRecord record)
    {
        try
        {
            var line = JsonSerializer.Serialize(record, Json);
            lock (Gate) File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { /* never break the run */ }
    }

    /// <summary>Read all records (best effort), newest last.</summary>
    public static List<RunRecord> ReadAll()
    {
        var list = new List<RunRecord>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<RunRecord>(line) is { } r) list.Add(r); } catch { /* skip bad line */ }
            }
        }
        catch { /* ignore */ }
        return list;
    }

    public static void Clear()
    {
        try { lock (Gate) File.WriteAllText(FilePath, ""); } catch { /* ignore */ }
    }

    public static void Open()
    {
        try
        {
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "");
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}
