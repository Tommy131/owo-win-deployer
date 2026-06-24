using System.IO;
using System.Windows;
using System.Windows.Media;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Terminal;

/// <summary>One embedded, fully interactive terminal session backed by a real Windows pseudo-console (ConPTY)
/// running a chosen shell (<see cref="ShellInfo"/>) — so ssh, password prompts and full-screen TUIs work, not
/// just line commands. Each session owns its own PTY and <see cref="VtScreen"/>, so it keeps running in the
/// background while another tab is shown; switching tabs never disconnects it. The host
/// (<see cref="TerminalViewModel"/>) binds the single on-screen surface to whichever session is active.</summary>
public sealed class TerminalSessionViewModel : ObservableObject, IDisposable
{
    private PtySession? _pty;
    private short _cols = 80, _rows = 25;
    private bool _started;

    public ShellInfo Shell { get; }
    public string WorkingDir { get; }

    /// <summary>The shell's real exe icon, shown before the name so the terminal type is recognizable at a glance.</summary>
    public ImageSource? ShellIcon => ShellCatalog.IconFor(Shell);
    public VtScreen Screen { get; } = new(80, 25);
    public RelayCommand ClearCommand { get; }
    public RelayCommand RestartCommand { get; }

    /// <summary>Raised when a fresh PTY/screen is (re)started, so the host re-binds its surface if active.</summary>
    public event Action? Reset;

    public TerminalSessionViewModel(ShellInfo shell, string workingDir, string title, string colorHex)
    {
        Shell = shell;
        WorkingDir = workingDir;
        _title = title;
        _colorHex = colorHex;
        ClearCommand = new RelayCommand(_ => Screen.Clear());
        RestartCommand = new RelayCommand(_ => Restart());
    }

    private string _title;
    /// <summary>The user-facing name / remark for this session (editable), shown in the picker.</summary>
    public string Title { get => _title; set => Set(ref _title, string.IsNullOrWhiteSpace(value) ? _title : value); }

    private string _colorHex;
    /// <summary>The session's accent color (hex) — a quick visual locator in the picker dropdown.</summary>
    public string ColorHex
    {
        get => _colorHex;
        set { if (Set(ref _colorHex, value)) OnPropertyChanged(nameof(ColorBrush)); }
    }

    /// <summary>The color as a brush for the picker's swatch dot.</summary>
    public Brush ColorBrush
    {
        get
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(_colorHex)); }
            catch { return Brushes.Gray; }
        }
    }

    private bool _isActive;
    /// <summary>True for the single session whose screen the surface is currently bound to.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    public bool Supported => PtySession.IsSupported;

    private string _status = Localizer.T("term.status.notStarted");
    public string Status { get => _status; private set => Set(ref _status, value); }

    // ── lifecycle (driven by the surface's measured size while this session is active) ──────────
    public void Send(string s) => _pty?.Write(s);

    /// <summary>Called by the host when the surface's pixel size maps to a (cols, rows) grid: resize the live
    /// PTY, or start it on first sizing.</summary>
    public void SetViewport(int cols, int rows)
    {
        _cols = (short)Math.Clamp(cols, 2, 1000);
        _rows = (short)Math.Clamp(rows, 1, 1000);
        Screen.Resize(_cols, _rows);
        if (_started && _pty is { HasExited: false }) _pty.Resize(_cols, _rows);
        else EnsureStarted();
    }

    public void EnsureStarted()
    {
        if (_started && _pty is { HasExited: false }) return;
        Start();
    }

    private void Start()
    {
        if (!PtySession.IsSupported)
        {
            Screen.Clear();
            Screen.FeedSystem(Localizer.T("term.surface.notSupported"));
            Status = Localizer.T("term.status.notSupported");
            return;
        }
        KillPty();
        Screen.Clear();
        try
        {
            var dir = Directory.Exists(WorkingDir) ? WorkingDir : Environment.CurrentDirectory;
            _pty = new PtySession();
            _pty.Output += OnOutput;
            _pty.Exited += OnExited;
            Screen.Respond = s => _pty?.Write(s);   // cursor-position reports etc. go back to the shell
            _pty.Start(Shell.CommandLine, dir, _cols, _rows);
            _started = true;
            Status = Shell.Name + " · " + dir;
            Reset?.Invoke();
        }
        catch (Exception ex) { Screen.FeedSystem(Localizer.Format("term.surface.startFailed", ex.Message)); Status = Localizer.T("term.status.startFailed"); }
    }

    private void OnOutput(string s) => Screen.Feed(s);   // VtScreen.Feed is lock-guarded (reader thread)

    private void OnExited()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) { disp.BeginInvoke(new Action(OnExited)); return; }
        Screen.FeedSystem(Localizer.T("term.surface.exited"));
        Status = Localizer.T("term.status.exited");
        _started = false;
    }

    private void Restart() => Start();

    private void KillPty()
    {
        try
        {
            if (_pty != null) { _pty.Output -= OnOutput; _pty.Exited -= OnExited; _pty.Dispose(); }
        }
        catch { /* best effort */ }
        _pty = null;
    }

    public void Dispose() => KillPty();
}
