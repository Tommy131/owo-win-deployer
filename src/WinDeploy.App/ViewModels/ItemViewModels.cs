using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

public sealed class NavItemViewModel : LocalizedObject
{
    private readonly string _labelKey;
    public string Glyph { get; }
    /// <summary>Localized nav label (resolved live from the i18n key passed to the ctor).</summary>
    public string Label => Localizer.T(_labelKey);
    public object Page { get; }

    /// <summary>Advanced / professional page — only shown in the nav when 开发人员模式 is on.</summary>
    public bool Advanced { get; }

    /// <summary>Minimum Windows build required for this page (0 = any). Pages above the running build are
    /// hidden from the nav (e.g. WSL on pre-Windows 10).</summary>
    public int MinBuild { get; }

    public NavItemViewModel(string glyph, string labelKey, object page, bool advanced = false, int minBuild = 0)
    {
        Glyph = glyph;
        _labelKey = labelKey;
        Page = page;
        Advanced = advanced;
        MinBuild = minBuild;
    }

    protected override void OnCultureChanged() => OnPropertyChanged(nameof(Label));

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>A collapsible nav group (e.g. 部署 / 系统 / 开发) with an icon and a set of nav items.</summary>
public sealed class NavGroupViewModel : LocalizedObject
{
    private readonly string _titleKey;
    public string Glyph { get; }
    /// <summary>Localized group header (resolved live from the i18n key passed to the ctor).</summary>
    public string Title => Localizer.T(_titleKey);
    public System.Collections.ObjectModel.ObservableCollection<NavItemViewModel> Items { get; } = new();
    public RelayCommand ToggleCommand { get; }

    public NavGroupViewModel(string glyph, string titleKey)
    {
        Glyph = glyph;
        _titleKey = titleKey;
        ToggleCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
    }

    protected override void OnCultureChanged() => OnPropertyChanged(nameof(Title));

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set { if (Set(ref _isExpanded, value)) OnPropertyChanged(nameof(ChevronGlyph)); } }

    /// <summary>▾ when expanded, ▸ when collapsed (Segoe MDL2).</summary>
    public string ChevronGlyph => _isExpanded ? ((char)0xE70D).ToString() : ((char)0xE76C).ToString();

    /// <summary>Whether any item in the group is currently visible (drives the whole group's visibility).</summary>
    public bool HasVisibleItems => Items.Any(i => i.IsVisible);
    public void RaiseVisibility() => OnPropertyChanged(nameof(HasVisibleItems));
}

/// <summary>One toggle chip in the install-center category filter (全选 + per-group show/hide).</summary>
public sealed class CategoryFilterViewModel : LocalizedObject
{
    public string Key { get; }
    /// <summary>Localized category name (resolved live from the <c>cat.&lt;key&gt;</c> i18n key).</summary>
    public string Title => Localizer.T("cat." + Key);
    public CategoryFilterViewModel(string key) { Key = key; }
    protected override void OnCultureChanged() => OnPropertyChanged(nameof(Title));

    private bool _isChecked = true;
    public bool IsChecked { get => _isChecked; set { if (Set(ref _isChecked, value)) Changed?.Invoke(); } }

    /// <summary>Set without firing Changed (for bulk 全选 / 全不选).</summary>
    public void SetSilently(bool v) { if (_isChecked != v) { _isChecked = v; OnPropertyChanged(nameof(IsChecked)); } }

    public event Action? Changed;
}

public sealed class PlaceholderViewModel
{
    public string Title { get; }
    public string Message { get; }

    public PlaceholderViewModel(string title, string message)
    {
        Title = title;
        Message = message;
    }
}

/// <summary>One software card on the install center.</summary>
public sealed class AppItemViewModel : LocalizedObject
{
    private static readonly Dictionary<string, (string Bg, string Fg)> Palette = new()
    {
        ["dev"] = ("#FAECE7", "#712B13"),
        ["system"] = ("#F1EFE8", "#444441"),
        ["ide"] = ("#E6F1FB", "#0C447C"),
        ["ai"] = ("#EEEDFE", "#3C3489"),
        ["office"] = ("#EAF3DE", "#27500A"),
        ["media"] = ("#FBEAF0", "#72243E"),
        ["db-api"] = ("#E1F5EE", "#085041"),
        ["vm"] = ("#FAEEDA", "#633806"),
        ["games"] = ("#F1EFE8", "#444441"),
        ["server"] = ("#E1F5EE", "#085041"),
        ["browser"] = ("#E6F1FB", "#0C447C"),
        ["proxy"] = ("#E1F5EE", "#085041"),
        ["dict"] = ("#EEEDFE", "#3C3489"),
        ["hwmon"] = ("#FBEAF0", "#72243E"),
        ["tools"] = ("#F1EFE8", "#444441"),
    };

