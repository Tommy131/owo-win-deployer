using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.Core.Engine;

namespace WinDeploy.App.ViewModels;

/// <summary>The "运行进度" page: overall bar + ETA, current task, a running HISTORY of per-item rows
/// across operations, and a live log. History is preserved between operations; the user can clear it
/// manually (with confirmation). The four count tiles reflect the CURRENT operation only.</summary>
public sealed class ProgressViewModel : ObservableObject
{
    private Dictionary<string, ProgressItemViewModel> _current = new();
    private readonly Stopwatch _sw = new();
    private int _total;
    private int _completed;

    public ObservableCollection<ProgressItemViewModel> Items { get; } = new();

    private double _percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }

    private string _overall = "准备中";
    public string Overall { get => _overall; set => Set(ref _overall, value); }

    private string _current_ = "";
    public string Current { get => _current_; set => Set(ref _current_, value); }

    public string CurrentLabel => $"正在{_verb}";

    private string _eta = "";
    public string Eta { get => _eta; set => Set(ref _eta, value); }

    private string _log = "";
    public string Log { get => _log; set => Set(ref _log, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }

    /// <summary>Raised when the user clicks 取消 while an operation is running.</summary>
    public event Action? CancelRequested;
    public RelayCommand CancelCommand => _cancelCommand ??= new RelayCommand(_ => CancelRequested?.Invoke(), _ => IsRunning);
    private RelayCommand? _cancelCommand;

    public RelayCommand ClearHistoryCommand => _clearHistory ??= new RelayCommand(_ => ClearHistory(), _ => Items.Count > 0 && !IsRunning);
    private RelayCommand? _clearHistory;

    public RelayCommand OpenTotalLogCommand => _openTotal ??= new RelayCommand(_ => RunHistory.Open());
    private RelayCommand? _openTotal;
    public RelayCommand ClearTotalLogCommand => _clearTotal ??= new RelayCommand(_ => ClearTotalLog());
    private RelayCommand? _clearTotal;

    private string? _currentId;

    // Per-current-operation tiles.
    public int OkCount => _current.Values.Count(i => i.Kind == "ok");
    public int FailedCount => _current.Values.Count(i => i.Kind == "failed");
    public int RunningCount => _current.Values.Count(i => i.Kind == "running");
    public int QueuedCount => _current.Values.Count(i => i.Kind == "queued");

    private string _verb = "安装";

    public void Begin(IReadOnlyList<PlanItem> plan, string verb = "安装")
    {
        _verb = verb;
        _current = new Dictionary<string, ProgressItemViewModel>();
        if (Items.Count > 0) Append("");
        Append($"── {DateTime.Now:HH:mm:ss} {verb} ──");

        foreach (var pi in plan)
        {
            var installed = pi.Status == PlanStatus.Installed;
            var vm = new ProgressItemViewModel(pi.Item.Name, pi.Item.Install.Method)
            {
                Status = installed ? "已装（跳过）" : "排队",
                Kind = installed ? "skip" : "queued",
            };
            Items.Add(vm);
            _current[pi.Item.Id] = vm;
        }
        _total = plan.Count(p => p.Status == PlanStatus.ToInstall);
        _completed = 0;
        IsRunning = true;
        Percent = 0;
        Current = "";
        Overall = $"待{_verb} {_total} 项";
        _sw.Restart();
        OnPropertyChanged(nameof(CurrentLabel));
        RaiseCounts();
    }

    public void OnStart(PlanItem pi)
    {
        Current = pi.Item.Name;
        _currentId = pi.Item.Id;
        if (_current.TryGetValue(pi.Item.Id, out var vm)) { vm.Status = $"{_verb}中"; vm.Kind = "running"; vm.MarkStarted(); }
        Append($"→ {_verb} {pi.Item.Name} …");
        RaiseCounts();
    }

    /// <summary>Append a granular step (e.g. "开始下载 …") to the current task + the log.</summary>
    public void OnStep(string msg)
    {
        if (_currentId != null && _current.TryGetValue(_currentId, out var vm))
            vm.AddDetail($"{DateTime.Now:HH:mm:ss}  {msg}");
        Append($"   · {msg}");
    }

    public void OnDone(RunResult r)
    {
        if (_current.TryGetValue(r.Item.Id, out var vm))
        {
            (vm.Status, vm.Kind) = r.Status switch
            {
                StepStatus.Ok => ("成功", "ok"),
                StepStatus.Failed => ("失败", "failed"),
                _ => (vm.Status, vm.Kind),
            };
            vm.MarkEnded();
            PersistRecord(r, vm);
        }

        if (r.Message != "already installed")
        {
            _completed++;
            Percent = _total == 0 ? 100 : Math.Round(_completed * 100.0 / _total);
            Eta = EstimateEta();
            if (r.Status == StepStatus.Failed) Append($"✗ {r.Item.Name}: {r.Message}");
            else Append(string.IsNullOrEmpty(r.Message) ? $"✓ {r.Item.Name}" : $"✓ {r.Item.Name} — {r.Message}");
        }
        Overall = $"进度 {_completed} / {_total}";
        RaiseCounts();
    }

    public void Complete()
    {
        IsRunning = false;
        Current = "";
        Eta = "";
        Percent = 100;
        var ok = _current.Values.Count(v => v.Kind == "ok");
        var failed = _current.Values.Count(v => v.Kind == "failed");
        Overall = $"完成 · 成功 {ok} · 失败 {failed}";
        Append("— 结束 —");
        RaiseCounts();
    }

    private void ClearHistory()
    {
        if (MessageBox.Show("确定清空运行进度历史记录？（屏幕显示，不影响独立总日志文件）", "清空历史",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Items.Clear();
        _current = new Dictionary<string, ProgressItemViewModel>();
        Log = "";
        Percent = 0;
        Overall = "准备中";
        Current = "";
        Eta = "";
        RaiseCounts();
    }

    private void ClearTotalLog()
    {
        if (MessageBox.Show("确定清空运行进度独立总日志（progress.jsonl）？此操作不可恢复。", "清空总日志",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RunHistory.Clear();
    }

    private void PersistRecord(RunResult r, ProgressItemViewModel vm)
    {
        try
        {
            RunHistory.Append(new RunRecord
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Op = _verb,
                Id = r.Item.Id,
                Name = r.Item.Name,
                Status = r.Status switch { StepStatus.Ok => "成功", StepStatus.Failed => "失败", _ => "跳过" },
                Message = r.Message,
                StartedAt = vm.StartTime?.ToString("HH:mm:ss") ?? "",
                EndedAt = vm.EndTime?.ToString("HH:mm:ss") ?? "",
                DurationMs = vm.StartTime != null && vm.EndTime != null ? (long)(vm.EndTime.Value - vm.StartTime.Value).TotalMilliseconds : 0,
                Steps = vm.Details.ToList(),
            });
        }
        catch { /* logging must not break the run */ }
    }

    private string EstimateEta()
    {
        if (_completed == 0 || _completed >= _total) return "";
        var per = _sw.Elapsed.TotalSeconds / _completed;
        var remain = TimeSpan.FromSeconds(per * (_total - _completed));
        return remain.TotalMinutes >= 1
            ? $"约剩 {(int)remain.TotalMinutes} 分 {remain.Seconds} 秒"
            : $"约剩 {remain.Seconds} 秒";
    }

    private void Append(string line) => Log += line + Environment.NewLine;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(OkCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(QueuedCount));
    }
}
