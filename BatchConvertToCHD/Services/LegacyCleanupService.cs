using System.IO;
using Serilog;

namespace BatchConvertToCHD.Services;

/// <summary>
///     Removes legacy files and folders left over from previous versions of the application.
///     Runs once at startup on a background thread to avoid blocking the UI.
/// </summary>
internal static class LegacyCleanupService
{
    private static readonly ILogger Logger = Log.ForContext(typeof(LegacyCleanupService));

    private static readonly string[] FoldersToDelete = ["logs", "Resources", "Screenshot"];

    private static readonly string[] FilesToDelete = ["maxcso.exe", "psxpackager.exe"];

    /// <summary>
    ///     Runs the cleanup on a background task. Fire-and-forget; all errors are silently ignored.
    /// </summary>
    internal static void RunInBackground()
    {
        _ = Task.Run(static () =>
        {
            try
            {
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

                foreach (var folder in FoldersToDelete)
                    try
                    {
                        var folderPath = Path.Combine(baseDirectory, folder);
                        if (Directory.Exists(folderPath))
                        {
                            Directory.Delete(folderPath, true);
                            Logger.Debug("Deleted legacy folder: {Folder}", folder);
                        }
                    }
                    catch
                    {
                        /* ignore - file may be in use */
                    }

                foreach (var file in FilesToDelete)
                    try
                    {
                        var filePath = Path.Combine(baseDirectory, file);
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            Logger.Debug("Deleted legacy file: {File}", file);
                        }
                    }
                    catch
                    {
                        /* ignore - file may be in use */
                    }
            }
            catch
            {
                /* ignore */
            }
        });
    }
}