using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Clip;

/// <summary>Watches the local Windows clipboard via the modern format-listener API (event-driven, not
/// polling) using a hidden message-only window. On every change it captures the current text or image and
/// raises <see cref="Captured"/> on the UI thread. To avoid an echo loop when a remote entry is mirrored
/// onto the local clipboard, the manager calls <see cref="Suppress"/> with that entry's content hash so the
/// resulting change is ignored once.</summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private HwndSource? _source;
    private DateTime _suppressUntil = DateTime.MinValue;   // ignore clipboard echoes of our own writes until this time
    private string? _lastHash;                             // dedupe identical consecutive copies

    /// <summary>Raised on the UI thread with a freshly captured local clipboard entry (origin unset).</summary>
    public event Action<ClipEntry>? Captured;
    public event Action<string>? Log;

    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;
    public bool Running { get; private set; }

    /// <summary>Begin listening. MUST be called on the UI (STA) thread so clipboard reads are valid.</summary>
    public void Start()
    {
        if (Running) return;
        _source = new HwndSource(new HwndSourceParameters("WinDeployClipMonitor")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = HWND_MESSAGE,   // message-only window: no UI, just receives WM_CLIPBOARDUPDATE
        });
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
        Running = true;
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { if (_source != null) RemoveClipboardFormatListener(_source.Handle); } catch { }
        try { _source?.RemoveHook(WndProc); } catch { }
        try { _source?.Dispose(); } catch { }
        _source = null;
    }

    /// <summary>Ignore clipboard changes for a short window — called right before we write a remote entry onto
    /// the local clipboard so the resulting OS clipboard-update isn't re-captured &amp; re-broadcast. A time
    /// window (not content matching) is used because a clipboard round-trip RE-ENCODES an image
    /// (PNG → DIB → PNG), so its bytes/hash change and content matching would miss the echo — duplicating the
    /// image under the wrong device's name.</summary>
    public void Suppress() => _suppressUntil = DateTime.Now.AddMilliseconds(700);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) OnClipboardUpdate();
        return IntPtr.Zero;
    }

    private void OnClipboardUpdate()
    {
        if (DateTime.Now <= _suppressUntil) return;   // echo of our own write (auto-apply) — ignore the whole window

        var entry = Capture();
        if (entry == null) return;

        var hash = entry.ContentHash();
        if (hash == _lastHash) return;   // same content copied again — don't duplicate
        _lastHash = hash;
        Captured?.Invoke(entry);
    }

    /// <summary>Read the current clipboard as a text or image entry; null if empty / unreadable / too large.
    /// Retries briefly because another app may momentarily hold the clipboard open.</summary>
    private ClipEntry? Capture()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (string.IsNullOrEmpty(text)) return null;
                    return new ClipEntry { Kind = ClipKind.Text, Text = text, CreatedAtUnix = ClipEntry.NowUnix() };
                }
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img == null) return null;
                    var png = EncodePng(NormalizeAlpha(img));
                    if (png.Length > MaxImageBytes)
                    {
                        Log?.Invoke(Localizer.Format("clip.log.imageTooBig", png.Length / 1024));
                        return null;
                    }
                    return new ClipEntry
                    {
                        Kind = ClipKind.Image, Image = png,
                        ImageW = img.PixelWidth, ImageH = img.PixelHeight, CreatedAtUnix = ClipEntry.NowUnix(),
                    };
                }
                return null;   // not text/image (files, etc.) — out of scope for v1
            }
            catch (COMException) { System.Threading.Thread.Sleep(30); }   // clipboard busy — retry
            catch { return null; }
        }
        return null;
    }

    /// <summary>Clipboard images (CF_DIB) frequently arrive with a zero/garbage alpha channel, so the shared
    /// PNG decodes fine but renders fully transparent (invisible over the UI). If EVERY pixel is transparent,
    /// force alpha to opaque so the picture actually shows; images that carry any real alpha are left as-is.</summary>
    private static BitmapSource NormalizeAlpha(BitmapSource src)
    {
        try
        {
            var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[h * stride];
            bgra.CopyPixels(px, stride, 0);
            for (var i = 3; i < px.Length; i += 4) if (px[i] != 0) return src;   // has real alpha — keep as-is
            for (var i = 3; i < px.Length; i += 4) px[i] = 255;                  // fully transparent → force opaque
            var fixedBmp = BitmapSource.Create(w, h, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null, px, stride);
            fixedBmp.Freeze();
            return fixedBmp;
        }
        catch { return src; }
    }

    private static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public void Dispose() => Stop();
}
