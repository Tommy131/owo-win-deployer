using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinDeploy.App.Services;

namespace WinDeploy.App.Views.Terminal;

/// <summary>Renders a <see cref="VtScreen"/> as a monospaced grid and turns keyboard / wheel input into the
/// VT byte sequences a PTY expects. It owns no process — the host wires <see cref="Send"/> to the PTY's input
/// and <see cref="ViewportChanged"/> to the PTY resize. Two looks: a green-phosphor "hacker" palette (default)
/// or the plain theme palette.</summary>
public sealed class TerminalSurface : FrameworkElement
{
    private VtScreen? _screen;
    private bool _hacker = true;
    private readonly Typeface _typeface = new(new FontFamily("Cascadia Mono, Consolas, Consolas"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private const double FontSize = 14.0;
    private double _cw = 8, _ch = 16;
    private int _cols = 80, _rows = 25;
    private long _lastVersion = -1;
    private int _scrollOffset;
    private bool _caretOn = true;
    private int _caretTick;
    private readonly DispatcherTimer _timer;

    /// <summary>VT input bytes to forward to the PTY (keystrokes, paste, control codes).</summary>
    public event Action<string>? Send;
    /// <summary>Raised when the pixel size maps to a new (cols, rows) cell grid.</summary>
    public event Action<int, int>? ViewportChanged;

    public TerminalSurface()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;
        Measure();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => { _timer.Start(); Focus(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void SetScreen(VtScreen screen) { _screen = screen; _lastVersion = -1; _scrollOffset = 0; InvalidateVisual(); }

    public bool Hacker
    {
        get => _hacker;
        set { _hacker = value; InvalidateVisual(); }
    }

    private bool _transparentBg;
    /// <summary>When true the surface paints a transparent (still hit-testable) base so a backdrop layer
    /// (code-rain) shows through the empty cells; independent of the <see cref="Hacker"/> palette.</summary>
    public bool TransparentBg
    {
        get => _transparentBg;
        set { _transparentBg = value; InvalidateVisual(); }
    }

    private void Measure()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(new string('M', 10), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, FontSize, Brushes.White, dpi);
        _cw = Math.Max(1, ft.WidthIncludingTrailingWhitespace / 10);
        _ch = Math.Max(1, ft.Height);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_screen == null) return;
        var v = _screen.Version;
        // Snap to the bottom whenever new output arrives, so a streaming command stays in view.
        if (v != _lastVersion) { _scrollOffset = 0; _lastVersion = v; InvalidateVisual(); }
        if (++_caretTick >= 16) { _caretTick = 0; _caretOn = !_caretOn; if (IsFocused) InvalidateVisual(); }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Measure();
        var cols = Math.Max(2, (int)(info.NewSize.Width / _cw));
        var rows = Math.Max(1, (int)(info.NewSize.Height / _ch));
        if (cols != _cols || rows != _rows) { _cols = cols; _rows = rows; ViewportChanged?.Invoke(cols, rows); }
        InvalidateVisual();
    }

    /// <summary>Force a viewport report from the current pixel size — the host calls this once after layout so
    /// the PTY starts at the true grid size (avoids a start-at-80 then resize that desyncs line wrapping).</summary>
    public void ReportSize()
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        Measure();
        _cols = Math.Max(2, (int)(ActualWidth / _cw));
        _rows = Math.Max(1, (int)(ActualHeight / _ch));
        ViewportChanged?.Invoke(_cols, _rows);
    }

    // ── palette ─────────────────────────────────────────────────────────────────
    private Color DefaultBg => _hacker ? Color.FromRgb(0x06, 0x0A, 0x06) : ResColor("CardBg", Color.FromRgb(0x1E, 0x1E, 0x1E));
    private Color DefaultFg => _hacker ? Color.FromRgb(0x3D, 0xFF, 0x74) : ResColor("TextPrimary", Color.FromRgb(0xDD, 0xDD, 0xDD));
    private Color CursorColor => _hacker ? Color.FromRgb(0x8C, 0xFF, 0xB0) : ResColor("Accent", Color.FromRgb(0x4D, 0x9D, 0xFF));

    private static Color ResColor(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    private static Color FromRgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var bg = DefaultBg;
        var fg = DefaultFg;
        // Transparent (but hit-testable) base when a backdrop is behind, so the code-rain shows through empty
        // cells; otherwise an opaque background in the current palette.
        dc.DrawRectangle(_transparentBg ? Brushes.Transparent : new SolidColorBrush(bg), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_screen == null) return;

        var frame = _screen.Snapshot(_scrollOffset);
        var cols = frame.Cols; var rows = frame.Rows;

        for (var r = 0; r < rows; r++)
        {
            var y = r * _ch;
            var c = 0;
            while (c < cols)
            {
                var cell = frame.Cells[r * cols + c];
                var (cf, cb) = Resolve(cell, fg, bg);
                // Coalesce a run of same-attribute cells into one draw.
                var start = c;
                var run = new System.Text.StringBuilder();
                while (c < cols)
                {
                    var n = frame.Cells[r * cols + c];
                    var (nf, nb) = Resolve(n, fg, bg);
                    if (nf != cf || nb != cb || (n.Flags & VtCell.Underline) != (cell.Flags & VtCell.Underline)) break;
                    run.Append(n.Ch == '\0' ? ' ' : n.Ch);
                    c++;
                }
                var x = start * _cw;
                var w = (c - start) * _cw;
                if (cb != bg) dc.DrawRectangle(new SolidColorBrush(cb), null, new Rect(x, y, w + 0.5, _ch + 0.5));
                var text = run.ToString();
                if (text.Trim().Length > 0)
                {
                    var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        _typeface, FontSize, new SolidColorBrush(cf), dpi);
                    if ((cell.Flags & VtCell.Bold) != 0) ft.SetFontWeight(FontWeights.Bold);
                    if ((cell.Flags & VtCell.Underline) != 0) ft.SetTextDecorations(TextDecorations.Underline);
                    dc.DrawText(ft, new Point(x, y));
                }
            }
        }

        if (frame.CursorVisible && IsFocused && _caretOn)
        {
            var cx = frame.CursorX * _cw;
            var cy = frame.CursorY * _ch;
            dc.DrawRectangle(new SolidColorBrush(CursorColor) { Opacity = 0.6 }, null, new Rect(cx, cy, _cw, _ch));
        }
    }

