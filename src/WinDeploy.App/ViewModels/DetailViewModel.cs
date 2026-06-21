using System.Diagnostics;
using System.Windows.Media;
using WinDeploy.App.Services;

namespace WinDeploy.App.ViewModels;

/// <summary>Software detail page. Detail fields come from <see cref="DetailService"/> (cached;
/// prefetched during lazy-load), so opening a card is instant and never re-fetches.</summary>
public sealed class DetailViewModel : ObservableObject
{
    public AppItemViewModel Item { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand OpenHomepageCommand { get; }

    public DetailViewModel(AppItemViewModel item, Action back)
    {
        Item = item;
        BackCommand = new RelayCommand(_ => back());
        OpenHomepageCommand = new RelayCommand(_ => OpenHomepage());

        var ins = item.Model.Install;
        Source = ins.Method switch
        {
            "winget" => "winget",
            "winget-bundle" => "winget（合集）",
            "portable" => "便携包（zip）",
            "git" => "Git 仓库",
            "conda" => "conda 环境",
            "vscode-ext" => "VS Code 扩展",
            "script" => "脚本",
            _ => ins.Method,
        };
        PackageId = ins.Id ?? (ins.Ids is { Count: > 0 } ? string.Join(", ", ins.Ids) : "—");

        var cached = DetailService.GetCached(item.Model.Id);
        if (cached != null) Apply(cached);
        _ = LoadAsync();
    }

    public ImageSource? IconImage => Item.IconImage;
    public bool HasIcon => Item.HasIcon;
    public bool ShowLetter => Item.ShowLetter;
    public string Badge => Item.Badge;
    public Brush ChipBackground => Item.ChipBackground;
    public Brush ChipForeground => Item.ChipForeground;
    public string Name => Item.Name;
    public string Summary => Item.Summary;
    public bool IsInstalled => Item.IsInstalled;
    public string StatusText => Item.IsInstalled ? "已安装" : "未安装";

    public string Source { get; }
    public string PackageId { get; }

    private string _version = "—";
    public string Version { get => _version; private set => Set(ref _version, value); }

    private string _size = "—";
    public string Size { get => _size; private set => Set(ref _size, value); }

    private string _installDate = "—";
    public string InstallDate { get => _installDate; private set => Set(ref _installDate, value); }

    private string _publisher = "—";
    public string Publisher { get => _publisher; private set => Set(ref _publisher, value); }

    private string _homepage = "—";
    public string Homepage
    {
        get => _homepage;
        private set { if (Set(ref _homepage, value)) OnPropertyChanged(nameof(HasHomepage)); }
    }
    public bool HasHomepage => _homepage.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private async Task LoadAsync() => Apply(await DetailService.FetchAsync(Item.Model));

    private void Apply(DetailInfo i)
    {
        Version = i.Version;
        Size = i.Size;
        InstallDate = i.InstallDate;
        Publisher = i.Publisher;
        Homepage = i.Homepage;
    }

    private void OpenHomepage()
    {
        if (!HasHomepage) return;
        try { Process.Start(new ProcessStartInfo(_homepage) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
