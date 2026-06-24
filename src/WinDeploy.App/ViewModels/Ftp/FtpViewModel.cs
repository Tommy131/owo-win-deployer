using WinDeploy.App.Services.Ftp;

namespace WinDeploy.App.ViewModels.Ftp;

/// <summary>Host for the 「FTP 传输」 page. A single <see cref="FtpServer"/> instance is shared by the
/// 服务端 (run/monitor) and 服务端配置 (users/groups/permissions/ports/SSL) tabs; a third 客户端 tab connects
/// out to a remote server. The active tab's VM is exposed via <see cref="Current"/>.</summary>
public sealed class FtpViewModel : ObservableObject
{
    private readonly FtpServer _server = new();

    public FtpServerViewModel Server { get; }
    public FtpConfigViewModel Config { get; }
    public FtpClientViewModel Client { get; }

    public FtpViewModel()
    {
        Config = new FtpConfigViewModel();
        Server = new FtpServerViewModel(_server, () => Config.Snapshot());
        Client = new FtpClientViewModel();
        _current = Server;

        ShowServerCommand = new RelayCommand(_ => Select(0));
        ShowConfigCommand = new RelayCommand(_ => Select(1));
        ShowClientCommand = new RelayCommand(_ => Select(2));
    }

    private int _tab;
    private object _current;
    public object Current { get => _current; private set => Set(ref _current, value); }

    public bool IsServerTab => _tab == 0;
    public bool IsConfigTab => _tab == 1;
    public bool IsClientTab => _tab == 2;

    public RelayCommand ShowServerCommand { get; }
    public RelayCommand ShowConfigCommand { get; }
    public RelayCommand ShowClientCommand { get; }

    private void Select(int t)
    {
        _tab = t;
        Current = t switch { 1 => Config, 2 => Client, _ => (object)Server };
        OnPropertyChanged(nameof(IsServerTab));
        OnPropertyChanged(nameof(IsConfigTab));
        OnPropertyChanged(nameof(IsClientTab));
    }

    /// <summary>Page shown — keep the server status/connection table refreshing live.</summary>
    public void Activate() => Server.StartLive();

    /// <summary>Page hidden — stop the refresh timer (the server itself keeps running).</summary>
    public void Deactivate() => Server.StopLive();

    /// <summary>App is closing — release the listening ports.</summary>
    public void Shutdown() => _server.Stop();
}
