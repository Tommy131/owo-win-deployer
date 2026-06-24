namespace WinDeploy.App.Services.Terminal;

/// <summary>One screen cell: a glyph plus its colors (ARGB 0xRRGGBB, or -1 for "use the default") and
/// attribute flags.</summary>
public struct VtCell
{
    public char Ch;
    public int Fg;     // 0xRRGGBB, or -1 = default foreground
    public int Bg;     // 0xRRGGBB, or -1 = default background
    public byte Flags; // bit0 bold, bit1 underline, bit2 inverse
    public const byte Bold = 1, Underline = 2, Inverse = 4;
}

/// <summary>A compact VT100/ANSI terminal emulator: a grid of <see cref="VtCell"/> driven by the UTF-8 escape
/// stream a ConPTY emits. Handles the common subset — printable text, CR/LF/BS/TAB, cursor movement (CUP/CUU…),
/// erase (ED/EL), insert/delete lines &amp; chars, SGR colors (16 / 256 / truecolor), scroll regions, the
/// alternate screen buffer and cursor show/hide — enough for shells, ssh, git, package managers and most TUIs.
/// Thread-safe: <see cref="Feed"/> runs on the PTY reader thread; the UI thread reads via <see cref="Snapshot"/>.</summary>
public sealed class VtScreen
{
    private readonly object _lock = new();

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    private VtCell[] _main = Array.Empty<VtCell>();
    private VtCell[] _alt = Array.Empty<VtCell>();
    private bool _altActive;
    private VtCell[] Cur => _altActive ? _alt : _main;

    private int _cx, _cy;            // cursor
    private int _savedCx, _savedCy;  // DECSC / ANSI.SYS save
    private int _top, _bottom;       // scroll region (inclusive, 0-based)
    private bool _wrapPending;
    private bool _cursorVisible = true;

    private int _fg = -1, _bg = -1;
    private byte _flags;
    private bool _autowrap = true;   // DECAWM (?7) — line editors toggle this; honoring it keeps redraws aligned

    /// <summary>Send a reply back to the PTY (e.g. a cursor-position report). Wired by the host to PTY input.</summary>
    public Action<string>? Respond;

    private readonly List<VtCell[]> _scrollback = new();
    private const int MaxScrollback = 5000;

    public long Version { get; private set; }

    // Standard xterm 16-color palette (0xRRGGBB).
    private static readonly int[] Pal16 =
    {
        0x000000, 0xCD0000, 0x00CD00, 0xCDCD00, 0x1E60D0, 0xCD00CD, 0x00CDCD, 0xE5E5E5,
        0x7F7F7F, 0xFF5555, 0x55FF55, 0xFFFF55, 0x5C9CFF, 0xFF55FF, 0x55FFFF, 0xFFFFFF,
    };

    public VtScreen(int cols, int rows) => Resize(cols, rows);