    private (Color fg, Color bg) Resolve(VtCell cell, Color defFg, Color defBg)
    {
        var f = cell.Fg < 0 ? defFg : FromRgb(cell.Fg);
        var b = cell.Bg < 0 ? defBg : FromRgb(cell.Bg);
        if ((cell.Flags & VtCell.Inverse) != 0) (f, b) = (b, f);
        return (f, b);
    }

    // ── input ─────────────────────────────────────────────────────────────────
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _scrollOffset = Math.Max(0, _scrollOffset + (e.Delta > 0 ? 3 : -3));
        if (_screen != null) _scrollOffset = Math.Min(_scrollOffset, _screen.Snapshot(_scrollOffset).Scrollback + 3);
        _lastVersion = _screen?.Version ?? _lastVersion;   // don't let the timer immediately snap us back this tick
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && e.Text != "") { Send?.Invoke(e.Text); e.Handled = true; }
        base.OnTextInput(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        // Shift+PageUp/Down scroll the scrollback instead of going to the shell.
        if (shift && e.Key is Key.PageUp or Key.PageDown)
        {
            _scrollOffset = Math.Max(0, _scrollOffset + (e.Key == Key.PageUp ? _rows - 1 : -(_rows - 1)));
            _lastVersion = _screen?.Version ?? _lastVersion;
            InvalidateVisual(); e.Handled = true; return;
        }

        if (ctrl && !alt)
        {
            if (e.Key == Key.V) { Paste(); e.Handled = true; return; }
            if (e.Key == Key.C && shift) { e.Handled = false; return; }   // leave Ctrl+Shift+C for future copy
            if (e.Key >= Key.A && e.Key <= Key.Z) { Send?.Invoke(((char)(e.Key - Key.A + 1)).ToString()); e.Handled = true; return; }
        }

        string? seq = e.Key switch
        {
            Key.Enter => "\r",
            Key.Back => "\x7f",
            Key.Tab => "\t",
            Key.Escape => "\x1b",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            _ => null,
        };
        if (seq != null) { Send?.Invoke(seq); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    private void Paste()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var t = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
                Send?.Invoke(t);
            }
        }
        catch { /* clipboard busy */ }
    }
}
