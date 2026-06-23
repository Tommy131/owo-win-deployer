using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinDeploy.App.Behaviors;

/// <summary>Attached behavior that restricts what a <see cref="TextBox"/> accepts (typing, paste, and drop),
/// so numeric / address fields can never hold invalid text. Apply in XAML with
/// <c>b:InputFilter.Mode="digits"</c> or in code via <see cref="SetMode"/>.
/// Modes: <c>digits</c> (0-9), <c>decimal</c> (digits + one dot), <c>ipv4</c> (incremental 0-255 octets),
/// <c>host</c> (letters/digits/dot/hyphen — hostnames or IPs), <c>portlist</c> (digits + separators).</summary>
public static class InputFilter
{
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached("Mode", typeof(string), typeof(InputFilter),
            new PropertyMetadata(null, OnModeChanged));

    public static void SetMode(DependencyObject o, string value) => o.SetValue(ModeProperty, value);
    public static string? GetMode(DependencyObject o) => (string?)o.GetValue(ModeProperty);

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        tb.PreviewTextInput -= OnPreviewTextInput;
        tb.PreviewKeyDown -= OnPreviewKeyDown;
        DataObject.RemovePastingHandler(tb, OnPaste);
        if (!string.IsNullOrEmpty(e.NewValue as string))
        {
            tb.PreviewTextInput += OnPreviewTextInput;
            tb.PreviewKeyDown += OnPreviewKeyDown;
            DataObject.AddPastingHandler(tb, OnPaste);
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var tb = (TextBox)sender;
        if (!IsValid(GetMode(tb), Proposed(tb, e.Text))) e.Handled = true;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The space bar doesn't always surface through PreviewTextInput; block it for every restricted field.
        if (e.Key == Key.Space) e.Handled = true;
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var tb = (TextBox)sender;
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) { e.CancelCommand(); return; }
        var text = (string)e.DataObject.GetData(DataFormats.UnicodeText)!;
        if (!IsValid(GetMode(tb), Proposed(tb, text))) e.CancelCommand();
    }

    /// <summary>The text the box would hold if <paramref name="insert"/> replaced the current selection.</summary>
    private static string Proposed(TextBox tb, string insert)
    {
        var t = tb.Text ?? "";
        var start = tb.SelectionStart;
        var len = tb.SelectionLength;
        if (start > t.Length) start = t.Length;
        if (start + len > t.Length) len = t.Length - start;
        return t.Substring(0, start) + insert + t.Substring(start + len);
    }

    /// <summary>Partial validation — accepts in-progress text so the user can keep typing toward a valid value.</summary>
    private static bool IsValid(string? mode, string s)
    {
        if (string.IsNullOrEmpty(s)) return true;   // allow clearing the field
        return mode switch
        {
            "digits" => s.All(char.IsDigit),
            "decimal" => Regex.IsMatch(s, @"^\d*\.?\d*$"),
            "ipv4" => IsPartialIpv4(s),
            "host" => Regex.IsMatch(s, @"^[A-Za-z0-9.\-]*$"),
            "portlist" => Regex.IsMatch(s, @"^[0-9,，;； ]*$"),
            _ => true,
        };
    }

    private static bool IsPartialIpv4(string s)
    {
        if (!Regex.IsMatch(s, @"^[0-9.]*$")) return false;
        var parts = s.Split('.');
        if (parts.Length > 4) return false;
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;            // trailing dot / mid-typing
            if (p.Length > 3) return false;
            if (!int.TryParse(p, out var n) || n > 255) return false;
        }
        return true;
    }
}
