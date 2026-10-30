using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WorkFor.UI;

// Same native resource lookup used by the product; the test host has no other app dependencies.
internal static class WindowLayout
{
    public static TextBlock Label(string text, string style = "BodyTextBlockStyle") => new()
    {
        Text = text, Style = Application.Current.Resources[style] as Style, TextWrapping = TextWrapping.Wrap
    };
    public static Brush ResourceBrush(string name) => (Brush)Application.Current.Resources[name];
}
