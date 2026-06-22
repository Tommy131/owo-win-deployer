using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

public sealed class NavItemViewModel
{
    public string Glyph { get; }
    public string Label { get; }
    public object Page { get; }

    public NavItemViewModel(string glyph, string label, object page)
    {
        Glyph = glyph;
        Label = label;
        Page = page;
    }
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
public sealed class AppItemViewModel : ObservableObject
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
    public string Summary => Model.Summary ?? "";
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
            var path = Path.Combine(repoRoot, "assets", "icons", Model.Id + ".png");
            if (!File.Exists(path)) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;          // don't lock the file
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            IconImage = bmp;
            OnPropertyChanged(nameof(IconImage));
            OnPropertyChanged(nameof(HasIcon));
            OnPropertyChanged(nameof(ShowLetter));
        }
        catch { /* keep the letter fallback */ }
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
            }
        }
    }

    public bool IsInstalled => _installed == true;
    public string StatusText => _installed switch { null => "检测中…", true => "已装", _ => "未装" };

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

public sealed class CategoryGroupViewModel : ObservableObject
{
    public string Key { get; }
    public string Title { get; }
    public System.Collections.ObjectModel.ObservableCollection<AppItemViewModel> Items { get; } = new();

    public CategoryGroupViewModel(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public string CountText => $"{SelectedCount} / {Items.Count} 已选";

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    public void RaiseCount()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CountText));
    }
}

/// <summary>One row in the running-progress list: status pill + start/end/duration + expandable steps.</summary>
public sealed class ProgressItemViewModel : ObservableObject
{
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

    private string _status = "排队";
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>queued | running | ok | failed | skip — drives the row pill colour via DataTrigger.</summary>
    private string _kind = "queued";
    public string Kind { get => _kind; set => Set(ref _kind, value); }

    public DateTime? StartTime { get; private set; }
    public DateTime? EndTime { get; private set; }

    public void MarkStarted() { StartTime = DateTime.Now; Raise(); }
    public void MarkEnded() { EndTime = DateTime.Now; Raise(); }

    public string TimeText
    {
        get
        {
            if (StartTime == null) return "";
            var s = StartTime.Value.ToString("HH:mm:ss");
            return EndTime == null ? $"开始 {s}" : $"{s} → {EndTime.Value:HH:mm:ss}";
        }
    }

    public string DurationText
    {
        get
        {
            if (StartTime == null || EndTime == null) return "";
            var d = EndTime.Value - StartTime.Value;
            return d.TotalSeconds >= 60 ? $"耗时 {(int)d.TotalMinutes}分{d.Seconds}秒" : $"耗时 {d.TotalSeconds:0.0}秒";
        }
    }

    public void AddDetail(string line)
    {
        Details.Add(line);
        OnPropertyChanged(nameof(HasDetails));
    }
    public bool HasDetails => Details.Count > 0;

    private bool _isDetailsOpen;
    public bool IsDetailsOpen { get => _isDetailsOpen; set => Set(ref _isDetailsOpen, value); }

    private void Raise()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DurationText));
    }
}
