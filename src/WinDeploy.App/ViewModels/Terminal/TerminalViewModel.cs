using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App.Services;

namespace WinDeploy.App.ViewModels.Terminal;

/// <summary>The terminal page host: holds any number of live <see cref="TerminalSessionViewModel"/> tabs at
/// once, each backed by its own ConPTY of a user-chosen shell. Sessions persist in the background — switching
/// the active tab only re-binds the single on-screen surface, it never disconnects a session. Closing a
/// session is gated behind a themed confirmation (the PTY's state is lost on disconnect).</summary>
public sealed class TerminalViewModel : ObservableObject, IDisposable
{
    private bool _initialized;

    public ObservableCollection<TerminalSessionViewModel> Sessions { get; } = new();

    /// <summary>The shells installed on this machine, offered when creating a new session.</summary>
    public IReadOnlyList<ShellInfo> AvailableShells { get; }

    /// <summary>Create a session for the shell passed as the command parameter.</summary>
    public RelayCommand NewSessionCommand { get; }
    /// <summary>Make the session passed as the command parameter the active (visible) one.</summary>
    public RelayCommand ActivateCommand { get; }
    /// <summary>Confirm, then close the session passed as the command parameter.</summary>
    public RelayCommand CloseSessionCommand { get; }
    /// <summary>Rename / recolor the session passed as the command parameter.</summary>
    public RelayCommand EditSessionCommand { get; }

    /// <summary>Default accent colors handed out (cycling) to new sessions, so tabs start visually distinct.</summary>
    public static readonly string[] Palette =
    {
        "#4D9DFF", "#36C5A6", "#5BCC5B", "#E0B23C", "#E8754C",
        "#E05D6F", "#C063D9", "#7C8AA5", "#3FB6E0", "#B5894A",
    };

    /// <summary>Raised when the active session changes (or its PTY restarts) so the view re-binds the surface.</summary>
    public event Action? ActiveChanged;

    public bool Supported => PtySession.IsSupported;

    public string WorkingDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public TerminalViewModel()
    {
        AvailableShells = ShellCatalog.Detect();
        NewSessionCommand = new RelayCommand(p => { if (p is ShellInfo s) NewSession(s); });
        ActivateCommand = new RelayCommand(p => { if (p is TerminalSessionViewModel s) ActiveSession = s; });
        CloseSessionCommand = new RelayCommand(p => { if (p is TerminalSessionViewModel s) RequestClose(s); });
        EditSessionCommand = new RelayCommand(p => { if (p is TerminalSessionViewModel s) RequestEdit(s); });
    }

    private TerminalSessionViewModel? _activeSession;
    public TerminalSessionViewModel? ActiveSession
    {
        get => _activeSession;
        set
        {
            if (ReferenceEquals(_activeSession, value)) return;
            _activeSession = value;
            foreach (var s in Sessions) s.IsActive = ReferenceEquals(s, value);
            OnPropertyChanged();
            ActiveChanged?.Invoke();
        }
    }

    /// <summary>Open the default session the first time the page is shown (after <see cref="WorkingDir"/> is
    /// set). Idempotent: navigating away and back doesn't spawn extras, nor re-create one the user closed.</summary>
    public void EnsureInitialSession()
    {
        if (_initialized) return;
        _initialized = true;
        if (Sessions.Count > 0) return;   // a session already exists (e.g. opened from the tray) — don't add one
        var def = AvailableShells.FirstOrDefault(s => s.Id == "powershell") ?? AvailableShells.FirstOrDefault();
        if (def != null) NewSession(def);
    }

    private int _created;   // total ever created — drives the cycling default color (stable across closes)

    private void NewSession(ShellInfo shell)
    {
        var color = Palette[_created++ % Palette.Length];
        var session = new TerminalSessionViewModel(shell, WorkingDir, MakeTitle(shell), color);
        session.Reset += () => { if (ReferenceEquals(session, ActiveSession)) ActiveChanged?.Invoke(); };
        Sessions.Add(session);
        ActiveSession = session;
    }

    /// <summary>"Git Bash", then "Git Bash 2", "Git Bash 3" … for repeats of the same shell.</summary>
    private string MakeTitle(ShellInfo shell)
    {
        var n = Sessions.Count(s => s.Shell.Id == shell.Id) + 1;
        return n == 1 ? shell.Name : $"{shell.Name} {n}";
    }

    private void RequestClose(TerminalSessionViewModel session)
    {
        var dlg = new TerminalCloseConfirmDialog(session.Title) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        CloseSession(session);
    }

    private void RequestEdit(TerminalSessionViewModel session)
    {
        var dlg = new TerminalSessionEditDialog(session.Title, session.ColorHex) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        session.Title = dlg.SessionName;       // setter ignores blank, keeping the old name
        session.ColorHex = dlg.ColorHex;
    }

    private void CloseSession(TerminalSessionViewModel session)
    {
        var idx = Sessions.IndexOf(session);
        // Re-point the active session to a neighbour BEFORE removal, so the bound picker never transits null.
        if (ReferenceEquals(ActiveSession, session))
            ActiveSession = Sessions.Count <= 1 ? null
                : Sessions[idx == Sessions.Count - 1 ? idx - 1 : idx + 1];
        Sessions.Remove(session);
        session.Dispose();
    }

    public void Dispose()
    {
        foreach (var s in Sessions) s.Dispose();
        Sessions.Clear();
    }
}
