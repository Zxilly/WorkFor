using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace WorkFor.UI;

internal static class WindowLayout
{
    public static void Configure(Window window, int width, int height, bool compact, RectInt32? anchor = null)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpiScale = GetDpiForWindow(hwnd) / 96d;
        var appWindow = window.AppWindow;
        appWindow.Resize(new SizeInt32((int)(width * dpiScale), (int)(height * dpiScale)));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = !compact;
            presenter.IsMaximizable = !compact;
            presenter.IsMinimizable = !compact;
            if (compact)
            {
                presenter.SetBorderAndTitleBar(true, false);
                appWindow.IsShownInSwitchers = false;
            }
        }

        if (!compact) return;
        var display = anchor is { } rectangle
            ? DisplayArea.GetFromRect(rectangle, DisplayAreaFallback.Primary) : DisplayArea.Primary;
        var size = appWindow.Size;
        var gap = (int)(8 * dpiScale);
        var right = display.WorkArea.X + display.WorkArea.Width;
        var bottom = display.WorkArea.Y + display.WorkArea.Height;
        var x = anchor is { } position ? position.X + position.Width - size.Width : right - size.Width - gap;
        var y = anchor is { } source ? source.Y - size.Height - gap : bottom - size.Height - gap;
        // A top-docked or small display still gets a visible panel inside its work area.
        appWindow.Move(new PointInt32(
            Math.Clamp(x, display.WorkArea.X, Math.Max(display.WorkArea.X, right - size.Width)),
            Math.Clamp(y, display.WorkArea.Y, Math.Max(display.WorkArea.Y, bottom - size.Height))));
    }

    public static RectInt32? ParseAnchor(string[] args)
    {
        var index = Array.IndexOf(args, "--anchor-rect");
        if (index < 0 || index + 1 >= args.Length) return null;
        var values = args[index + 1].Split(',');
        if (values.Length != 4) return null;
        var numbers = new int[4];
        for (var i = 0; i < numbers.Length; i++)
            if (!int.TryParse(values[i], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out numbers[i])) return null;
        if (numbers[0] is < -100000 or > 100000 || numbers[1] is < -100000 or > 100000
            || numbers[2] is < 1 or > 10000 || numbers[3] is < 1 or > 10000) return null;
        return new RectInt32(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    public static TextBlock Label(string text, string style = "BodyTextBlockStyle") => new()
    {
        Text = text,
        Style = Application.Current.Resources[style] as Style,
        TextWrapping = TextWrapping.Wrap
    };

    public static Brush ResourceBrush(string name) =>
        (Brush)Application.Current.Resources[name];

    public static Border Divider() => new()
    {
        Height = 1,
        Background = ResourceBrush("DividerStrokeColorDefaultBrush"),
        Margin = new Thickness(0, 8, 0, 8)
    };

    public static void OpenSettings()
    {
        App.Show(new SettingsWindow());
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
