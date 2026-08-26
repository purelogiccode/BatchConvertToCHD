using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Serilog;

namespace BatchConvertToCHD.Services;

/// <summary>
/// Captures a screenshot of the currently active (foreground) window
/// and saves it as a PNG file in the screenshots folder under
/// %LocalAppData%\BatchConvertToCHD\screenshots.
/// </summary>
internal class ScreenshotService
{
    private static readonly ILogger Logger = Log.ForContext<ScreenshotService>();

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int nWidth,
        int nHeight, IntPtr hdcSrc, int xSrc, int ySrc, uint dwRop);

    [DllImport("user32.dll")]
    private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    private const uint SrcCopy = 0x00CC0020;

    /// <summary>
    /// Captures a screenshot of the currently active foreground window
    /// and saves it as a PNG in the screenshots folder under
    /// %LocalAppData%\BatchConvertToCHD\screenshots.
    /// </summary>
    internal string? TakeScreenshot()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
                return null;

            if (!GetWindowRect(hWnd, out var rect))
                return null;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
                return null;

            var windowDc = GetWindowDC(hWnd);
            if (windowDc == IntPtr.Zero)
                return null;

            try
            {
                var compatibleDc = CreateCompatibleDC(windowDc);
                if (compatibleDc == IntPtr.Zero)
                    return null;

                try
                {
                    var bitmap = CreateCompatibleBitmap(windowDc, width, height);
                    if (bitmap == IntPtr.Zero)
                        return null;

                    try
                    {
                        var oldBitmap = SelectObject(compatibleDc, bitmap);

                        try
                        {
                            BitBlt(compatibleDc, 0, 0, width, height, windowDc, 0, 0, SrcCopy);

                            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                            var screenshotDir = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                AppConfig.ApplicationName,
                                "screenshots");
                            Directory.CreateDirectory(screenshotDir);

                            var timestamp =
                                DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
                            var filePath = Path.Combine(screenshotDir, $"screenshot_{timestamp}.png");

                            using var fileStream = new FileStream(filePath, FileMode.Create);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                            encoder.Save(fileStream);

                            return filePath;
                        }
                        finally
                        {
                            if (oldBitmap != IntPtr.Zero)
                                SelectObject(compatibleDc, oldBitmap);
                        }
                    }
                    finally
                    {
                        if (bitmap != IntPtr.Zero)
                            DeleteObject(bitmap);
                    }
                }
                finally
                {
                    if (compatibleDc != IntPtr.Zero)
                        DeleteDC(compatibleDc);
                }
            }
            finally
            {
                ReleaseDC(hWnd, windowDc);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to take screenshot");
            return null;
        }
    }
}