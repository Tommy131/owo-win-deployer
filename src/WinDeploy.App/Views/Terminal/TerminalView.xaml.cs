using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.App.ViewModels;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Terminal;

public partial class TerminalView : UserControl
{
    private TerminalViewModel? _vm;
    private bool _wired;
    private Storyboard? _flicker;

    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalViewModel vm) return;
        _vm = vm;
        if (!_wired)
        {
            _wired = true;
            // The surface is part of this view instance (wire once): route input/resize to whichever session
            // is currently active — sessions persist in the background regardless of which tab is shown.
            Surface.Send += s => _vm?.ActiveSession?.Send(s);
            Surface.ViewportChanged += (c, r) => _vm?.ActiveSession?.SetViewport(c, r);
        }
        // The VM outlives the view, so (un)subscribe its events per load to avoid leaks / duplicates.
        vm.ActiveChanged -= OnActiveChanged;
        vm.ActiveChanged += OnActiveChanged;
        TerminalFx.Changed -= OnFxChanged;
        TerminalFx.Changed += OnFxChanged;

        vm.EnsureInitialSession();               // first visit: open the default shell
        BindActiveSurface();
        ApplyFx();
        // Start/resize the active PTY once layout has given the surface its real size, so the shell initializes
        // at the right width (a start-at-80-then-resize desyncs PSReadLine's line wrapping).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_vm?.ActiveSession != null) { Surface.ReportSize(); Surface.Focus(); }
        }));
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        TerminalFx.Changed -= OnFxChanged;
        if (_vm != null) _vm.ActiveChanged -= OnActiveChanged;
    }

    /// <summary>Active session swapped (or its PTY restarted): re-bind the surface to its screen and resize
    /// that PTY to the current surface size.</summary>
    private void OnActiveChanged()
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            BindActiveSurface();
            if (_vm?.ActiveSession != null) { Surface.ReportSize(); Surface.Focus(); }
        }));

    private void BindActiveSurface()
    {
        var active = _vm?.ActiveSession;
        if (active != null) Surface.SetScreen(active.Screen);
        EmptyHint.Visibility = active == null ? Visibility.Visible : Visibility.Collapsed;
        Surface.Visibility = active == null ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>The 新建 button: a themed dropdown of the shells detected on this machine.</summary>
    private void OnNewSessionClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not Button btn) return;
        var menu = new ContextMenu
        {
            Style = TryFindResource("ThemedContextMenu") as Style,
            PlacementTarget = btn,
            Placement = PlacementMode.Bottom,
        };
        foreach (var shell in _vm.AvailableShells)
        {
            var item = new MenuItem { Header = shell.Name, Style = TryFindResource("ThemedMenuItem") as Style };
            if (ShellCatalog.IconFor(shell) is { } icon)
                item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
            var captured = shell;
            item.Click += (_, _) => _vm?.NewSessionCommand.Execute(captured);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = Localizer.T("term.menu.none"), IsEnabled = false, Style = TryFindResource("ThemedMenuItem") as Style });
        menu.IsOpen = true;
    }

    private void OnFxChanged() => Dispatcher.BeginInvoke(new Action(ApplyFx));

    private void ApplyFx()
    {
        var hacker = TerminalFx.Hacker;
        var crt = TerminalFx.Crt;
        var rain = TerminalFx.CodeRain;

        // ── hacker palette: green phosphor text + black panel + green glow ──
        Surface.Hacker = hacker;
        Host.Effect = hacker
            ? new DropShadowEffect { Color = Color.FromRgb(0x2E, 0xFF, 0x6A), BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6 }
            : null;
        Host.Background = hacker
            ? new SolidColorBrush(Color.FromRgb(0x05, 0x09, 0x05))
            : TryFindResource("CardBg") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

        // ── background code-rain (transparent surface so it shows through empty cells) ──
        Surface.TransparentBg = rain;
        Rain.Visibility = rain ? Visibility.Visible : Visibility.Collapsed;
        Rain.Opacity = TerminalFx.CodeOpacity;
        Rain.Speed = TerminalFx.Speed;
        if (rain) Rain.Start(); else Rain.Stop();

        // ── CRT: scanlines + panel flicker ──
        Scanlines.Visibility = crt ? Visibility.Visible : Visibility.Collapsed;
        if (crt) StartFlicker(); else StopFlicker();
    }

    private void StartFlicker()
    {
        if (_flicker == null)
        {
            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.00, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.90, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.00, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.97, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.00, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
            Storyboard.SetTarget(anim, Host);
            Storyboard.SetTargetProperty(anim, new PropertyPath(OpacityProperty));
            _flicker = new Storyboard();
            _flicker.Children.Add(anim);
        }
        _flicker.Begin();
    }

    private void StopFlicker()
    {
        _flicker?.Stop();
        Host.Opacity = 1.0;
    }
}