    public CatalogItem Model { get; }

    public AppItemViewModel(CatalogItem model)
    {
        Model = model;
        _isSelected = model.Default;
        var (bg, fg) = Palette.TryGetValue(model.Category, out var c) ? c : ("#F1EFE8", "#444441");
        ChipBackground = (Brush)new BrushConverter().ConvertFromString(bg)!;
        ChipForeground = (Brush)new BrushConverter().ConvertFromString(fg)!;
    }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Summary => Model.SummaryFor(Localizer.Current) ?? "";
    public string Method => Model.Install.Method;
    public string Badge => Model.Name.Length > 0 ? Model.Name[..1].ToUpperInvariant() : "?";
    public Brush ChipBackground { get; }
    public Brush ChipForeground { get; }

    /// <summary>Brand icon thumbnail (assets/icons/&lt;id&gt;.png); null falls back to the letter badge.</summary>
    public ImageSource? IconImage { get; private set; }
    public bool HasIcon => IconImage != null;
    public bool ShowLetter => IconImage == null;

    public void LoadIcon(string repoRoot)
    {
        try
        {
            // Bundled icon first; else a previously downloaded icon-cache entry.
            var path = Path.Combine(repoRoot, "assets", "icons", Model.Id + ".png");
            if (!File.Exists(path))
            {
                var cache = IconCache.PathFor(Model.Id);
                if (!File.Exists(cache)) return;   // keep the letter fallback
                path = cache;
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;          // don't lock the file
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            SetIcon(bmp);
        }
        catch { /* keep the letter fallback */ }
    }

    /// <summary>After a background icon-cache fetch, adopt the newly cached icon if we still have none.</summary>
    public void ReloadFromCache()
    {
        if (IconImage != null) return;
        if (IconCache.Load(Model.Id) is { } img) SetIcon(img);
    }

    private void SetIcon(ImageSource img)
    {
        IconImage = img;
        OnPropertyChanged(nameof(IconImage));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(ShowLetter));
    }

    /// <summary>Override the icon with the app's real icon extracted from its installed .exe.</summary>
    public void SetIconFromExe(string exePath)
    {
        var img = IconExtractor.FromExe(exePath);
        if (img == null) return;
        IconImage = img;
        OnPropertyChanged(nameof(IconImage));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(ShowLetter));
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) SelectionChanged?.Invoke(); }
    }

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    private bool? _installed;
    public bool? Installed
    {
        get => _installed;
        set
        {
            if (Set(ref _installed, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsInstalled));
                OnPropertyChanged(nameof(ShowLaunch));
                OnPropertyChanged(nameof(ShowStop));
                OnPropertyChanged(nameof(NotInstalled));
                OnPropertyChanged(nameof(CanUpdate));
                OnPropertyChanged(nameof(CanUninstall));
            }
        }
    }

    public bool IsInstalled => _installed == true;
    public string StatusText => _installed switch
    {
        null => Localizer.T("install.status.checking"),
        true => Localizer.T("install.status.installed"),
        _ => Localizer.T("install.status.notInstalled"),
    };

    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Summary));
    }

    // Right-click context-menu gating.
    public bool NotInstalled => _installed == false;
    public bool CanUpdate => IsInstalled && WinDeploy.Core.Engine.Updater.CanUpdate(Model);
    public bool CanUninstall => IsInstalled && Model.Install.Method is "winget" or "winget-bundle" or "portable" or "git";
    public bool HasHomepage => !string.IsNullOrWhiteSpace(Model.Homepage);

    private bool _hasRunningProc;
    /// <summary>The software currently has running processes (live-updated while the list is visible).</summary>
    public bool HasRunningProc
    {
        get => _hasRunningProc;
        set
        {
            if (!Set(ref _hasRunningProc, value)) return;
            OnPropertyChanged(nameof(ShowLaunch));
            OnPropertyChanged(nameof(ShowStop));
        }
    }
    public bool ShowLaunch => IsInstalled && !_hasRunningProc;   // ▶ 启动
    public bool ShowStop => IsInstalled && _hasRunningProc;      // ■ 停止

    private bool _hasUpdate;
    /// <summary>An upgrade is available (set during the startup update check).</summary>
    public bool HasUpdate
    {
        get => _hasUpdate;
        set { if (Set(ref _hasUpdate, value)) OnPropertyChanged(nameof(ShowUpdateBadge)); }
    }

    private bool _badgeHidden;
    /// <summary>User toggled "忽略可更新" — hides the badge without clearing HasUpdate.</summary>
    public bool BadgeHidden
    {
        get => _badgeHidden;
        set { if (Set(ref _badgeHidden, value)) OnPropertyChanged(nameof(ShowUpdateBadge)); }
    }

    public bool ShowUpdateBadge => _hasUpdate && !_badgeHidden;

    public event Action? SelectionChanged;
}

