using Avalonia;

namespace BatchConvertToCHD;

/// <summary>
///     Entry point for the Avalonia (cross-platform) front end of Batch Convert to CHD.
/// </summary>
internal static class Program
{
    /// <summary>
    ///     Application entry point. Starts the classic desktop lifetime.
    /// </summary>
    /// <param name="args">Command-line arguments (an optional input folder path).</param>
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    ///     Creates and configures the <see cref="AppBuilder" /> used by the application and by the
    ///     Avalonia designer tooling.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
