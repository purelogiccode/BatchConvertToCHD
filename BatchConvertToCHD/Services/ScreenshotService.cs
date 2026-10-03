using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Serilog;

namespace BatchConvertToCHD.Services;

/// <summary>
///     Captures a screenshot of the application window and saves it as a PNG file. The
///     <c>screenshots</c> folder inside <c>%LocalAppData%\BatchConvertToCHD</c> (or the platform
///     equivalent) is tried first, beside the application's <c>logs</c> folder; when that folder
///     cannot be written, the service falls back to a <c>screenshots</c> folder next to the
///     application executable.
/// </summary>
internal static class ScreenshotService
{
    /// <summary>The name of the folder screenshots are saved into.</summary>
    internal const string FolderName = "screenshots";

    private static readonly ILogger Logger = Log.ForContext(typeof(ScreenshotService));

    /// <summary>
    ///     Returns the preferred screenshot directory: <c>%LocalAppData%\BatchConvertToCHD\screenshots</c>
    ///     (or the platform equivalent).
    /// </summary>
    /// <returns>The preferred directory path.</returns>
    internal static string GetPreferredDirectory()
    {
        return GetPreferredDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        );
    }

    /// <summary>
    ///     Returns the preferred screenshot directory for a given application-data root. Exposed so
    ///     tests can supply a temporary directory.
    /// </summary>
    /// <param name="applicationDataRoot">The application-data root folder.</param>
    /// <returns>The preferred directory path.</returns>
    internal static string GetPreferredDirectory(string applicationDataRoot)
    {
        return Path.Combine(applicationDataRoot, AppConfig.ApplicationName, FolderName);
    }

    /// <summary>
    ///     Returns the fallback screenshot directory used when the preferred folder cannot be
    ///     written: a <c>screenshots</c> folder next to the application executable.
    /// </summary>
    /// <param name="applicationBaseDirectory">The application's base directory.</param>
    /// <returns>The fallback directory path.</returns>
    internal static string GetFallbackDirectory(string applicationBaseDirectory)
    {
        return Path.Combine(applicationBaseDirectory, FolderName);
    }

    /// <summary>
    ///     Builds a unique, timestamped screenshot file name.
    /// </summary>
    /// <param name="timestamp">The capture time.</param>
    /// <returns>The file name, including the <c>.png</c> extension.</returns>
    internal static string BuildFileName(DateTime timestamp)
    {
        return $"screenshot_{timestamp.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture)}.png";
    }

    /// <summary>
    ///     Saves a screenshot into the preferred directory and falls back to the application-data
    ///     directory when the preferred save fails. The target folder is created when missing.
    /// </summary>
    /// <param name="saveToFile">Delegate that writes the image to the supplied path.</param>
    /// <param name="preferredDirectory">Directory tried first.</param>
    /// <param name="fallbackDirectory">Directory tried when the preferred one fails.</param>
    /// <returns>The saved file path, or <see langword="null" /> when both attempts failed.</returns>
    internal static string? SaveScreenshot(
        Action<string> saveToFile,
        string preferredDirectory,
        string fallbackDirectory
    )
    {
        var savedPath = TrySave(saveToFile, preferredDirectory);
        if (savedPath != null) return savedPath;

        Logger.Debug(
            "Screenshot folder {Directory} is not writable; using fallback {Fallback}",
            preferredDirectory,
            fallbackDirectory
        );

        savedPath = TrySave(saveToFile, fallbackDirectory);
        if (savedPath == null)
        {
            Logger.Error(
                "Failed to save screenshot to both {Directory} and {Fallback}",
                preferredDirectory,
                fallbackDirectory
            );
        }

        return savedPath;
    }

    /// <summary>
    ///     Captures the given window and saves it as a PNG.
    /// </summary>
    /// <param name="window">The window to capture.</param>
    /// <returns>The saved file path, or <see langword="null" /> when the capture failed.</returns>
    internal static string? TakeScreenshot(Window window)
    {
        try
        {
            var size = window.Bounds.Size;
            var scaling = window.RenderScaling;
            var pixelSize = new PixelSize(
                Math.Max(1, (int)Math.Round(size.Width * scaling)),
                Math.Max(1, (int)Math.Round(size.Height * scaling))
            );

            using var bitmap = new RenderTargetBitmap(
                pixelSize,
                new Vector(96 * scaling, 96 * scaling)
            );
            bitmap.Render(window);

            return SaveScreenshot(
                filePath => bitmap.Save(filePath, PngBitmapEncoderOptions.Default),
                GetPreferredDirectory(),
                GetFallbackDirectory(AppDomain.CurrentDomain.BaseDirectory)
            );
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to take screenshot");
            return null;
        }
    }

    /// <summary>
    ///     Captures the given window and logs the outcome through Serilog. Used by windows that
    ///     have no terminal log of their own.
    /// </summary>
    /// <param name="window">The window to capture.</param>
    /// <returns>The saved file path, or <see langword="null" /> when the capture failed.</returns>
    internal static string? CaptureAndLog(Window window)
    {
        var filePath = TakeScreenshot(window);
        if (filePath != null)
        {
            Logger.Information("Screenshot saved: {Path}", filePath);
        }
        else
        {
            Logger.Warning("Screenshot failed: could not capture the window.");
        }

        return filePath;
    }

    /// <summary>
    ///     Creates the directory when missing and writes one timestamped screenshot into it.
    /// </summary>
    /// <param name="saveToFile">Delegate that writes the image to the supplied path.</param>
    /// <param name="directory">The directory to save into.</param>
    /// <returns>The saved file path, or <see langword="null" /> when the save failed.</returns>
    private static string? TrySave(Action<string> saveToFile, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var filePath = Path.Combine(directory, BuildFileName(DateTime.Now));
            saveToFile(filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to save screenshot to {Directory}", directory);
            return null;
        }
    }
}