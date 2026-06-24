using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinDeploy.App.Views.Terminal;

/// <summary>A backdrop of real-looking source code scrolling upward (forward reading order), with lightweight syntax
/// highlighting (keywords / types / strings / numbers / comments). Purely decorative and non-interactive —
/// shown behind the terminal text when hacker FX is on, dim enough that the bright terminal output stays
/// readable on top.</summary>
public sealed class CodeRain : FrameworkElement
{
    private const double RowH = 15, FontSize = 12.5, PxPerFrame = 1.0;

    private readonly Typeface _tf = new(new FontFamily("Cascadia Mono, Consolas, Consolas"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly DispatcherTimer _timer;
    private double _scroll;

    /// <summary>Scroll-speed multiplier (0.2–4.0), set from the Settings slider.</summary>
    public double Speed { get; set; } = 1.0;

    public CodeRain()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => { _scroll += PxPerFrame * Math.Max(0.05, Speed); InvalidateVisual(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void Start() { if (!_timer.IsEnabled) _timer.Start(); }
    public void Stop() => _timer.Stop();

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var o = _scroll % RowH;
        var k = (int)(_scroll / RowH);
        var visible = (int)(ActualHeight / RowH) + 2;
        for (var i = 0; i <= visible; i++)
        {
            var y = i * RowH - o;                              // slides up as _scroll grows
            var line = Lines[Mod(k + i, Lines.Length)];        // forward order: top = earlier line, new at bottom
            DrawLine(dc, line, y, dpi);
        }
    }

    private void DrawLine(DrawingContext dc, string line, double y, double dpi)
    {
        if (line.Length == 0) return;
        var ft = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _tf, FontSize,
            new SolidColorBrush(Color.FromRgb(0x8F, 0xBF, 0x9F)) { Opacity = 0.85 }, dpi);
        foreach (var (start, len, brush) in Tokenize(line))
            ft.SetForegroundBrush(brush, start, len);
        dc.DrawText(ft, new Point(4, y));
    }

    private static int Mod(int a, int m) => ((a % m) + m) % m;

    // ── tiny tokenizer ──────────────────────────────────────────────────────────
    private static readonly Brush Keyword = Dim(0x7A, 0xA2, 0xFF, 1.0);
    private static readonly Brush Type = Dim(0x56, 0xC8, 0xC8, 0.95);
    private static readonly Brush Str = Dim(0xD6, 0xA5, 0x6A, 0.95);
    private static readonly Brush Num = Dim(0x5B, 0xD6, 0xA8, 0.92);
    private static readonly Brush Comment = Dim(0x5E, 0x7E, 0x68, 0.60);

    private static SolidColorBrush Dim(byte r, byte g, byte b, double a)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b)) { Opacity = a };
        br.Freeze();
        return br;
    }

    private static readonly HashSet<string> Kw = new(StringComparer.Ordinal)
    {
        "var", "let", "const", "function", "return", "if", "else", "for", "while", "foreach", "switch", "case",
        "break", "continue", "class", "struct", "enum", "interface", "record", "public", "private", "protected",
        "internal", "static", "readonly", "void", "new", "async", "await", "using", "namespace", "import", "from",
        "export", "default", "def", "func", "fn", "package", "type", "true", "false", "null", "nil", "None", "print",
        "try", "catch", "throw", "finally", "this", "self", "yield", "match", "use", "pub", "mut", "impl", "go",
        "defer", "range", "select", "require", "then", "do", "end", "local", "in", "is", "as", "and", "or", "not",
    };

    private static IEnumerable<(int start, int len, Brush brush)> Tokenize(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            var ch = s[i];
            if ((ch == '/' && i + 1 < s.Length && s[i + 1] == '/') || ch == '#')
            {
                yield return (i, s.Length - i, Comment);
                yield break;
            }
            if (ch is '"' or '\'')
            {
                var j = i + 1;
                while (j < s.Length && s[j] != ch) j++;
                if (j < s.Length) j++;
                yield return (i, j - i, Str);
                i = j;
            }
            else if (char.IsDigit(ch))
            {
                var j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '.' or 'x')) j++;
                yield return (i, j - i, Num);
                i = j;
            }
            else if (char.IsLetter(ch) || ch == '_')
            {
                var j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                var word = s[i..j];
                if (Kw.Contains(word)) yield return (i, j - i, Keyword);
                else if (char.IsUpper(ch)) yield return (i, j - i, Type);
                i = j;
            }
            else i++;
        }
    }

    // A small "program" of varied lines (C# / JS / Rust / Go / Python / shell) cycled for the backdrop.
    private static readonly string[] Lines =
    {
        "public async Task<Result> DeployAsync(CatalogItem item, CancellationToken ct)",
        "{",
        "    var plan = Selection.Resolve(catalog, profile);   // topo-sort by depends",
        "    foreach (var step in plan.Ordered)",
        "    {",
        "        if (await Detection.IsInstalledAsync(step, ct)) { report.Skip(step); continue; }",
        "        var ok = await installer.RunAsync(step, ctx);",
        "        report.Record(step, ok ? Status.Ok : Status.Failed);",
        "    }",
        "    return report.Summarize();",
        "}",
        "",
        "const connect = async (host, port = 22) => {",
        "    const sock = await net.connect({ host, port });",
        "    sock.on('data', buf => terminal.write(decoder.decode(buf)));",
        "    return new Session(sock, { keepAlive: true });",
        "};",
        "",
        "fn parse_smart(buf: &[u8]) -> Option<NvmeHealthLog> {",
        "    let kelvin = u16::from_le_bytes([buf[1], buf[2]]);",
        "    let power_on = u64::from_le_bytes(buf[128..136].try_into().ok()?);",
        "    Some(NvmeHealthLog { temperature: kelvin.checked_sub(273), power_on })",
        "}",
        "",
        "func (s *Server) handleConn(c net.Conn) {",
        "    defer c.Close()",
        "    user, err := s.auth.Login(c)",
        "    if err != nil { log.Printf(\"auth failed: %v\", err); return }",
        "    s.sessions.Add(user, NewSession(c))",
        "}",
        "",
        "def reflow(grid, old_cols, new_cols, rows):",
        "    out = [[Cell() for _ in range(new_cols)] for _ in range(rows)]",
        "    for r in range(min(rows, len(grid))):",
        "        for c in range(min(old_cols, new_cols)):",
        "            out[r][c] = grid[r][c]",
        "    return out   # preserve content on resize",
        "",
        "#!/usr/bin/env bash",
        "ssh -l \"$USER\" -p \"${PORT:-22}\" \"$HOST\" 'uname -a && uptime'",
        "for pkg in git node go rust; do winget install --id \"$pkg\" -e; done",
        "",
        "[StructLayout(LayoutKind.Sequential)] struct COORD { public short X, Y; }",
        "CreatePseudoConsole(size, inRead, outWrite, 0, out hPC);   // real TTY",
        "UpdateProcThreadAttribute(attr, 0, PSEUDOCONSOLE_HANDLE, hPC, IntPtr.Size);",
    };
}
