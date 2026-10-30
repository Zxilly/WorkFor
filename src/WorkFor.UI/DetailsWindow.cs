using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.Graphics;

namespace WorkFor.UI;

internal sealed class DetailsWindow : Window
{
    private readonly HostClient _client = new();
    private readonly TextBlock _title = WindowLayout.Label("今日收入", "SubtitleTextBlockStyle");
    private readonly TextBlock _amount = WindowLayout.Label("--", "TitleLargeTextBlockStyle");
    private readonly TextBlock _status = WindowLayout.Label("正在连接计算宿主", "CaptionTextBlockStyle");
    private readonly TextBlock _rate = WindowLayout.Label("--");
    private readonly TextBlock _elapsed = WindowLayout.Label("--");
    private readonly TextBlock _note = WindowLayout.Label("", "CaptionTextBlockStyle");
    private readonly InfoBar _connection = new() { IsClosable = false, Severity = InfoBarSeverity.Warning };
    private DisplaySnapshot? _last;

    public DetailsWindow(RectInt32? anchor = null)
    {
        Title = "WorkFor · 收入详情";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        var body = new StackPanel { Padding = new Thickness(24), Spacing = 12 };
        _status.Foreground = WindowLayout.ResourceBrush("TextFillColorSecondaryBrush");
        _note.Foreground = WindowLayout.ResourceBrush("TextFillColorSecondaryBrush");
        body.Children.Add(_title);
        body.Children.Add(_amount);
        body.Children.Add(_status);
        body.Children.Add(WindowLayout.Divider());
        body.Children.Add(DetailRow(Symbol.Clock, "当前时薪", _rate));
        body.Children.Add(DetailRow(Symbol.Calendar, "计薪时长", _elapsed));
        body.Children.Add(_note);
        body.Children.Add(WindowLayout.Divider());
        var settings = new Button { HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 12, 8, 12) };
        var settingsGrid = new Grid { ColumnSpacing = 16 };
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        settingsGrid.Children.Add(new SymbolIcon(Symbol.Setting));
        var settingsLabel = WindowLayout.Label("计算规则");
        Grid.SetColumn(settingsLabel, 1);
        settingsGrid.Children.Add(settingsLabel);
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 12 };
        Grid.SetColumn(chevron, 2);
        settingsGrid.Children.Add(chevron);
        settings.Content = settingsGrid;
        settings.Click += (_, _) => WindowLayout.OpenSettings();
        body.Children.Add(settings);
        body.Children.Add(_connection);
        Content = body;
        body.KeyDown += OnKeyDown;
        WindowLayout.Configure(this, 380, 420, compact: true, anchor);
        _client.SnapshotReceived += snapshot => DispatcherQueue.TryEnqueue(() => ShowSnapshot(snapshot));
        _client.ConnectionChanged += state => DispatcherQueue.TryEnqueue(() => ShowConnection(state));
        Closed += async (_, _) => await _client.DisposeAsync();
    }

    internal void MoveToAnchor(RectInt32? anchor) => WindowLayout.Configure(this, 380, 420, compact: true, anchor);

    private static Grid DetailRow(Symbol icon, string label, TextBlock value)
    {
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new SymbolIcon(icon) { Width = 20, Height = 20 });
        var title = WindowLayout.Label(label);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }

    private void ShowSnapshot(DisplaySnapshot snapshot)
    {
        _last = snapshot;
        _title.Text = snapshot.Title;
        _amount.Text = snapshot.AmountText;
        AutomationProperties.SetName(_amount, $"{snapshot.Title}{snapshot.AmountText}");
        _status.Text = snapshot.Status == "fresh"
            ? "实时更新 · 每秒刷新" : snapshot.StatusText;
        _rate.Text = string.IsNullOrEmpty(snapshot.RateText) ? "未提供" : snapshot.RateText;
        _elapsed.Text = snapshot.ScheduledElapsedText;
        _note.Text = snapshot.Note;
        _note.Visibility = string.IsNullOrWhiteSpace(snapshot.Note) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowConnection(string state)
    {
        _connection.IsOpen = state.Length > 0;
        _connection.Title = "连接状态";
        _connection.Message = state;
        if (state.Length > 0) _status.Text = _last is null ? "等待首次计算" : "连接中 · 显示上次结果";
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        Close();
    }
}