public sealed class CategoryGroupViewModel : LocalizedObject
{
    public string Key { get; }
    /// <summary>Localized category name (resolved live from the <c>cat.&lt;key&gt;</c> i18n key).</summary>
    public string Title => Localizer.T("cat." + Key);
    public System.Collections.ObjectModel.ObservableCollection<AppItemViewModel> Items { get; } = new();

    public CategoryGroupViewModel(string key)
    {
        Key = key;
    }

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public string CountText => Localizer.Format("install.countSelected", SelectedCount, Items.Count);

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    public void RaiseCount()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CountText));
    }

    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CountText));
    }
}

/// <summary>One row in the running-progress list: status pill + start/end/duration + expandable steps.</summary>
public sealed class ProgressItemViewModel : LocalizedObject
{
    public string Id { get; set; } = "";
    public string Name { get; }
    public string Method { get; }
    public System.Collections.ObjectModel.ObservableCollection<string> Details { get; } = new();
    public RelayCommand ToggleDetailsCommand { get; }

    public ProgressItemViewModel(string name, string method)
    {
        Name = name;
        Method = method;
        ToggleDetailsCommand = new RelayCommand(_ => IsDetailsOpen = !IsDetailsOpen);
    }

    /// <summary>queued | running | ok | failed | skip — drives the row pill colour via DataTrigger
    /// AND the localized <see cref="Status"/> pill text.</summary>
    private string _kind = "queued";
    public string Kind { get => _kind; set { if (Set(ref _kind, value)) OnPropertyChanged(nameof(Status)); } }

    /// <summary>Localized status pill text, derived from <see cref="Kind"/>.</summary>
    public string Status => _kind switch
    {
        "queued" => Localizer.T("progress.kind.queued"),
        "running" => Localizer.T("progress.kind.running"),
        "ok" => Localizer.T("progress.kind.ok"),
        "failed" => Localizer.T("progress.kind.failed"),
        "skip" => Localizer.T("progress.kind.skip"),
        _ => _kind,
    };

    public DateTime? StartTime { get; private set; }
    public DateTime? EndTime { get; private set; }

    public void MarkStarted() { StartTime = DateTime.Now; Raise(); }
    public void MarkEnded() { EndTime = DateTime.Now; Raise(); }

    private string? _timeOverride, _durationOverride;

    public string TimeText
    {
        get
        {
            if (_timeOverride != null) return _timeOverride;
            if (StartTime == null) return "";
            var s = StartTime.Value.ToString("HH:mm:ss");
            return EndTime == null ? Localizer.Format("progress.time.start", s) : $"{s} → {EndTime.Value:HH:mm:ss}";
        }
    }

    public string DurationText
    {
        get
        {
            if (_durationOverride != null) return _durationOverride;
            if (StartTime == null || EndTime == null) return "";
            return FormatDuration(EndTime.Value - StartTime.Value);
        }
    }

    public static string FormatDuration(TimeSpan d)
        => d.TotalSeconds >= 60
            ? Localizer.Format("progress.dur.minSec", (int)d.TotalMinutes, d.Seconds)
            : Localizer.Format("progress.dur.sec", d.TotalSeconds);

    /// <summary>Populate a row from a persisted history record (no live timing).</summary>
    public void LoadHistorical(string timeText, string durationText, IEnumerable<string> steps)
    {
        _timeOverride = timeText;
        _durationOverride = durationText;
        foreach (var s in steps) Details.Add(s);
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(ExpandGlyph));
    }

    public void AddDetail(string line)
    {
        Details.Add(line);
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(ExpandGlyph));
    }
    public bool HasDetails => Details.Count > 0;

    private bool _isDetailsOpen;
    public bool IsDetailsOpen
    {
        get => _isDetailsOpen;
        set { if (Set(ref _isDetailsOpen, value)) OnPropertyChanged(nameof(ExpandGlyph)); }
    }

    /// <summary>Left-side expand indicator: ▶ collapsed, ▼ open, blank when there are no details.</summary>
    public string ExpandGlyph => !HasDetails ? "" : _isDetailsOpen ? "▼" : "▶";

    private void Raise()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DurationText));
    }

    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DurationText));
    }
}
