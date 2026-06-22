using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

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
        string line;
        try { line = JsonSerializer.Serialize(record, Json); }
        catch { return; }

        // Retry: the file may be transiently locked (an editor with the file open to inspect it,
        // antivirus, or the search indexer) — a single AppendAllText would silently lose the record,
        // leaving rows on the page that never reach disk. Back off briefly and try again.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                lock (Gate)
                    using (var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                        w.Write(line + Environment.NewLine);
                return;
            }
            catch (IOException) when (attempt < 8) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) when (attempt < 8) { Thread.Sleep(25); }
            catch (Exception ex)
            {
                // Final failure: surface it to the audit log instead of vanishing silently.
                try { AuditLog.Action($"运行记录写入失败（{Path.GetFileName(FilePath)}）：{ex.Message}"); } catch { }
                return;
            }
        }
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
        lock (Gate)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(FilePath)) File.Delete(FilePath);   // 彻底删除旧记录
                    return;
                }
                catch (IOException) when (attempt < 6) { Thread.Sleep(30); }            // 被占用，稍后重试
                catch (UnauthorizedAccessException) when (attempt < 6) { Thread.Sleep(30); }
                catch
                {
                    // 删除仍失败（被外部查看器独占等）→ 退而求其次，清空内容到 0 字节
                    try { using (new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { } } catch { }
                    return;
                }
            }
        }
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
