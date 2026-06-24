using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Common;

/// <summary>A small modal single-choice dialog (themed), built in code. Returns the picked index.</summary>
public sealed class ChoiceDialog : Window
{
    private readonly ListBox _list;
    public int SelectedIndex => _list.SelectedIndex;

    public ChoiceDialog(string title, string prompt, IReadOnlyList<string> options, int recommended)
    {
        Title = title;
        Width = 580;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptTb = new TextBlock
        {
            Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Foreground = Brush("TextSecondary"), FontSize = 13,
        };
        Grid.SetRow(promptTb, 0);
        grid.Children.Add(promptTb);

        _list = new ListBox
        {
            MaxHeight = 320,
            Background = Brush("CardBg"),
            Foreground = Brush("TextPrimary"),
            BorderBrush = Brush("BorderStrong"),
        };
        foreach (var o in options) _list.Items.Add(new ListBoxItem { Content = o, Foreground = Brush("TextPrimary"), Padding = new Thickness(6, 4, 6, 4) });
        _list.SelectedIndex = recommended >= 0 && recommended < options.Count ? recommended : 0;
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedIndex >= 0) DialogResult = true; };
        Grid.SetRow(_list, 1);
        grid.Children.Add(_list);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = Localizer.T("dialog.choice.ok"), MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style okStyle) ok.Style = okStyle;
        if (Application.Current.TryFindResource("MiniButton") is Style cancelStyle) cancel.Style = cancelStyle;
        ok.Click += (_, _) => DialogResult = true;
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private static Brush Brush(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
