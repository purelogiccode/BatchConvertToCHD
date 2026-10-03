using Serilog;

namespace BatchConvertToCHD.Services;

/// <summary>
///     Removes legacy files and folders left over from previous versions of the application.
///     Runs once at startup on a background thread to avoid blocking the UI.
/// </summary>
internal static class LegacyCleanupService
{
    private static readonly ILogger Logger = Log.ForContext(typeof(LegacyCleanupService));

    private static readonly string[] FoldersToDelete = ["logs", "Resources"];

    private static readonly string[] FilesToDelete = ["maxcso.exe", "psxpackager.exe"];

    /// <summary>
    ///     Runs the cleanup on a background task. Fire-and-forget; all errors are silently ignored.
    /// </summary>
    internal static void RunInBackground()
    {
        _ = Task.Run(static () => Cleanup(AppDomain.CurrentDomain.BaseDirectory));
    }

    /// <summary>
    ///     Removes the legacy folders and files from the given directory. Never throws; items
    ///     that are missing or in use are skipped.
    /// </summary>
    /// <param name="baseDirectory">The application base directory to clean up.</param>
    internal static void Cleanup(string baseDirectory)
    {
        foreach (var folder in FoldersToDelete)
        {
            try
            {
                var folderPath = Path.Combine(baseDirectory, folder);
                if (Directory.Exists(folderPath))
                {
                    Directory.Delete(folderPath, true);
                    Logger.Debug("Deleted legacy folder: {Folder}", folder);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not delete legacy folder: {Folder}", folder);
            }
        }

        foreach (var file in FilesToDelete)
        {
            try
            {
                var filePath = Path.Combine(baseDirectory, file);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Logger.Debug("Deleted legacy file: {File}", file);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not delete legacy file: {File}", file);
            }
        }
    }
}