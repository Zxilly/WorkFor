namespace WorkFor.UI;

// This probe does not invoke the settings button or open product settings.
internal static class App
{
    internal static void OpenSettings() => throw new InvalidOperationException("Settings activation is outside this probe.");
}
