using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Serilog;

namespace BatchConvertToCHD.Services;

/// <summary>
///     Captures a screenshot of the application window and saves it as a PNG file in the
///     screenshots folder under <c>%LocalAppData%\BatchConvertToCHD\screenshots</c> (or the
///     platform equivalent).
/// </summary>
internal sealed class ScreenshotService
{
    private static readonly ILogger Logger = Log.ForContext<ScreenshotService>();

    /// <summary>
    ///     Captures the given window and saves it as a PNG. Returns the saved file path, or null
    ///     when the capture failed.
    /// </summary>
    /// <param name="window">The window to capture.</param>
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

            var screenshotDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppConfig.ApplicationName,
                "screenshots"
            );
            Directory.CreateDirectory(screenshotDir);

            var timestamp = DateTime.Now.ToString(
                "yyyy-MM-dd_HH-mm-ss-fff",
                CultureInfo.InvariantCulture
            );
            var filePath = Path.Combine(screenshotDir, $"screenshot_{timestamp}.png");

            bitmap.Save(filePath, PngBitmapEncoderOptions.Default);

            return filePath;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to take screenshot");
            return null;
        }
    }
}