    // ── parser state ──────────────────────────────────────────────────────────
    private enum St { Ground, Esc, Csi, Osc }
    private St _state = St.Ground;
    private readonly System.Text.StringBuilder _params = new();
    private char _escInter;

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(1, rows);
        lock (_lock)
        {
            if (cols == Cols && rows == Rows) return;
            _main = Reflow(_main, Cols, Rows, cols, rows);
            _alt = Reflow(_alt, Cols, Rows, cols, rows);
            Cols = cols; Rows = rows;
            _top = 0; _bottom = rows - 1;
            _cx = Math.Min(_cx, cols - 1);
            _cy = Math.Min(_cy, rows - 1);
            _wrapPending = false;
            Version++;
        }
    }

    private static VtCell[] Reflow(VtCell[] old, int oc, int or, int nc, int nr)
    {
        var grid = NewGrid(nc, nr);
        for (var r = 0; r < Math.Min(or, nr); r++)
            for (var c = 0; c < Math.Min(oc, nc); c++)
                grid[r * nc + c] = old[r * oc + c];
        return grid;
    }

    private static VtCell[] NewGrid(int cols, int rows)
    {
        var g = new VtCell[cols * rows];
        for (var i = 0; i < g.Length; i++) g[i] = Blank(-1);
        return g;
    }

    private static VtCell Blank(int bg) => new() { Ch = ' ', Fg = -1, Bg = bg, Flags = 0 };

    public void Clear()
    {
        lock (_lock)
        {
            Array.Fill(_main, Blank(-1));
            Array.Fill(_alt, Blank(-1));
            _scrollback.Clear();
            _cx = _cy = 0; _wrapPending = false;
            Version++;
        }
    }

    /// <summary>Append a system/local line (not from the shell), e.g. an exit notice.</summary>
    public void FeedSystem(string text)
    {
        Feed("\r\n" + text + "\r\n");
    }

    public void Feed(string data)
    {
        lock (_lock)
        {
            foreach (var ch in data) Step(ch);
            Version++;
        }
    }

    private void Step(char ch)
    {
        switch (_state)
        {
            case St.Ground: Ground(ch); break;
            case St.Esc: Escape(ch); break;
            case St.Csi: Csi(ch); break;
            case St.Osc: Osc(ch); break;
        }
    }

    private void Ground(char ch)
    {
        switch (ch)
        {
            case '\x1b': _state = St.Esc; _escInter = '\0'; break;
            case '\r': _cx = 0; _wrapPending = false; break;
            case '\n': case '\v': case '\f': LineFeed(); break;
            case '\b': if (_cx > 0) _cx--; _wrapPending = false; break;
            case '\t': _cx = Math.Min(Cols - 1, (_cx / 8 + 1) * 8); break;
            case '\a': break; // bell
            default:
                if (ch >= ' ') Put(ch);
                break;
        }
    }

    private void Escape(char ch)
    {
        switch (ch)
        {
            case '[': _params.Clear(); _state = St.Csi; break;
            case ']': _params.Clear(); _state = St.Osc; break;
            case '(': case ')': case '*': case '+': _escInter = ch; break;   // charset designation: eat next char
            case '7': _savedCx = _cx; _savedCy = _cy; _state = St.Ground; break;
            case '8': _cx = _savedCx; _cy = _savedCy; _wrapPending = false; _state = St.Ground; break;
            case 'M': ReverseIndex(); _state = St.Ground; break;
            case 'D': LineFeed(); _state = St.Ground; break;
            case 'E': _cx = 0; LineFeed(); _state = St.Ground; break;
            case 'c': FullReset(); _state = St.Ground; break;
            case '=': case '>': _state = St.Ground; break; // keypad mode
            default:
                if (_escInter != '\0') { _escInter = '\0'; _state = St.Ground; break; } // consumed charset arg
                _state = St.Ground;
                break;
        }
    }

    private void Csi(char ch)
    {
        if ((ch >= '0' && ch <= '9') || ch is ';' or '?' or ':' or '<' or '>' or '=' or '!')
        {
            if (_params.Length < 64) _params.Append(ch);
            return;
        }
        DispatchCsi(ch, _params.ToString());
        _state = St.Ground;
    }

    private void Osc(char ch)
    {
        // Operating System Command (e.g. window title) — consume until BEL or ST (ESC \). We ignore content.
        if (ch == '\a') { _state = St.Ground; return; }
        if (ch == '\x1b') { _escInter = 'O'; return; }       // expect '\' next
        if (_escInter == 'O') { _escInter = '\0'; _state = St.Ground; }
    }

    // ── CSI dispatch ────────────────────────────────────────────────────────────
    private void DispatchCsi(char cmd, string raw)
    {
        var priv = raw.StartsWith('?');
        var body = priv ? raw[1..] : raw;
        var ps = ParseParams(body);
        int P(int i, int def) => i < ps.Count && ps[i] > 0 ? ps[i] : (i < ps.Count && ps[i] == 0 ? 0 : def);
        int P1(int i) => i < ps.Count && ps[i] > 0 ? ps[i] : 1;   // movement default 1

        switch (cmd)
        {
            case 'A': _cy = Math.Max(_top, _cy - P1(0)); _wrapPending = false; break;
            case 'B': _cy = Math.Min(_bottom, _cy + P1(0)); _wrapPending = false; break;
            case 'C': _cx = Math.Min(Cols - 1, _cx + P1(0)); _wrapPending = false; break;
            case 'D': _cx = Math.Max(0, _cx - P1(0)); _wrapPending = false; break;
            case 'E': _cx = 0; _cy = Math.Min(_bottom, _cy + P1(0)); break;
            case 'F': _cx = 0; _cy = Math.Max(_top, _cy - P1(0)); break;
            case 'G': _cx = Clamp(P1(0) - 1, Cols); _wrapPending = false; break;
            case 'd': _cy = Clamp(P1(0) - 1, Rows); _wrapPending = false; break;
            case 'H': case 'f':
                _cy = Clamp(P1(0) - 1, Rows); _cx = Clamp(P1(1) - 1, Cols); _wrapPending = false; break;
            case 'J': EraseDisplay(P(0, 0)); break;
            case 'K': EraseLine(P(0, 0)); break;
            case 'L': InsertLines(P1(0)); break;
            case 'M': DeleteLines(P1(0)); break;
            case 'P': DeleteChars(P1(0)); break;
            case '@': InsertChars(P1(0)); break;
            case 'X': EraseChars(P1(0)); break;
            case 'S': ScrollUp(P1(0)); break;
            case 'T': ScrollDown(P1(0)); break;
            case 'r':
                _top = Clamp((ps.Count > 0 ? P1(0) : 1) - 1, Rows);
                _bottom = ps.Count > 1 && ps[1] > 0 ? Clamp(ps[1] - 1, Rows) : Rows - 1;
                if (_bottom <= _top) { _top = 0; _bottom = Rows - 1; }
                _cx = 0; _cy = _top; break;
            case 'm': ApplySgr(ps); break;
            case 's': _savedCx = _cx; _savedCy = _cy; break;
            case 'u': _cx = _savedCx; _cy = _savedCy; _wrapPending = false; break;
            case 'h': SetMode(priv, ps, true); break;
            case 'l': SetMode(priv, ps, false); break;
            case 'n':   // Device Status Report
                if (!priv && P(0, 0) == 6) Respond?.Invoke($"\x1b[{_cy + 1};{_cx + 1}R");
                else if (!priv && P(0, 0) == 5) Respond?.Invoke("\x1b[0n");
                break;
        }
    }

    private void SetMode(bool priv, List<int> ps, bool on)
    {
        if (!priv) return;
        foreach (var p in ps)
        {
            switch (p)
            {
                case 7: _autowrap = on; break;   // DECAWM
                case 25: _cursorVisible = on; break;
                case 47: case 1047: case 1049: SwitchAlt(on); break;
            }
        }
    }

    private void SwitchAlt(bool toAlt)
    {
        if (toAlt == _altActive) { if (toAlt) { Array.Fill(_alt, Blank(_bg)); _cx = _cy = 0; } return; }
        if (toAlt) { _savedCx = _cx; _savedCy = _cy; Array.Fill(_alt, Blank(_bg)); _cx = _cy = 0; }
        else { _cx = _savedCx; _cy = _savedCy; }
        _altActive = toAlt;
        _wrapPending = false;
    }

    private void ApplySgr(List<int> ps)
    {
        if (ps.Count == 0) ps.Add(0);
        for (var i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            switch (p)
            {
                case 0: _fg = -1; _bg = -1; _flags = 0; break;
                case 1: _flags |= VtCell.Bold; break;
                case 4: _flags |= VtCell.Underline; break;
                case 7: _flags |= VtCell.Inverse; break;
                case 22: _flags &= unchecked((byte)~VtCell.Bold); break;
                case 24: _flags &= unchecked((byte)~VtCell.Underline); break;
                case 27: _flags &= unchecked((byte)~VtCell.Inverse); break;
                case 39: _fg = -1; break;
                case 49: _bg = -1; break;
                case 38: i = ExtColor(ps, i, ref _fg); break;
                case 48: i = ExtColor(ps, i, ref _bg); break;
                default:
                    if (p >= 30 && p <= 37) _fg = Pal16[p - 30];
                    else if (p >= 90 && p <= 97) _fg = Pal16[8 + p - 90];
                    else if (p >= 40 && p <= 47) _bg = Pal16[p - 40];
                    else if (p >= 100 && p <= 107) _bg = Pal16[8 + p - 100];
                    break;
            }
        }
    }

    /// <summary>Parse 38/48 extended color (5;n = 256-color, 2;r;g;b = truecolor). Returns the new index.</summary>
    private int ExtColor(List<int> ps, int i, ref int target)
    {
        if (i + 1 >= ps.Count) return i;
        var mode = ps[i + 1];
        if (mode == 5 && i + 2 < ps.Count) { target = Color256(ps[i + 2]); return i + 2; }
        if (mode == 2 && i + 4 < ps.Count) { target = (ps[i + 2] << 16) | (ps[i + 3] << 8) | ps[i + 4]; return i + 4; }
        return i + 1;
    }

    private static int Color256(int n)
    {
        n &= 0xFF;
        if (n < 16) return Pal16[n];
        if (n < 232) { n -= 16; int r = n / 36, g = n / 6 % 6, b = n % 6; int S(int v) => v == 0 ? 0 : 55 + v * 40; return (S(r) << 16) | (S(g) << 8) | S(b); }
        var v2 = 8 + (n - 232) * 10; return (v2 << 16) | (v2 << 8) | v2;
    }

    private static List<int> ParseParams(string body)
    {
        var list = new List<int>();
        if (body.Length == 0) return list;
        foreach (var part in body.Split(';'))
        {
            var seg = part.Split(':')[0];   // sub-params (rare) — keep the first
            list.Add(int.TryParse(seg, out var v) ? v : 0);
        }
        return list;
    }

    private static int Clamp(int v, int max) => Math.Max(0, Math.Min(max - 1, v));

    // ── text + scrolling primitives ─────────────────────────────────────────────
    private void Put(char ch)
    {
        if (_wrapPending) { _cx = 0; LineFeed(); _wrapPending = false; }
        Cur[_cy * Cols + _cx] = new VtCell { Ch = ch, Fg = _fg, Bg = _bg, Flags = _flags };
        if (_cx >= Cols - 1) { if (_autowrap) _wrapPending = true; }   // DECAWM off: overwrite the last column in place
        else _cx++;
    }

    private void LineFeed()
    {
        if (_cy == _bottom) ScrollUp(1);
        else if (_cy < Rows - 1) _cy++;
    }

    private void ReverseIndex()
    {
        if (_cy == _top) ScrollDown(1);
        else if (_cy > 0) _cy--;
    }

    private void ScrollUp(int n)
    {
        n = Math.Min(n, _bottom - _top + 1);
        var g = Cur;
        var pushHistory = !_altActive && _top == 0 && _bottom == Rows - 1;
        for (var k = 0; k < n; k++)
        {
            if (pushHistory)
            {
                var line = new VtCell[Cols];
                Array.Copy(g, _top * Cols, line, 0, Cols);
                _scrollback.Add(line);
                if (_scrollback.Count > MaxScrollback) _scrollback.RemoveAt(0);
            }
            for (var r = _top; r < _bottom; r++)
                Array.Copy(g, (r + 1) * Cols, g, r * Cols, Cols);
            for (var c = 0; c < Cols; c++) g[_bottom * Cols + c] = Blank(_bg);
        }
    }

    private void ScrollDown(int n)
    {
        n = Math.Min(n, _bottom - _top + 1);
        var g = Cur;
        for (var k = 0; k < n; k++)
        {
            for (var r = _bottom; r > _top; r--)
                Array.Copy(g, (r - 1) * Cols, g, r * Cols, Cols);
            for (var c = 0; c < Cols; c++) g[_top * Cols + c] = Blank(_bg);
        }
    }

    private void InsertLines(int n)
    {
        if (_cy < _top || _cy > _bottom) return;
        n = Math.Min(n, _bottom - _cy + 1);
        var g = Cur;
        for (var r = _bottom; r >= _cy + n; r--) Array.Copy(g, (r - n) * Cols, g, r * Cols, Cols);
        for (var r = _cy; r < _cy + n; r++) for (var c = 0; c < Cols; c++) g[r * Cols + c] = Blank(_bg);
    }

    private void DeleteLines(int n)
    {
        if (_cy < _top || _cy > _bottom) return;
        n = Math.Min(n, _bottom - _cy + 1);
        var g = Cur;
        for (var r = _cy; r <= _bottom - n; r++) Array.Copy(g, (r + n) * Cols, g, r * Cols, Cols);
        for (var r = _bottom - n + 1; r <= _bottom; r++) for (var c = 0; c < Cols; c++) g[r * Cols + c] = Blank(_bg);
    }

    private void InsertChars(int n)
    {
        n = Math.Min(n, Cols - _cx);
        var g = Cur; var row = _cy * Cols;
        for (var c = Cols - 1; c >= _cx + n; c--) g[row + c] = g[row + c - n];
        for (var c = _cx; c < _cx + n; c++) g[row + c] = Blank(_bg);
    }

    private void DeleteChars(int n)
    {
        n = Math.Min(n, Cols - _cx);
        var g = Cur; var row = _cy * Cols;
        for (var c = _cx; c < Cols - n; c++) g[row + c] = g[row + c + n];
        for (var c = Cols - n; c < Cols; c++) g[row + c] = Blank(_bg);
    }

    private void EraseChars(int n)
    {
        var g = Cur; var row = _cy * Cols;
        for (var c = _cx; c < Math.Min(Cols, _cx + n); c++) g[row + c] = Blank(_bg);
    }

    private void EraseLine(int mode)
    {
        var g = Cur; var row = _cy * Cols;
        int from = mode == 1 ? 0 : _cx, to = mode == 0 ? Cols - 1 : _cx;
        if (mode == 2) { from = 0; to = Cols - 1; }
        for (var c = from; c <= to && c < Cols; c++) g[row + c] = Blank(_bg);
    }

    private void EraseDisplay(int mode)
    {
        var g = Cur;
        if (mode == 2 || mode == 3) { Array.Fill(g, Blank(_bg)); return; }
        if (mode == 0) { EraseLine(0); for (var r = _cy + 1; r < Rows; r++) for (var c = 0; c < Cols; c++) g[r * Cols + c] = Blank(_bg); }
        else if (mode == 1) { EraseLine(1); for (var r = 0; r < _cy; r++) for (var c = 0; c < Cols; c++) g[r * Cols + c] = Blank(_bg); }
    }

    private void FullReset()
    {
        _fg = -1; _bg = -1; _flags = 0;
        _top = 0; _bottom = Rows - 1;
        _cx = _cy = 0; _wrapPending = false;
        _altActive = false; _cursorVisible = true; _autowrap = true;
        Array.Fill(_main, Blank(-1));
        Array.Fill(_alt, Blank(-1));
    }

    // ── snapshot for the renderer ───────────────────────────────────────────────
    public readonly record struct Frame(VtCell[] Cells, int Cols, int Rows, int CursorX, int CursorY,
        bool CursorVisible, int Scrollback, long Version);

    /// <summary>Copy the visible window (offset lines up from the bottom into scrollback) plus cursor — a
    /// stable snapshot the UI thread renders without holding the lock.</summary>
    public Frame Snapshot(int scrollOffset)
    {
        lock (_lock)
        {
            var cols = Cols; var rows = Rows;
            var cells = new VtCell[cols * rows];
            var hist = _altActive ? 0 : _scrollback.Count;
            scrollOffset = Math.Max(0, Math.Min(scrollOffset, hist));
            var g = Cur;
            var startVirtual = hist - scrollOffset;   // index of the first visible line in the [history++grid] space
            for (var i = 0; i < rows; i++)
            {
                var v = startVirtual + i;
                for (var c = 0; c < cols; c++)
                {
                    if (v < hist) { var line = _scrollback[v]; cells[i * cols + c] = c < line.Length ? line[c] : Blank(-1); }
                    else { var gr = v - hist; cells[i * cols + c] = gr < rows ? g[gr * cols + c] : Blank(-1); }
                }
            }
            var showCursor = _cursorVisible && scrollOffset == 0;
            return new Frame(cells, cols, rows, _cx, _cy, showCursor, hist, Version);
        }
    }
}
