using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BatchConvertToCHD.Diagnostics;
using BatchConvertToCHD.Dialogs;
using BatchConvertToCHD.Interfaces;
using BatchConvertToCHD.Models;
using BatchConvertToCHD.Services;
using BatchConvertToCHD.Utilities;
using BatchConvertToCHD.Utilities.Ecm;
using CCDSharp;
using CCDSharp.Models;
using CHDSharp;
using CHDSharp.Models;
using MDSSharp;
using PBPSharp;
using PBPSharp.Models;
using Serilog;
using ISZSharp;

namespace BatchConvertToCHD;

/// <summary>
///     Main application window for BatchConvertToCHD.
///     Provides functionality for converting, verifying, and extracting CHD files.
/// </summary>
internal partial class MainWindow : Window, IDisposable
{
    // Temp Directory Prefix
    private const string TempDirPrefix = "BatchConvertToCHD_Temp_";

    // Extension used while a CHD is still being written. chdman ignores the output extension, and
    // keeping it off ".chd" means a leftover staging file is never mistaken for a finished CHD by
    // the verification and extraction tabs.
    private const string StagingExtension = ".chdtmp";

    // Maximum characters kept in the on-screen log before the oldest half is dropped, and the
    // most lines buffered while the UI thread is busy (a longer burst drops the oldest lines).
    private const int MaxLogLength = 50000;
    private const int MaxPendingLogLines = 2000;

    /// <summary>
    ///     Free space below this on the output drive means no conversion can succeed.
    /// </summary>
    private const long MinimumOutputFreeBytes = 64L * 1024 * 1024;

    /// <summary>
    ///     A CHD below this fraction of its source is rare for game data, so less free space than this
    ///     is treated as certain failure rather than something to discover an hour in.
    /// </summary>
    private const double MinimumOutputSizeRatio = 0.10;

    /// <summary>How a .isz file is referred to when its content turns out not to be one.</summary>
    private const string IszContainerDescription = "a compressed ISZ image";

    private const int MaxFileOperationRetries = 5;

    // MP3 audio track decoder (Media Foundation) for cue sheets with MP3 tracks.
    private static readonly IMp3Decoder Mp3Decoder = new Mp3ToWavDecoder();
    private readonly ArchiveService _archiveService;
    private readonly string _chdmanExePath;
    private readonly string _chdmanResolvedName;

    // File collections for DataGrids
    private readonly ObservableCollection<FileItem> _conversionFiles = new();
    private readonly Lock _ctsLock = new();
    private readonly ObservableCollection<FileItem> _extractionFiles = new();
    private readonly FileWatcherService _fileWatcher = new();
    private readonly bool _isChdmanAvailable;
    private readonly Stopwatch _operationTimer = new();
    private readonly DispatcherTimer _elapsedTimeTimer;
    private readonly DispatcherTimer _logFlushTimer;
    private readonly Lock _logQueueLock = new();
    private readonly Lock _performanceCounterLock = new();
    private readonly Queue<string> _pendingLogLines = new();
    private readonly string _sevenZipExePath;

    // Services
    private readonly UpdateService _updateService;
    private readonly ObservableCollection<FileItem> _verificationFiles = new();
    private CancellationTokenSource _cts;
    private volatile int _failedCount;
    private readonly bool _uiInitialized;

    // Operation state tracking (0 = idle, >0 = running) - using Interlocked for thread safety
    private int _operationRunningState;

    // CHD paths already produced by the running batch, so a second input resolving to the same
    // output cannot silently replace the first product.
    private readonly Lock _batchOutputPathsLock = new();
    private readonly HashSet<string> _batchOutputPaths = new(StringComparer.OrdinalIgnoreCase);

    // Tracks whether a close was requested while an operation was running
    private bool _pendingClose;

    // Name of the operation currently running (Conversion, Verification or Extraction)
    private string _activeOperation = string.Empty;
    private volatile int _processedOkCount;
    private IoThroughputCounter? _readBytesCounter;

    // Statistics
    private volatile int _totalFilesProcessed;
    private bool _wasCancelled;
    private IoThroughputCounter? _writeBytesCounter;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MainWindow" /> class.
    ///     Sets up services, checks for required executables, and initializes the UI.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();

        // Ticks once a second while an operation runs so the elapsed-time stat card keeps
        // counting during long single-file conversions (e.g. a CHDSharp run), which produce
        // no per-file UI updates for the batch loops to refresh from.
        _elapsedTimeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimeTimer.Tick += (_, _) => UpdateProcessingTimeDisplay();

        // Batches bursty log output (chdman prints per file) into one UI update per tick instead
        // of one dispatcher call per line, and the flush caps the on-screen text length, so a large
        // log cannot freeze the window.
        _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _logFlushTimer.Tick += (_, _) => FlushPendingLogLines();
        _logFlushTimer.Start();

        ConversionFilesDataGrid.ItemsSource = _conversionFiles;
        VerificationFilesDataGrid.ItemsSource = _verificationFiles;
        ExtractionFilesDataGrid.ItemsSource = _extractionFiles;

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;

        // Resolve the bundled tools once, in preference order. On an ARM64 machine both builds
        // execute (natively or emulated), so a missing preferred binary falls back to the other
        // architecture's file instead of failing outright; on pure x64 there is nothing to fall
        // back to and the missing-dependency messaging stays accurate.
        (_chdmanExePath, _chdmanResolvedName, _isChdmanAvailable) = ResolveToolExecutable(
            appDirectory,
            GetChdmanCandidates()
        );
        (_sevenZipExePath, _, var isSevenZipAvailable) = ResolveToolExecutable(
            appDirectory,
            GetSevenZipCandidates()
        );

        // Initialize Services
        _updateService = new UpdateService(AppConfig.ApplicationName)
        {
            ShowUpdatePromptAsync = ShowUpdatePromptAsync
        };
        _archiveService = new ArchiveService(_sevenZipExePath, isSevenZipAvailable);

        // F8 screenshot hotkey (window-scoped on every platform)
        KeyDown += MainWindow_KeyDown;

        InitializeStatusBar();
        _ = Task.Run(
            static async () =>
            {
                try
                {
                    await Task.Delay(2000);
                    CleanupLeftoverTempDirectories();
                    LegacyCleanupService.RunInBackground();
                }
                catch
                {
                    /* ignore */
                }
            },
            _cts.Token
        );
        DisplayConversionInstructionsInLog();
        ResetOperationStats();
        LogEnvironmentDetails();
        InitializeExplorerTab();

        // Defer heavy initialization until after window is shown
        Opened += MainWindow_OpenedAsync;

        // Hide speed display initially until we know counters are available
        SpeedStatCard.IsVisible = false;

        // From here on the event handlers can safely touch every named control.
        _uiInitialized = true;
    }

    /// <summary>
    ///     Releases all resources used by the <see cref="MainWindow" />.
    ///     Cancels ongoing operations and disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        lock (_ctsLock)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        _writeBytesCounter?.Dispose();
        _readBytesCounter?.Dispose();
        _fileWatcher.Dispose();
        _operationTimer.Stop();
        _elapsedTimeTimer.Stop();
        _logFlushTimer.Stop();

        KillOrphanedProcesses();
    }

    /// <summary>
    ///     Runs deferred startup work once the window is shown: performance counters, a command-line
    ///     input folder, the dependency notice and the update check.
    /// </summary>
    private async void MainWindow_OpenedAsync(object? sender, EventArgs e)
    {
        try
        {
            // Initialize performance counters off the UI thread (WMI queries are slow)
            await Task.Run(
                () =>
                {
                    _writeBytesCounter = CreateWritePerformanceCounter();
                    _readBytesCounter = CreateReadPerformanceCounter();
                },
                _cts.Token
            );

            // Apply command-line argument for input folder path if provided
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                var inputPath = args[1];
                SetInputFolder(inputPath);
            }

            // Show speed display if counters are available
            if (_writeBytesCounter != null || _readBytesCounter != null) SpeedStatCard.IsVisible = true;

            // Check for missing dependencies and notify user
            CheckDependenciesAndNotifyUser();

            // Defer update check until window is responsive
            await Task.Delay(100, _cts.Token); // Allow UI to render first
            SafeFireAndForget(
                _updateService.CheckForNewVersionAsync(
                    LogMessage,
                    UpdateStatusBarMessage,
                    ReportBugAsync
                )
            );
        }
        catch (OperationCanceledException)
        {
            // The window was closed while startup work was still pending (OnClosed cancels
            // _cts), so cancellation here is the normal shutdown path, not an error.
            Log.Debug("MainWindow_LoadedAsync cancelled - window closed during startup");
        }
        catch (Exception ex)
        {
            LogError("MainWindow_Loaded error", ex);
        }
    }

    /// <summary>Handles the F8 hotkey by saving a screenshot of the window.</summary>
    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F8) return;

        try
        {
            var filePath = ScreenshotService.TakeScreenshot(this);
            if (filePath != null)
            {
                LogMessage($"Screenshot saved: {filePath}");
                UpdateStatusBarMessage("Screenshot captured");
            }
            else
            {
                LogMessage("Screenshot failed: could not capture the application window.");
                UpdateStatusBarMessage("Screenshot failed");
            }
        }
        catch (Exception ex)
        {
            LogError($"Screenshot error: {ex.Message}", ex);
        }

        e.Handled = true;
    }

    /// <summary>Starts a window move drag when the title bar is pressed with the left mouse button.</summary>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>Toggles the maximized state when the title bar is double-clicked.</summary>
    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
    {
        ToggleMaximize();
    }

    /// <summary>Minimizes the window.</summary>
    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>Toggles the maximized state of the window.</summary>
    private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    /// <summary>Closes the window.</summary>
    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>Switches the window between the maximized and normal states.</summary>
    private void ToggleMaximize()
    {
        WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>
    ///     Returns the first candidate name that exists in <paramref name="baseDirectory" />, together
    ///     with its name and availability. The candidate list is ordered best-first (the OS-native
    ///     build on ARM64 machines), so a partial or mixed deployment still finds an executable the
    ///     machine can run. When nothing exists, the preferred name is returned so missing-dependency
    ///     messages point at the file that should be there.
    /// </summary>
    private static (string Path, string Name, bool Available) ResolveToolExecutable(
        string baseDirectory,
        IReadOnlyList<string> candidateNames
    )
    {
        foreach (var name in candidateNames)
        {
            var path = Path.Combine(baseDirectory, name);
            if (File.Exists(path)) return (path, name, true);
        }

        // On Linux and macOS the tools are normally installed system-wide (MAME's chdman,
        // p7zip, ...) rather than next to the app, so fall back to PATH.
        foreach (var name in candidateNames)
        {
            var path = FindOnPath(name);
            if (path != null) return (path, name, true);
        }

        var preferred = candidateNames[0];
        return (Path.Combine(baseDirectory, preferred), preferred, false);
    }

    /// <summary>
    ///     Returns the chdman executable names to probe, best first, for the current platform.
    /// </summary>
    private static IReadOnlyList<string> GetChdmanCandidates()
    {
        return OperatingSystem.IsWindows() ? AppConfig.ChdmanExeCandidates : ["chdman"];
    }

    /// <summary>
    ///     Returns the 7-Zip executable names to probe, best first, for the current platform.
    /// </summary>
    private static IReadOnlyList<string> GetSevenZipCandidates()
    {
        return OperatingSystem.IsWindows()
            ? AppConfig.SevenZipExeCandidates
            : ["7z", "7za", "7zz"];
    }

    /// <summary>
    ///     Returns the full path of the first executable named <paramref name="executableName" />
    ///     found in the <c>PATH</c> environment variable, or null when it is not present.
    /// </summary>
    /// <param name="executableName">The executable file name to locate.</param>
    private static string? FindOnPath(string executableName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable)) return null;

        foreach (
            var directory in pathVariable.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, executableName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate)) return candidate;

            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
                return candidate + ".exe";
        }

        return null;
    }

    /// <summary>
    ///     Warns the user on Windows when chdman was not found, since conversions then use the
    ///     built-in CHDSharp encoder.
    /// </summary>
    private void CheckDependenciesAndNotifyUser()
    {
        // The built-in CHDSharp encoder always exists, so conversion can never hard-fail on a
        // missing encoder. chdman is the primary encoder on Windows and is bundled with the app;
        // on Linux and macOS the built-in encoder is the expected path, and a system-installed
        // chdman on PATH is only a bonus, so its absence is not worth a warning there.
        if (!_isChdmanAvailable && OperatingSystem.IsWindows())
        {
            const string msg =
                "chdman.exe was not found, so conversions will run on the built-in CHDSharp encoder.";

            LogWarning(" " + msg);
            _ = ShowMessageBoxAsync(msg, "Encoder Notice", MessageBoxButton.Ok, MessageBoxImage.Warning);
        }
    }

    /// <summary>Creates the write-throughput performance counter.</summary>
    /// <returns>The counter, or null when it is unavailable.</returns>
    private static IoThroughputCounter? CreateWritePerformanceCounter()
    {
        try
        {
            return IoThroughputCounter.CreateForWrites();
        }
        catch
        {
            // Best effort - return null if creation fails
            return null;
        }
    }

    /// <summary>Creates the read-throughput performance counter.</summary>
    /// <returns>The counter, or null when it is unavailable.</returns>
    private static IoThroughputCounter? CreateReadPerformanceCounter()
    {
        try
        {
            return IoThroughputCounter.CreateForReads();
        }
        catch
        {
            // Best effort - return null if creation fails
            return null;
        }
    }

    /// <summary>
    ///     Initializes the status bar labels, colors and initial message from the resolved tool
    ///     availability.
    /// </summary>
    private void InitializeStatusBar()
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                StatusBarChdSharp.Text = " CHDSharp ";
                StatusBarChdSharp.Foreground = FindBrush("SuccessTextBrush");
                StatusBarChdman.Text = " CHDMAN ";
                StatusBarChdman.Foreground = _isChdmanAvailable
                    ? FindBrush("SuccessTextBrush")
                    : OperatingSystem.IsWindows()
                        ? FindBrush("FailedTextBrush")
                        : Brushes.Gray;
                StatusBarMessage.Text = "Ready";
                SpeedValue.Text = "0.0 MB/s";
            }
            catch (Exception ex)
            {
                LogError("StatusBar Initialization Error", ex);
            }
        });
    }

    /// <summary>
    ///     Looks up a brush resource from the application resources, falling back to gray.
    /// </summary>
    /// <param name="resourceKey">The resource key to look up.</param>
    private static IBrush FindBrush(string resourceKey)
    {
        if (
            Application.Current is { } app
            && app.TryFindResource(resourceKey, out var resource)
            && resource is IBrush brush
        )
        {
            return brush;
        }

        return Brushes.Gray;
    }

    /// <summary>Deletes leftover temp directories from previous runs in the background.</summary>
    private static void CleanupLeftoverTempDirectories()
    {
        _ = Task.Run(static () =>
        {
            try
            {
                foreach (var basePath in PathUtils.GetPossibleTempBasePaths())
                {
                    try
                    {
                        var directories = Directory.GetDirectories(basePath, $"{TempDirPrefix}*");
                        foreach (var dir in directories)
                        {
                            try
                            {
                                Directory.Delete(dir, true);
                            }
                            catch
                            {
                                /* ignore */
                            }
                        }
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
            catch
            {
                /* ignore */
            }
        });
    }

    /// <summary>Updates the status bar message on the UI thread.</summary>
    /// <param name="message">The message to show.</param>
    private void UpdateStatusBarMessage(string message)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() => StatusBarMessage.Text = message);
    }

    /// <summary>
    ///     Checks that an executable exists and can be opened for reading, reporting a user-facing
    ///     error when it cannot.
    /// </summary>
    /// <param name="exePath">Full path of the executable.</param>
    /// <param name="exeName">Name used in log and error messages.</param>
    /// <returns>True when the executable is accessible.</returns>
    private async Task<bool> ValidateExecutableAccessAsync(string exePath, string exeName)
    {
        try
        {
            if (!File.Exists(exePath))
            {
                LogError($" {exeName} not found at: {exePath}");
                ShowError($"{exeName} not found.");
                return false;
            }

            // Check if file has executable extension (Windows only; Unix tools have no extension)
            if (
                OperatingSystem.IsWindows()
                && !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            )
            {
                LogError($" {exeName} is not an executable file.");
                ShowError($"{exeName} is not a valid executable.");
                return false;
            }

            // Check for read access. The sharing level mirrors how Windows itself opens executable
            // images (read + delete), so a chdman.exe currently running under another instance of
            // this app, or briefly held open by an antivirus scan, does not produce a false
            // "locked by another process" abort; only a file that cannot be opened at all fails.
            try
            {
                await using (
                    File.Open(
                        exePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete
                    )
                )
                {
                    // File is readable and can be executed
                }
            }
            catch (IOException ioEx)
                when (ioEx.Message.Contains(
                          "being used by another process",
                          StringComparison.OrdinalIgnoreCase
                      )
                     )
            {
                LogError(
                    $" {exeName} cannot be opened - it is held with incompatible access by another process."
                );
                LogMessage(
                    "       Close other instances of this application and any antivirus scan in progress, then try again."
                );
                ShowError($"{exeName} is currently in use by another process.");
                return false;
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            LogError($" Cannot access {exeName}. Insufficient permissions.");
            ShowError($"Access denied to {exeName}. Check antivirus or permissions.");
            return false;
        }
        catch (Exception ex)
        {
            LogError($"Cannot access {exeName}. {ex.Message}", ex);
            ShowError(
                $"Cannot access {exeName}. Check permissions and ensure the file is not in use."
            );
            return false;
        }
    }

    /// <summary>
    ///     Validates that chdman.exe is compatible with the current OS platform.
    ///     This catches Win32Exception (0x800700C1) when the executable is not valid for this OS.
    /// </summary>
    private async Task<bool> ValidateChdmanCompatibilityAsync(
        string chdmanPath,
        CancellationToken token
    )
    {
        using var process = new Process();
        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = chdmanPath,
                Arguments = "help",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ErrorDialog = false
            };

            process.Start();
            await process.WaitForExitAsync(token);

            // A negative exit code means Windows terminated chdman abnormally (e.g. 0xC000001D
            // illegal instruction on a CPU without the SIMD extensions this build was compiled
            // with). The exe launches fine but will crash on every conversion, so stop before the
            // batch starts instead of failing each file with "produced no error output".
            if (process.ExitCode < 0)
            {
                LogError(
                    $" chdman.exe terminated abnormally during the startup check (exit code {process.ExitCode}{DescribeChdmanCrash(process.ExitCode)})."
                );
                LogWarning(
                    "       The bundled chdman.exe is likely incompatible with this computer's CPU or Windows version, or was damaged/quarantined by antivirus software."
                );
                LogMessage(
                    "       Replace chdman.exe with a build that matches your CPU and Windows version (e.g. an official MAME tools release) and add an antivirus exclusion for it."
                );
                ShowError(
                    $"chdman.exe crashed during the startup check (exit code {process.ExitCode}).\n\n"
                    + "The bundled build may be incompatible with this computer's CPU, or it was damaged/quarantined by antivirus software.\n"
                    + "Replace chdman.exe with a build that matches your CPU and check your antivirus settings."
                );
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                    await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
                }
                catch
                {
                    // Best effort - ignore errors during cleanup
                }
            }

            throw;
        }
        catch (Win32Exception ex)
            when (ex.NativeErrorCode == 193
                  || ex.Message.Contains("not a valid application", StringComparison.Ordinal)
                 )
        {
            LogError(" The bundled chdman.exe is not compatible with this version of Windows.");
            LogMessage(
                "       This typically occurs when running on older Windows versions (e.g., Windows 7)."
            );
            LogMessage(
                "       It can also occur when files from the win-arm64 release are copied into a win-x64 installation (or vice versa) - keep the two releases separate."
            );
            LogMessage(
                "       Please download a compatible version of chdman.exe from MAME releases."
            );
            ShowError(
                "chdman.exe is not compatible with this OS.\n\n"
                + "The bundled chdman.exe requires a newer Windows version, or it belongs to the other architecture release (win-x64 vs win-arm64).\n"
                + "For Windows 7, please obtain a compatible chdman.exe from an older MAME release."
            );
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            LogError($" Access denied when trying to start {Path.GetFileName(chdmanPath)}.");
            LogMessage(
                "       This can be caused by antivirus blocking the executable or insufficient file permissions."
            );
            ShowError(
                $"Access denied to {Path.GetFileName(chdmanPath)}.\n\nPlease check your antivirus settings or file permissions."
            );
            return false;
        }
        catch (Exception ex)
        {
            // Ensure process is terminated on any other exception
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                    await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
                }
                catch
                {
                    // Best effort - ignore errors during cleanup
                }
            }

            // Other errors are acceptable - at least the exe started or we have a generic error
            LogWarning($"Could not validate chdman compatibility: {ex.Message}", ex);
            SafeFireAndForget(ReportBugAsync("Could not validate chdman compatibility", ex));
            return true;
        }
    }

    /// <summary>Writes OS, architecture and resolved tool details to the activity log.</summary>
    private void LogEnvironmentDetails()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Environment Details ===");
            sb.AppendLine(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"User: {Environment.UserName}");
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"Process Architecture: {RuntimeInformation.ProcessArchitecture}"
            );
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"OS Architecture: {RuntimeInformation.OSArchitecture}"
            );
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"chdman executable: {_chdmanResolvedName} ({(_isChdmanAvailable ? "found" : "NOT FOUND")})"
            );
            sb.AppendLine("CHDSharp encoder: built-in (always available)");
            LogMessage(sb.ToString());
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>Writes the conversion mode welcome and readiness messages to the activity log.</summary>
    private void DisplayConversionInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Conversion Mode)");
        if (!_isChdmanAvailable && OperatingSystem.IsWindows())
        {
            LogWarning(
                " chdman.exe not found; conversions will use the built-in CHDSharp encoder."
            );
        }

        LogMessage("--- Ready for Conversion ---");
    }

    /// <summary>Writes the verification mode welcome and readiness messages to the activity log.</summary>
    private void DisplayVerificationInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Verification Mode)");

        LogMessage("--- Ready for Verification ---");
    }

    /// <summary>Writes the extraction mode welcome, guidance and readiness messages to the activity log.</summary>
    private void DisplayExtractionInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Extraction Mode)");

        LogMessage(
            "This feature extracts CHD files back to their original format (ISO/BIN/CUE etc.)"
        );
        LogMessage("--- Ready for Extraction ---");
    }

    /// <summary>
    ///     Updates the activity log, status message and Explorer layout when the selected tab changes.
    /// </summary>
    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Avalonia raises selection changes while the XAML is still being populated, before the
        // named controls exist; ignore those and react only to user/programmatic changes later.
        if (!_uiInitialized) return;

        if (e.Source is not TabControl control) return;

        if (!StartConversionButton.IsEnabled && !StartVerificationButton.IsEnabled) return;

        _ = Dispatcher.UIThread.InvokeAsync((Action)ClearUiLog);
        if (control.SelectedItem is TabItem selectedTab)
        {
            switch (selectedTab.Name)
            {
                case "ConvertTab":
                    DisplayConversionInstructionsInLog();
                    UpdateStatusBarMessage("Ready for conversion");
                    SpeedValue.Text = "0.0 MB/s";
                    break;
                case "VerifyTab":
                    DisplayVerificationInstructionsInLog();
                    UpdateStatusBarMessage("Ready for verification");
                    break;
                case "ExtractTab":
                    DisplayExtractionInstructionsInLog();
                    UpdateStatusBarMessage("Ready for extraction");
                    break;
                case "ExplorerTab":
                    DisplayExplorerInstructionsInLog();
                    UpdateStatusBarMessage("Ready to explore");
                    break;
            }

            SetExplorerLayout(string.Equals(selectedTab.Name, "ExplorerTab", StringComparison.Ordinal));
        }

        UpdateWriteSpeedDisplay(0);
        UpdateReadSpeedDisplay(0);
    }

    /// <summary>
    ///     Cancels a running operation before closing, then disposes resources and shuts the
    ///     application down.
    /// </summary>
    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        // Check if any operation is currently running using thread-safe Interlocked check
        var isOperationRunning = Interlocked.CompareExchange(ref _operationRunningState, 0, 0) != 0;

        if (isOperationRunning)
        {
            lock (_ctsLock)
            {
                if (!_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                    LogMessage("Cancelling operations before closing...");
                    UpdateStatusBarMessage("Cancelling...");
                }
            }

            _pendingClose = true;
            e.Cancel = true;
            return;
        }

        Dispose();

        // Shutting down re-entrantly from inside Closing calls Close() on this window again,
        // which raises Closing again in a loop and the window never closes. Post the shutdown so
        // it runs after the close has completed (the desktop lifetime also shuts down on the
        // main window close, so this is the explicit, ordered path).
        Dispatcher.UIThread.Post(App.ShutdownApp, DispatcherPriority.Background);
    }

    /// <summary>Writes an informational message to the Serilog log and the on-screen activity log.</summary>
    /// <param name="message">The message to log.</param>
    private void LogMessage(string message)
    {
        Log.Information("{Message}", message);
        AppendToUiLog(message);
    }

    /// <summary>Writes an error message to the Serilog log and the on-screen activity log.</summary>
    /// <param name="message">The message to log.</param>
    /// <param name="ex">Optional exception to log with the message.</param>
    private void LogError(string message, Exception? ex = null)
    {
        Log.Error(ex, "{Message}", message.TrimStart());
        AppendToUiLog($"ERROR: {message.TrimStart()}");
    }

    /// <summary>Writes a warning message to the Serilog log and the on-screen activity log.</summary>
    /// <param name="message">The message to log.</param>
    /// <param name="ex">Optional exception to log with the message.</param>
    private void LogWarning(string message, Exception? ex = null)
    {
        Log.Warning(ex, "{Message}", message.TrimStart());
        AppendToUiLog($"WARNING: {message.TrimStart()}");
    }

    /// <summary>
    ///     Queues a timestamped line for the UI log. The line is written to the control by the
    ///     flush timer, which batches bursts and caps the on-screen text length.
    /// </summary>
    /// <param name="message">The message to append.</param>
    private void AppendToUiLog(string message)
    {
        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        lock (_logQueueLock)
        {
            if (_pendingLogLines.Count >= MaxPendingLogLines) _pendingLogLines.Dequeue();

            _pendingLogLines.Enqueue(timestampedMessage);
        }
    }

    /// <summary>
    ///     Writes the queued log lines to the control, dropping the oldest half of the on-screen
    ///     text when it exceeds the cap. Runs on the UI thread from the flush timer.
    /// </summary>
    private void FlushPendingLogLines()
    {
        string[] lines;
        lock (_logQueueLock)
        {
            if (_pendingLogLines.Count == 0) return;

            lines = [.. _pendingLogLines];
            _pendingLogLines.Clear();
        }

        try
        {
            if (LogViewer.Text.Length > MaxLogLength)
            {
                var excess = LogViewer.Text.Length - MaxLogLength / 2;
                LogViewer.SelectionStart = 0;
                LogViewer.SelectionLength = excess;
                LogViewer.SelectedText =
                    $"[{DateTime.Now:HH:mm:ss.fff}] --- Log truncated to keep app responsive ---{Environment.NewLine}";
            }

            LogViewer.AppendText(
                $"{string.Join(Environment.NewLine, lines)}{Environment.NewLine}"
            );
            LogViewer.ScrollToEnd();
        }
        catch
        {
            /* ignore logging errors */
        }
    }

    /// <summary>Clears the on-screen log and discards any lines queued for it.</summary>
    private void ClearUiLog()
    {
        lock (_logQueueLock)
        {
            _pendingLogLines.Clear();
        }

        LogViewer.Clear();
    }

    /// <summary>
    ///     Sets the input folder for conversion from a command line argument.
    /// </summary>
    /// <param name="path">The path to the input folder.</param>
    private void SetInputFolder(string path)
    {
        if (Directory.Exists(path))
        {
            ConversionInputFolderTextBox.Text = path;
            if (string.IsNullOrWhiteSpace(ConversionOutputFolderTextBox.Text))
            {
                ConversionOutputFolderTextBox.Text = path;
            }

            LogMessage($"Input folder set from command line: {path}");
            SafeFireAndForget(LoadFilesForConversionAsync());
        }
        else
        {
            LogMessage($"Warning: Command line path does not exist: {path}");
        }
    }

    /// <summary>Opens the folder picker for the conversion input folder.</summary>
    private async void BrowseConversionInputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            await HandleFolderBrowseAsync(ConversionInputFolderTextBox, "Conversion input");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseConversionInputButton_ClickAsync");
        }
    }

    /// <summary>Opens the folder picker for the conversion output folder.</summary>
    private async void BrowseConversionOutputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            await HandleFolderBrowseAsync(ConversionOutputFolderTextBox, "Conversion output");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseConversionOutputButton_ClickAsync");
        }
    }

    /// <summary>Opens the folder picker for the verification input folder.</summary>
    private async void BrowseVerificationInputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            await HandleFolderBrowseAsync(VerificationInputFolderTextBox, "Verification input");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseVerificationInputButton_ClickAsync");
        }
    }

    /// <summary>Opens the folder picker for the extraction input folder.</summary>
    private async void BrowseExtractionInputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            await HandleFolderBrowseAsync(ExtractionInputFolderTextBox, "Extraction input");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseExtractionInputButton_ClickAsync");
        }
    }

    /// <summary>Opens the folder picker for the extraction output folder.</summary>
    private async void BrowseExtractionOutputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            await HandleFolderBrowseAsync(ExtractionOutputFolderTextBox, "Extraction output");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseExtractionOutputButton_ClickAsync");
        }
    }

    /// <summary>Validates the extraction inputs and options and runs the batch CHD extraction.</summary>
    private async void StartExtractionButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync((Action)ClearUiLog);
            DisplayExtractionInstructionsInLog();

            var inputFolder = PathUtils.ValidateAndNormalizePath(
                ExtractionInputFolderTextBox.Text,
                "CHD Files Folder",
                ShowError,
                LogMessage
            );
            var outputFolder = PathUtils.ValidateAndNormalizePath(
                ExtractionOutputFolderTextBox.Text,
                "Output Folder",
                ShowError,
                LogMessage
            );

            if (inputFolder == null || outputFolder == null) return;

            if (!Directory.Exists(inputFolder))
            {
                ShowError($"Input folder does not exist: {inputFolder}");
                return;
            }

            if (!Directory.Exists(outputFolder))
            {
                ShowError($"Output folder does not exist: {outputFolder}");
                return;
            }

            var selectedFiles = _extractionFiles
                .Where(static f => f.IsSelected)
                .Select(static f => f.FullPath)
                .ToArray();
            if (selectedFiles.Length == 0)
            {
                ShowError("No files selected for extraction.");
                return;
            }

            // Extracting into the source folder is allowed and needs no warning: an extraction whose
            // output would replace existing files of the same name is diverted into a subfolder
            // instead (see ExtractChdAsync), so nothing is overwritten and nothing is asked.

            RenewCancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            _activeOperation = "Extraction";
            await Task.Yield();
            StartOperationTimer();
            ResetSpeedCounters();

            var deleteOriginal = DeleteOriginalChdCheckBox.IsChecked ?? false;

            LogMessage("--- Starting batch extraction process... ---");
            _wasCancelled = false;

            try
            {
                CancellationToken token;
                lock (_ctsLock)
                {
                    token = _cts.Token;
                }

                await PerformBatchExtractionAsync(
                    inputFolder,
                    outputFolder,
                    deleteOriginal,
                    selectedFiles,
                    token
                );
            }
            catch (OperationCanceledException)
            {
                LogMessage("Extraction canceled.");
                _wasCancelled = true;
            }
            catch (Exception ex)
            {
                LogError(ex.Message, ex);
            }
            finally
            {
                FinishOperation("Extraction");
            }
        }
        catch (Exception ex)
        {
            LogError("StartExtractionButton_Click error", ex);
        }
    }

    /// <summary>
    ///     Shows a folder picker for a text box, stores the chosen path and refreshes the active tab's
    ///     file list.
    /// </summary>
    /// <param name="targetBox">Text box that receives the chosen folder.</param>
    /// <param name="logName">Folder type name used in the picker title and log messages.</param>
    private async Task HandleFolderBrowseAsync(TextBox targetBox, string logName)
    {
        var folder = await SelectFolderAsync($"Select {logName} folder");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var normalized = PathUtils.ValidateAndNormalizePath(folder, logName, ShowError, LogMessage);
        if (normalized != null)
        {
            targetBox.Text = normalized;
            RefreshFileListForActiveTab();
        }

        if (targetBox == ConversionInputFolderTextBox && normalized != null)
        {
            if (string.IsNullOrWhiteSpace(ConversionOutputFolderTextBox.Text))
            {
                ConversionOutputFolderTextBox.Text = normalized;
            }

            _fileWatcher.StartWatching(normalized);
            if (_fileWatcher.IsWatching)
            {
                LogMessage($"Monitoring input folder for file changes: {normalized}");
            }
        }

        UpdateStatusBarMessage($"{logName} folder selected");
    }

    /// <summary>Reloads the file list for the currently selected tab.</summary>
    private void RefreshFileListForActiveTab()
    {
        if (MainTabControl.SelectedItem is TabItem selectedTab)
        {
            switch (selectedTab.Name)
            {
                case "ConvertTab":
                    SafeFireAndForget(LoadFilesForConversionAsync());
                    break;
                case "VerifyTab":
                    SafeFireAndForget(LoadFilesForVerificationAsync());
                    break;
                case "ExtractTab":
                    SafeFireAndForget(LoadFilesForExtractionAsync());
                    break;
            }
        }
    }

    /// <summary>Scans the conversion input folder and populates the conversion file grid.</summary>
    /// <returns>A task that completes when the file list has been loaded.</returns>
    private Task LoadFilesForConversionAsync()
    {
        var inputFolder = ConversionInputFolderTextBox.Text;
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) return Task.CompletedTask;

        var includeSub = SearchSubfoldersConversionCheckBox.IsChecked ?? false;

        CancellationToken token;
        lock (_ctsLock)
        {
            token = _cts.Token;
        }

        return Task.Run(
            async () =>
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = includeSub,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                };

                var paths = Directory
                    .GetFiles(inputFolder, "*.*", options)
                    .Where(static file =>
                        FileExtensions.AllSupportedInputExtensionsForConversionSet.Contains(
                            Path.GetExtension(file)
                        )
                    )
                    .ToList();

                // A raw image that a sibling descriptor already covers must not be offered as its own
                // input. Both would target the same CHD name, and because the raw image has no track
                // layout it fails in chdman - which would then delete the descriptor's good output.
                paths = await InputFileFilter.RemoveCompanionDataFilesAsync(
                    paths,
                    LogMessage,
                    token
                );

                // A multi-part RAR is decoded from its first volume, so only that volume is offered.
                paths = InputFileFilter.RemoveRarVolumeParts(paths, LogMessage);

                var files = paths
                        .ConvertAll(f => new FileItem
                        {
                            FileName = Path.GetRelativePath(inputFolder, f),
                            FullPath = f,
                            FileSize = new FileInfo(f).Length,
                            IsSelected = true
                        })
                    ;

                Dispatcher.UIThread.Invoke(() => _conversionFiles.Clear());

                // Add items in chunks to avoid freezing the UI thread if there are thousands of files
                const int chunkSize = 100;
                try
                {
                    for (var i = 0; i < files.Count; i += chunkSize)
                    {
                        var chunk = files.Skip(i).Take(chunkSize).ToList();
                        await Dispatcher.UIThread.InvokeAsync(
                            () =>
                            {
                                foreach (var item in chunk)
                                    _conversionFiles.Add(item);
                                TotalFilesValue.Text = _conversionFiles.Count.ToString(
                                    CultureInfo.InvariantCulture
                                );
                            },
                            DispatcherPriority.Background,
                            token
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during chunked load is expected; partial results remain visible
                }
            },
            token
        );
    }

    /// <summary>Scans the verification input folder for CHD files and populates the verification grid.</summary>
    /// <returns>A task that completes when the file list has been loaded.</returns>
    private Task LoadFilesForVerificationAsync()
    {
        var inputFolder = VerificationInputFolderTextBox.Text;
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) return Task.CompletedTask;

        var includeSub = SearchSubfoldersVerificationCheckBox.IsChecked ?? false;

        CancellationToken token;
        lock (_ctsLock)
        {
            token = _cts.Token;
        }

        return Task.Run(
            async () =>
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = includeSub,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                };

                var files = Directory
                    .GetFiles(inputFolder, "*.chd", options)
                    .Where(f =>
                    {
                        if (!includeSub) return true;

                        var relPath = Path.GetRelativePath(inputFolder, f);
                        var firstPart = relPath.Split(Path.DirectorySeparatorChar)[0];
                        return !firstPart.Equals("Success", StringComparison.OrdinalIgnoreCase)
                               && !firstPart.Equals("Failed", StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(f => new FileItem
                    {
                        FileName = Path.GetRelativePath(inputFolder, f),
                        FullPath = f,
                        FileSize = new FileInfo(f).Length,
                        IsSelected = true
                    })
                    .ToList();

                Dispatcher.UIThread.Invoke(() => _verificationFiles.Clear());

                // Add items in chunks to avoid freezing the UI thread
                const int chunkSize = 100;
                try
                {
                    for (var i = 0; i < files.Count; i += chunkSize)
                    {
                        var chunk = files.Skip(i).Take(chunkSize).ToList();
                        await Dispatcher.UIThread.InvokeAsync(
                            () =>
                            {
                                foreach (var item in chunk)
                                    _verificationFiles.Add(item);
                                TotalFilesValue.Text = _verificationFiles.Count.ToString(
                                    CultureInfo.InvariantCulture
                                );
                            },
                            DispatcherPriority.Background,
                            token
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during chunked load is expected; partial results remain visible
                }
            },
            token
        );
    }

    /// <summary>Scans the extraction input folder for CHD files and populates the extraction grid.</summary>
    /// <returns>A task that completes when the file list has been loaded.</returns>
    private Task LoadFilesForExtractionAsync()
    {
        var inputFolder = ExtractionInputFolderTextBox.Text;
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) return Task.CompletedTask;

        var includeSub = SearchSubfoldersExtractionCheckBox.IsChecked ?? false;

        CancellationToken token;
        lock (_ctsLock)
        {
            token = _cts.Token;
        }

        return Task.Run(
            async () =>
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = includeSub,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                };

                var files = Directory
                    .GetFiles(inputFolder, "*.chd", options)
                    .Where(f =>
                    {
                        if (!includeSub) return true;

                        var relPath = Path.GetRelativePath(inputFolder, f);
                        var firstPart = relPath.Split(Path.DirectorySeparatorChar)[0];
                        return !firstPart.Equals("Success", StringComparison.OrdinalIgnoreCase)
                               && !firstPart.Equals("Failed", StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(f => new FileItem
                    {
                        FileName = Path.GetRelativePath(inputFolder, f),
                        FullPath = f,
                        FileSize = new FileInfo(f).Length,
                        IsSelected = true
                    })
                    .ToList();

                Dispatcher.UIThread.Invoke(() => _extractionFiles.Clear());

                // Add items in chunks to avoid freezing the UI thread
                const int chunkSize = 100;
                try
                {
                    for (var i = 0; i < files.Count; i += chunkSize)
                    {
                        var chunk = files.Skip(i).Take(chunkSize).ToList();
                        await Dispatcher.UIThread.InvokeAsync(
                            () =>
                            {
                                foreach (var item in chunk)
                                    _extractionFiles.Add(item);
                                TotalFilesValue.Text = _extractionFiles.Count.ToString(
                                    CultureInfo.InvariantCulture
                                );
                            },
                            DispatcherPriority.Background,
                            token
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during chunked load is expected; partial results remain visible
                }
            },
            token
        );
    }

    /// <summary>Selects every conversion input file.</summary>
    private void SelectAllConversion_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _conversionFiles) f.IsSelected = true;
    }

    /// <summary>Clears the selection of every conversion input file.</summary>
    private void DeselectAllConversion_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _conversionFiles) f.IsSelected = false;
    }

    /// <summary>Selects every verification CHD file.</summary>
    private void SelectAllVerification_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _verificationFiles) f.IsSelected = true;
    }

    /// <summary>Clears the selection of every verification CHD file.</summary>
    private void DeselectAllVerification_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _verificationFiles) f.IsSelected = false;
    }

    /// <summary>Selects every extraction CHD file.</summary>
    private void SelectAllExtraction_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _extractionFiles) f.IsSelected = true;
    }

    /// <summary>Clears the selection of every extraction CHD file.</summary>
    private void DeselectAllExtraction_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _extractionFiles) f.IsSelected = false;
    }

    /// <summary>Validates the conversion inputs and options and runs the batch conversion.</summary>
    private async void StartConversionButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync((Action)ClearUiLog);
            DisplayConversionInstructionsInLog();

            var inputFolder = PathUtils.ValidateAndNormalizePath(
                ConversionInputFolderTextBox.Text,
                "Source Files Folder",
                ShowError,
                LogMessage
            );
            var outputFolder = PathUtils.ValidateAndNormalizePath(
                ConversionOutputFolderTextBox.Text,
                "Output CHD Folder",
                ShowError,
                LogMessage
            );
            if (inputFolder == null || outputFolder == null) return;

            // Converting in place is allowed. The output name is always "<base>.chd" and .chd is not
            // a conversion input, so a source file can never be the target; and since the conversion
            // stages to .chdtmp and only moves into place on success, an existing CHD of the same
            // name survives a failed run.
            if (PathUtils.IsSameOrInsideDirectory(inputFolder, outputFolder))
            {
                LogMessage(
                    " The output folder is inside the source folder, so CHDs will be written alongside the originals."
                );
            }

            var selectedFiles = _conversionFiles
                .Where(static f => f.IsSelected)
                .Select(static f => f.FullPath)
                .ToArray();
            if (selectedFiles.Length == 0)
            {
                ShowError("No files selected for conversion.");
                return;
            }

            RenewCancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            _activeOperation = "Conversion";
            await Task.Yield();
            StartOperationTimer();
            ResetSpeedCounters();

            var deleteFiles = DeleteOriginalsCheckBox.IsChecked ?? false;
            var processSmallerFirst = ProcessSmallerFirstCheckBox.IsChecked ?? false;
            var forceCd = ForceCreateCdCheckBox.IsChecked ?? false;
            var forceDvd = ForceCreateDvdCheckBox.IsChecked ?? false;

            var timeoutEnabled = EnableConversionTimeoutCheckBox.IsChecked ?? false;
            var timeoutMinutes =
                timeoutEnabled
                && int.TryParse(
                    ConversionTimeoutTextBox.Text,
                    CultureInfo.InvariantCulture,
                    out var mins
                )
                && mins > 0
                    ? (int?)Math.Min(mins, AppConfig.MaxConversionTimeoutHours * 60)
                    : null;

            LogMessage("--- Starting batch conversion process... ---");
            _wasCancelled = false;

            try
            {
                CancellationToken token;
                lock (_ctsLock)
                {
                    token = _cts.Token;
                }

                await PerformBatchConversionAsync(
                    _chdmanExePath,
                    inputFolder,
                    outputFolder,
                    deleteFiles,
                    processSmallerFirst,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    selectedFiles,
                    token
                );
            }
            catch (OperationCanceledException)
            {
                LogMessage("Conversion canceled.");
                _wasCancelled = true;
            }
            catch (Exception ex)
            {
                LogError(ex.Message, ex);
            }
            finally
            {
                FinishOperation("Conversion");
            }
        }
        catch (Exception ex)
        {
            LogError("StartConversionButton_Click error", ex);
        }
    }

    /// <summary>Validates the verification inputs and options and runs the batch CHD verification.</summary>
    private async void StartVerificationButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync((Action)ClearUiLog);
            DisplayVerificationInstructionsInLog();

            var inputFolder = PathUtils.ValidateAndNormalizePath(
                VerificationInputFolderTextBox.Text,
                "CHD Files Folder",
                ShowError,
                LogMessage
            );
            if (inputFolder == null) return;

            var selectedFiles = _verificationFiles
                .Where(static f => f.IsSelected)
                .Select(static f => f.FullPath)
                .ToArray();
            if (selectedFiles.Length == 0)
            {
                ShowError("No files selected for verification.");
                return;
            }

            RenewCancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            _activeOperation = "Verification";
            await Task.Yield();
            StartOperationTimer();
            ResetSpeedCounters();

            var includeSubfolders = SearchSubfoldersVerificationCheckBox.IsChecked ?? false;
            var moveSuccess = MoveSuccessFilesCheckBox.IsChecked ?? false;
            var moveFailed = MoveFailedFilesCheckBox.IsChecked ?? false;
            var successFolder = moveSuccess ? Path.Combine(inputFolder, "Success") : string.Empty;
            var failedFolder = moveFailed ? Path.Combine(inputFolder, "Failed") : string.Empty;

            LogMessage("--- Starting batch verification process... ---");
            _wasCancelled = false;

            try
            {
                CancellationToken token;
                lock (_ctsLock)
                {
                    token = _cts.Token;
                }

                await PerformBatchVerificationAsync(
                    inputFolder,
                    includeSubfolders,
                    moveSuccess,
                    successFolder,
                    moveFailed,
                    failedFolder,
                    selectedFiles,
                    token
                );
            }
            catch (OperationCanceledException)
            {
                LogMessage("Verification canceled.");
                _wasCancelled = true;
            }
            catch (Exception ex)
            {
                LogError(ex.Message, ex);
            }
            finally
            {
                FinishOperation("Verification");
            }
        }
        catch (Exception ex)
        {
            LogError("StartVerificationButton_Click error", ex);
        }
    }

    /// <summary>
    ///     Stops the operation timers, restores the controls, logs the summary and closes the window
    ///     when a close was pending.
    /// </summary>
    /// <param name="opName">Name of the finished operation.</param>
    private void FinishOperation(string opName)
    {
        _activeOperation = string.Empty;
        _operationTimer.Stop();
        _elapsedTimeTimer.Stop();
        UpdateProcessingTimeDisplay();
        UpdateWriteSpeedDisplay(0);
        UpdateReadSpeedDisplay(0);
        SetControlsState(true);
        LogOperationSummary(opName);

        // Clear progress display
        ClearProgressDisplay();

        if (_pendingClose) Close();
    }

    /// <summary>Replaces the cancellation token source with a fresh one for the next operation.</summary>
    private void RenewCancellationTokenSource()
    {
        lock (_ctsLock)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
    }

    /// <summary>Requests cancellation of the running operation.</summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        lock (_ctsLock)
        {
            _cts.Cancel();
        }

        LogMessage("Cancellation requested...");
        UpdateStatusBarMessage("Cancelling...");
    }

    /// <summary>
    ///     Enables or disables the input controls and toggles the progress area while an operation runs.
    /// </summary>
    /// <param name="enabled">True when the UI should be idle and interactive.</param>
    private void SetControlsState(bool enabled)
    {
        // Thread-safely update operation state (0 = idle, 1 = running)
        Interlocked.Exchange(ref _operationRunningState, enabled ? 0 : 1);

        ConversionInputFolderTextBox.IsEnabled = enabled;
        BrowseConversionInputButton.IsEnabled = enabled;
        ConversionOutputFolderTextBox.IsEnabled = enabled;
        BrowseConversionOutputButton.IsEnabled = enabled;
        SearchSubfoldersConversionCheckBox.IsEnabled = enabled;
        DeleteOriginalsCheckBox.IsEnabled = enabled;
        ProcessSmallerFirstCheckBox.IsEnabled = enabled;
        StartConversionButton.IsEnabled = enabled;
        ForceCreateCdCheckBox.IsEnabled = enabled;
        ForceCreateDvdCheckBox.IsEnabled = enabled;
        VerificationInputFolderTextBox.IsEnabled = enabled;
        BrowseVerificationInputButton.IsEnabled = enabled;
        SearchSubfoldersVerificationCheckBox.IsEnabled = enabled;
        StartVerificationButton.IsEnabled = enabled;
        MoveSuccessFilesCheckBox.IsEnabled = enabled;
        MoveFailedFilesCheckBox.IsEnabled = enabled;
        ExtractionInputFolderTextBox.IsEnabled = enabled;
        BrowseExtractionInputButton.IsEnabled = enabled;
        ExtractionOutputFolderTextBox.IsEnabled = enabled;
        BrowseExtractionOutputButton.IsEnabled = enabled;
        SearchSubfoldersExtractionCheckBox.IsEnabled = enabled;
        DeleteOriginalChdCheckBox.IsEnabled = enabled;
        ExtractAutoRadioButton.IsEnabled = enabled;
        ExtractCdRadioButton.IsEnabled = enabled;
        ExtractDvdRadioButton.IsEnabled = enabled;
        ExtractGdiRadioButton.IsEnabled = enabled;
        ExtractHdRadioButton.IsEnabled = enabled;
        StartExtractionButton.IsEnabled = enabled;
        MainTabControl.IsEnabled = enabled;

        // Toggle progress area visibility
        ProgressAreaGrid.IsVisible = !enabled;
        ProgressText.IsVisible = !enabled;
        ProgressBar.IsVisible = !enabled;
        ProgressBar.IsIndeterminate = !enabled; // Start moving immediately
        CancelButton.IsVisible = !enabled;

        if (!enabled)
        {
            var tab = MainTabControl.SelectedItem as TabItem;
            var message = tab?.Name switch
            {
                "ConvertTab" => "Converting files...",
                "VerifyTab" => "Verifying files...",
                "ExtractTab" => "Extracting files...",
                _ => "Processing..."
            };
            UpdateStatusBarMessage(message);
        }
        else
        {
            ClearProgressDisplay();
            UpdateWriteSpeedDisplay(0);
            UpdateReadSpeedDisplay(0);
        }
    }

    /// <summary>Shows a folder picker and returns the chosen local path.</summary>
    /// <param name="description">Title shown in the picker.</param>
    /// <returns>The chosen folder path, or null when no folder was chosen.</returns>
    private async Task<string?> SelectFolderAsync(string description)
    {
        try
        {
            var storageProvider = GetTopLevel(this)?.StorageProvider;
            if (storageProvider is null) return null;

            var folders = await storageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = description, AllowMultiple = false }
            );

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Runs the conversion batch: checks the encoder and output folder, filters the selected inputs
    ///     and converts each file.
    /// </summary>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <param name="deleteFiles">Whether to delete each source after a successful conversion.</param>
    /// <param name="processSmallerFirst">Whether to convert the smallest files first.</param>
    /// <param name="forceCd">Whether to force the createcd verb.</param>
    /// <param name="forceDvd">Whether to force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout in minutes, or null for none.</param>
    /// <param name="selectedFiles">Full paths of the files to convert.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the batch has finished.</returns>
    private async Task PerformBatchConversionAsync(
        string chdmanPath,
        string inputFolder,
        string outputFolder,
        bool deleteFiles,
        bool processSmallerFirst,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        string[] selectedFiles,
        CancellationToken token
    )
    {
        // The built-in CHDSharp encoder is always available, so a batch can never be refused for a
        // missing encoder. On Windows chdman is the primary encoder and is probed once up front: an
        // executable the OS cannot start must fail here with one actionable message, not once per
        // file. A chdman that launches but crashes the CPU-compatibility probe is a different case —
        // the built-in encoder takes over per file.
        if (OperatingSystem.IsWindows() && File.Exists(chdmanPath))
        {
            if (!await ValidateExecutableAccessAsync(chdmanPath, "chdman.exe"))
                return;

            if (!await ValidateChdmanCompatibilityAsync(chdmanPath, token))
            {
                LogWarning(
                    " Continuing without chdman: the built-in CHDSharp encoder takes over whenever chdman fails."
                );
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            LogMessage(
                " chdman.exe not found; every file will be converted with the built-in CHDSharp encoder."
            );
        }
        else
        {
            LogMessage(" Using the built-in CHDSharp encoder.");
        }

        var filesToConvert = selectedFiles;

        if (processSmallerFirst)
        {
            filesToConvert = filesToConvert
                .OrderBy(static f =>
                {
                    try
                    {
                        return new FileInfo(f).Length;
                    }
                    catch
                    {
                        return 0;
                    }
                })
                .ToArray();
        }

        // Second line of defence behind the folder scan: whatever route the selection arrived by,
        // a raw image covered by a sibling descriptor is never converted on its own.
        filesToConvert =
        [
            .. await InputFileFilter.RemoveCompanionDataFilesAsync(
                filesToConvert,
                LogMessage,
                token
            )
        ];

        // Whatever route the selection arrived by, a multi-part RAR is only processed once, from
        // its first volume; the remaining parts would each extract the same set.
        filesToConvert = [.. InputFileFilter.RemoveRarVolumeParts(filesToConvert, LogMessage)];

        filesToConvert = ResolveOutputCollisions(filesToConvert, inputFolder, outputFolder);

        lock (_batchOutputPathsLock)
        {
            _batchOutputPaths.Clear();
        }

        _totalFilesProcessed = filesToConvert.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} files to process.");
        if (_totalFilesProcessed == 0) return;

        CheckDiskSpace(outputFolder, filesToConvert, true);

        // A disconnected or unmounted output drive (USB stick unplugged, mapped drive gone) is a
        // common cause of mid-batch failures: every file then dies with a confusing "Could not
        // find a part of the path" from deep inside the staging logic. Probe the folder once up
        // front so the user gets one actionable message instead of a batch of failures.
        if (!Directory.Exists(outputFolder))
        {
            try
            {
                Directory.CreateDirectory(outputFolder);
            }
            catch (Exception ex)
                when (ex
                          is DriveNotFoundException
                          or DirectoryNotFoundException
                          or IOException
                          or UnauthorizedAccessException
                          or SecurityException
                      && !IsCancellationException(ex)
                     )
            {
                LogError($" The output folder is not available: {outputFolder}");
                LogMessage(
                    "       Reconnect the drive that holds the output folder, or pick an output folder that exists, and try again."
                );
                ShowError(
                    $"The output folder is not available:\n\n{outputFolder}\n\nReconnect the drive or choose a different output folder and try again."
                );
                return;
            }
        }

        // chdman reports an unwritable destination only as a per-file "Permission denied" deep in
        // its own output (e.g. writing into "Program Files" without elevation). Probe the folder
        // once up front so the user gets one actionable message instead of a batch of failures.
        if (!IsOutputFolderWritable(outputFolder))
        {
            LogError($" The output folder is not writable: {outputFolder}");
            LogMessage(
                "       Choose a folder you have write access to (for example Documents or a data drive) and try again."
            );
            LogMessage(
                "       Writing into folders like 'Program Files' requires administrator rights."
            );
            ShowError(
                $"The output folder is not writable:\n\n{outputFolder}\n\nChoose a folder you have write access to and try again."
            );
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
            ProgressBar.Maximum = _totalFilesProcessed
        );
        var processedCount = 0;
        var cores = Environment.ProcessorCount;
        ResetSpeedCounters();

        foreach (var file in filesToConvert)
        {
            token.ThrowIfCancellationRequested();

            // Update text to show we are starting this file, but bar stays at 'processedCount'
            UpdateProgressDisplay(
                processedCount,
                _totalFilesProcessed,
                Path.GetFileName(file),
                "Converting"
            );

            var success = await ProcessSingleFileForConversionAsync(
                chdmanPath,
                file,
                inputFolder,
                outputFolder,
                deleteFiles,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                token
            );
            if (success)
                Interlocked.Increment(ref _processedOkCount);
            else
                Interlocked.Increment(ref _failedCount);

            processedCount++;
            UpdateProgressDisplay(
                processedCount,
                _totalFilesProcessed,
                Path.GetFileName(file),
                "Finishing"
            );
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
            UpdateWriteSpeedFromPerformanceCounter();
        }
    }

    /// <summary>Runs the extraction batch over the selected CHD files.</summary>
    /// <param name="inputFolder">Root of the extraction input folder.</param>
    /// <param name="outputFolder">Root of the extraction output folder.</param>
    /// <param name="deleteOriginal">Whether to delete each CHD after a successful extraction.</param>
    /// <param name="selectedFiles">Full paths of the CHD files to extract.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the batch has finished.</returns>
    private async Task PerformBatchExtractionAsync(
        string inputFolder,
        string outputFolder,
        bool deleteOriginal,
        string[] selectedFiles,
        CancellationToken token
    )
    {
        _totalFilesProcessed = selectedFiles.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} CHD files to extract.");
        if (_totalFilesProcessed == 0) return;

        CheckDiskSpace(outputFolder, selectedFiles, false);

        await Dispatcher.UIThread.InvokeAsync(() =>
            ProgressBar.Maximum = _totalFilesProcessed
        );
        var processedCount = 0;
        ResetSpeedCounters();

        foreach (var file in selectedFiles)
        {
            token.ThrowIfCancellationRequested();

            UpdateProgressDisplay(
                processedCount,
                _totalFilesProcessed,
                Path.GetFileName(file),
                "Extracting"
            );

            var success = await ExtractChdAsync(
                _chdmanExePath,
                file,
                inputFolder,
                outputFolder,
                deleteOriginal,
                token
            );
            if (success)
                Interlocked.Increment(ref _processedOkCount);
            else
                Interlocked.Increment(ref _failedCount);

            processedCount++;
            UpdateProgressDisplay(
                processedCount,
                _totalFilesProcessed,
                Path.GetFileName(file),
                "Finishing"
            );
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
            UpdateReadSpeedFromPerformanceCounter();
        }
    }

    /// <summary>
    ///     Converts a single input file to CHD, routing it to the handler for its resolved content type.
    /// </summary>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="inputFile">Full path of the input file.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <param name="deleteOriginal">Whether to delete the source after a successful conversion.</param>
    /// <param name="cores">Worker threads to give the encoder.</param>
    /// <param name="forceCd">Whether to force the createcd verb.</param>
    /// <param name="forceDvd">Whether to force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout in minutes, or null for none.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True when the file was converted successfully.</returns>
    private async Task<bool> ProcessSingleFileForConversionAsync(
        string chdmanPath,
        string inputFile,
        string inputFolder,
        string outputFolder,
        bool deleteOriginal,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        CancellationToken token
    )
    {
        inputFile = Path.GetFullPath(inputFile);
        var originalName = Path.GetFileName(inputFile);
        LogMessage($"Processing: {originalName}");

        if (!File.Exists(inputFile))
        {
            LogMessage($" File not found, skipping: {inputFile}");

            var watcherCtx = _fileWatcher.GetContextForMissingFile(inputFile);
            if (watcherCtx != null)
                LogMessage($"       {watcherCtx}");

            return false;
        }

        // A folder whose name looks like an image ("Game.BIN.ISO") would otherwise be handed to
        // chdman and fail deep inside it with a bare "Is a directory".
        if (Directory.Exists(inputFile))
        {
            LogWarning($" {originalName} is a folder, not a disc image file - skipping.");
            return false;
        }

        var ext = Path.GetExtension(inputFile).ToLowerInvariant();
        var tempDirs = new List<string>();

        try
        {
            token.ThrowIfCancellationRequested();

            var outputChd = ComputeOutputChdPath(inputFile, inputFolder, outputFolder);

            // Before trusting the extension, check what the file actually is. This picks up split
            // volume sets and files whose name disagrees with their content, both of which the
            // extension-based dispatch below would mishandle.
            var resolved = await TryResolveByContentAsync(
                inputFile,
                originalName,
                outputFolder,
                tempDirs,
                token
            );
            if (resolved is not null)
            {
                if (resolved.SkipReason is not null)
                {
                    LogWarning($" {originalName}: {resolved.SkipReason}");
                    return false;
                }

                var resolvedOutputDir = Path.GetDirectoryName(outputChd) ?? outputFolder;
                if (!Directory.Exists(resolvedOutputDir))
                    Directory.CreateDirectory(resolvedOutputDir);

                UpdateWriteSpeedDisplay(0);
                if (resolved.PathToConvert is null)
                {
                    LogWarning($" {originalName}: resolved path is null; skipping.");
                    return false;
                }

                var resolvedSuccess = await ConvertToChdAsync(
                    chdmanPath,
                    resolved.PathToConvert,
                    outputChd,
                    cores,
                    forceCd,
                    resolved.ForceDvd || forceDvd,
                    timeoutMinutes,
                    token
                );

                return await HandleConversionResultAsync(
                    resolvedSuccess,
                    inputFile,
                    originalName,
                    ext,
                    inputFolder,
                    outputChd,
                    deleteOriginal,
                    token
                );
            }

            string fileToProcess;
            if (ext.Equals(FileExtensions.Cso, StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessCsoFileForConversionAsync(
                    inputFile,
                    originalName,
                    outputFolder,
                    tempDirs,
                    token,
                    chdmanPath,
                    outputChd,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    deleteOriginal,
                    inputFolder
                );
            }
            else if (FileExtensions.ArchiveExtensionsSet.Contains(ext))
            {
                return await ProcessArchiveFileForConversionAsync(
                    inputFile,
                    inputFolder,
                    outputFolder,
                    tempDirs,
                    token,
                    chdmanPath,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    deleteOriginal
                );
            }
            else if (ext.Equals(FileExtensions.Pbp, StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessPbpFileForConversionAsync(
                    inputFile,
                    originalName,
                    inputFolder,
                    outputFolder,
                    tempDirs,
                    token,
                    chdmanPath,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    deleteOriginal
                );
            }
            else if (ext.Equals(FileExtensions.Ccd, StringComparison.OrdinalIgnoreCase))
            {
                return await ProcessCcdFileForConversionAsync(
                    inputFile,
                    inputFolder,
                    outputFolder,
                    tempDirs,
                    token,
                    chdmanPath,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    deleteOriginal
                );
            }
            else if (
                ext.Equals(FileExtensions.Mds, StringComparison.OrdinalIgnoreCase)
                || ext.Equals(FileExtensions.Mdx, StringComparison.OrdinalIgnoreCase)
            )
            {
                return await ProcessMdsFileForConversionAsync(
                    inputFile,
                    originalName,
                    inputFolder,
                    outputFolder,
                    tempDirs,
                    token,
                    chdmanPath,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    deleteOriginal
                );
            }
            else
            {
                // Try processing directly from source first to avoid unnecessary I/O
                fileToProcess = inputFile;

                var stagedCue = await TryStageCueForRawImageAsync(
                    inputFile,
                    originalName,
                    tempDirs,
                    token
                );
                if (stagedCue is not null) fileToProcess = stagedCue;
            }

            var isDependent = await ValidateDependentFilesAsync(
                ext,
                inputFile,
                originalName,
                token
            );
            if (!isDependent)
                return false;

            UpdateWriteSpeedDisplay(0);
            var outputDir = Path.GetDirectoryName(outputChd) ?? outputFolder;
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            var success = await TryDirectConversionAsync(
                chdmanPath,
                fileToProcess,
                outputChd,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                token,
                originalName
            );

            // Fallback: If direct conversion failed and we haven't already extracted to temp (i.e. it was a direct file attempt),
            // try copying to temp and converting there. This handles network path issues or file locking quirks.
            if (
                !success
                && string.Equals(fileToProcess, inputFile, StringComparison.Ordinal)
                && !token.IsCancellationRequested
            )
            {
                success = await TryRetryConversionViaTempCopyAsync(
                    chdmanPath,
                    inputFile,
                    originalName,
                    ext,
                    outputFolder,
                    outputChd,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    tempDirs,
                    token
                );
            }

            return await HandleConversionResultAsync(
                success,
                inputFile,
                originalName,
                ext,
                inputFolder,
                outputChd,
                deleteOriginal,
                token
            );
        }
        catch (OperationCanceledException)
        {
            // Nothing to clean up at the destination: conversions write to a staging file and only
            // move into place after success, so a cancelled run never touched an existing CHD.
            throw;
        }
        catch (Exception ex)
        {
            if (IsDiskSpaceException(ex))
            {
                LogError(
                    $" Not enough disk space to process {originalName}. Free up disk space and try again."
                );
            }
            else if (IsCorruptionException(ex))
            {
                LogError($" Archive appears to be corrupt or unsupported: {originalName}");
            }
            else
            {
                LogError($"Processing {originalName}: {ex.Message}", ex);
            }

            // The destination is deliberately left alone. A failure here says nothing about the CHD
            // already sitting at that path, which may be a good conversion from another input.
            return false;
        }
        finally
        {
            foreach (var tempDir in tempDirs)
            {
                if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                    await TryDeleteDirectoryAsync(tempDir, "temp dir", CancellationToken.None);
            }
        }
    }

    /// <summary>
    ///     Checks the output drive has room before chdman starts, and returns false when it clearly does
    ///     not. Free space between the certain-failure floor and the full source size is allowed through
    ///     with a warning, because compression ratios vary and a hard block would refuse conversions
    ///     that would have succeeded.
    /// </summary>
    /// <param name="chdmanInputPath">The file chdman will actually read.</param>
    /// <param name="originalInputPath">The original input, used for log messages.</param>
    /// <param name="outputPath">Destination CHD path, which determines the drive checked.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<bool> HasRoomForOutputAsync(
        string chdmanInputPath,
        string originalInputPath,
        string outputPath,
        CancellationToken token
    )
    {
        long sourceBytes;
        try
        {
            sourceBytes = await EstimateSourceBytesAsync(chdmanInputPath, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }

        if (sourceBytes <= 0) return true;

        long freeBytes;
        string driveName;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(outputPath));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return true;

            freeBytes = drive.AvailableFreeSpace;
            driveName = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return true;
        }

        var name = Path.GetFileName(originalInputPath);

        if (
            freeBytes < MinimumOutputFreeBytes
            || freeBytes < (long)(sourceBytes * MinimumOutputSizeRatio)
        )
        {
            LogError(
                $" Not enough disk space on {driveName} to convert {name}: {freeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB free for a {sourceBytes / (1024.0 * 1024.0 * 1024.0):F1} GB source. Skipping before starting the conversion."
            );
            return false;
        }

        if (freeBytes < sourceBytes)
        {
            LogMessage(
                $" {name}: only {freeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB free on {driveName} for a {sourceBytes / (1024.0 * 1024.0 * 1024.0):F1} GB source. Proceeding, but the conversion will fail if it does not compress enough."
            );
        }

        return true;
    }

    /// <summary>
    ///     Estimates the bytes chdman will read: for a descriptor, the total of the files it references;
    ///     otherwise the file's own size.
    /// </summary>
    private static async Task<long> EstimateSourceBytesAsync(
        string chdmanInputPath,
        CancellationToken token
    )
    {
        var ext = Path.GetExtension(chdmanInputPath).ToLowerInvariant();
        if (ext is FileExtensions.Cue or FileExtensions.Toc or FileExtensions.Gdi)
        {
            var referenced = ext switch
            {
                FileExtensions.Cue => await GameFileParser.GetReferencedFilesFromCueAsync(
                    chdmanInputPath,
                    static _ => { },
                    token
                ),
                FileExtensions.Gdi => await GameFileParser.GetReferencedFilesFromGdiAsync(
                    chdmanInputPath,
                    static _ => { },
                    token
                ),
                _ => await GameFileParser.GetReferencedFilesFromTocAsync(
                    chdmanInputPath,
                    static _ => { },
                    token
                )
            };

            long total = 0;
            foreach (var file in referenced.Distinct(StringComparer.OrdinalIgnoreCase))
                try
                {
                    total += new FileInfo(file).Length;
                }
#pragma warning disable RCS1075
                catch (Exception)
#pragma warning restore RCS1075
                {
                    /* a missing reference is reported elsewhere */
                }

            return total;
        }

        return new FileInfo(chdmanInputPath).Length;
    }

    /// <summary>
    ///     Inspects an input's leading bytes and, where the extension is misleading, works out what
    ///     should actually be converted. Returns null when the normal extension-based dispatch is
    ///     correct, which is the common case.
    ///     Handles two families of problem: images split into numbered volumes, which have to be
    ///     rejoined before anything can read them, and files whose extension disagrees with their
    ///     content - a disc image called .rar, or an .isz that was never compressed.
    /// </summary>
    /// <param name="inputFile">Path of the input file.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="outputFolder">Conversion output folder, used to pick a temp location.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<ResolvedInput?> TryResolveByContentAsync(
        string inputFile,
        string originalName,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        var ext = Path.GetExtension(inputFile).ToLowerInvariant();

        // Descriptors are text and have their own handlers; there is nothing to sniff.
        if (
            ext
            is FileExtensions.Cue
            or FileExtensions.Gdi
            or FileExtensions.Toc
            or FileExtensions.Ccd
            or FileExtensions.Mds
            or FileExtensions.Mdx
        )
        {
            return null;
        }

        var kind = DiscImageSignature.Detect(inputFile);
        var extensionClaimsArchive = FileExtensions.ArchiveExtensionsSet.Contains(ext);
        var extensionClaimsIsz = ext.Equals(FileExtensions.Isz, StringComparison.OrdinalIgnoreCase);

        // An archive that really is an archive: leave it to the archive handler.
        if (extensionClaimsArchive && DiscImageSignature.IsArchive(kind)) return null;

        var volumeSet = SplitImageJoiner.TryGetVolumeSet(inputFile);
        if (volumeSet is not null)
        {
            return await ResolveSplitVolumeSetAsync(
                volumeSet,
                originalName,
                outputFolder,
                tempDirs,
                token
            );
        }

        // Formats that need a step this build cannot perform. Say so plainly instead of letting
        // chdman fail with a sector-size error.
        switch (kind)
        {
            case DiscImageKind.Ecm:
                return await ResolveEcmAsync(
                    inputFile,
                    originalName,
                    outputFolder,
                    tempDirs,
                    token
                );
            case DiscImageKind.Isz:
                return await ResolveIszAsync(
                    inputFile,
                    originalName,
                    outputFolder,
                    tempDirs,
                    token
                );
            case DiscImageKind.Chd:
                return ResolvedInput.Skip(
                    "this file is already a CHD. Copy it to the output folder rather than converting it."
                );
        }

        if (!extensionClaimsArchive && !extensionClaimsIsz)
        {
            // The extension is not lying about being a container, so the normal path applies.
            return null;
        }

        // The extension promises a container and the content is a plain image. Routine for .isz:
        // files get renamed to it to mean "a disc image" without UltraISO ever being involved, and
        // chdman picks its verb from the extension and knows nothing about .isz.
        return await ResolveMislabelledContainerAsync(
            inputFile,
            originalName,
            extensionClaimsIsz ? IszContainerDescription : "an archive",
            kind,
            tempDirs,
            token
        );
    }

    /// <summary>
    ///     Joins a split volume set into a temp file and decides how the result should be converted.
    /// </summary>
    /// <param name="volumeSet">Volumes in order, first part first.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="outputFolder">Conversion output folder, used to pick a temp location.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<ResolvedInput> ResolveSplitVolumeSetAsync(
        List<string> volumeSet,
        string originalName,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        var firstVolume = volumeSet[0];

        // A multi-part archive is a different thing entirely. 7-Zip and ZIP volume sets are
        // byte-splits of one archive stream, which the bundled 7za reads straight from the first
        // volume, so they convert end to end. RAR sets are decoded from their first volume by
        // SharpCompress, which can follow the whole set from that path.
        var firstKind = DiscImageSignature.Detect(firstVolume);
        if (DiscImageSignature.IsArchive(firstKind))
        {
            if (firstKind is DiscImageKind.Rar)
            {
                return await ResolveRarVolumeSetAsync(
                    volumeSet,
                    originalName,
                    outputFolder,
                    tempDirs,
                    token
                );
            }

            return await ResolveSplitArchiveSetAsync(
                volumeSet,
                originalName,
                outputFolder,
                tempDirs,
                token
            );
        }

        var totalBytes = SplitImageJoiner.GetTotalBytes(volumeSet);
        LogMessage(
            $" {originalName} is part 1 of a {volumeSet.Count}-part split image ({totalBytes:N0} bytes total); joining the parts."
        );

        var tempDir = PathUtils.GetBestTempDirectory(
            firstVolume,
            outputFolder,
            TempDirPrefix,
            totalBytes
        );
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);
        tempDirs.Add(tempDir);

        var joinedPath = Path.Combine(
            tempDir,
            Path.GetFileNameWithoutExtension(firstVolume) + FileExtensions.Bin
        );
        var joinedBytes = await SplitImageJoiner.JoinAsync(volumeSet, joinedPath, token);

        return await ClassifyRecoveredImageAsync(
            joinedPath,
            tempDir,
            "Joined image",
            $"the {volumeSet.Count} parts join to {joinedBytes:N0} bytes, which is not a whole number of 2352-byte CD sectors or 2048-byte data sectors. A part is missing or truncated, so the set needs re-downloading.",
            token
        );
    }

    /// <summary>
    ///     Extracts a numbered archive volume set (.7z.001/.002 or .zip.001/.002 style) with the
    ///     bundled 7za and decides how the extracted disc image should be converted.
    /// </summary>
    /// <param name="volumeSet">Volumes in order, first part first.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="outputFolder">Conversion output folder, used to pick a temp location.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<ResolvedInput> ResolveSplitArchiveSetAsync(
        List<string> volumeSet,
        string originalName,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        var firstVolume = volumeSet[0];
        var totalBytes = SplitImageJoiner.GetTotalBytes(volumeSet);
        LogMessage(
            $" {originalName} is part 1 of a {volumeSet.Count}-part archive ({totalBytes:N0} bytes total); extracting the set with 7za.exe."
        );

        var tempDir = PathUtils.GetBestTempDirectory(
            firstVolume,
            outputFolder,
            TempDirPrefix,
            totalBytes
        );
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);
        tempDirs.Add(tempDir);

        var extraction = await _archiveService.ExtractSplitArchiveWith7ZaAsync(
            firstVolume,
            tempDir,
            LogMessage,
            token,
            totalBytes
        );
        if (!extraction.Success)
        {
            return ResolvedInput.Skip(extraction.ErrorMessage);
        }

        return await ResolveExtractedArchiveContentsAsync(
            tempDir,
            originalName,
            outputFolder,
            tempDirs,
            token
        );
    }

    /// <summary>
    ///     Extracts a RAR volume set ("game.part01.rar" with the rest of the set beside it, or a RAR
    ///     renamed to ".001") and decides how the extracted disc image should be converted.
    ///     SharpCompress follows the whole set from its first volume, so the set is handed over as a
    ///     path rather than a stream; opening single parts is what used to crash its decoder.
    /// </summary>
    /// <param name="volumeSet">Volumes in order, first part first.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="outputFolder">Conversion output folder, used to pick a temp location.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<ResolvedInput> ResolveRarVolumeSetAsync(
        List<string> volumeSet,
        string originalName,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        var firstVolume = volumeSet[0];
        var totalBytes = SplitImageJoiner.GetTotalBytes(volumeSet);
        LogMessage(
            $" {originalName} is part 1 of a {volumeSet.Count}-part RAR archive ({totalBytes:N0} bytes total); extracting the set."
        );

        var tempDir = PathUtils.GetBestTempDirectory(
            firstVolume,
            outputFolder,
            TempDirPrefix,
            totalBytes
        );
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);
        tempDirs.Add(tempDir);

        var extraction = await _archiveService.ExtractArchiveAsync(
            firstVolume,
            tempDir,
            LogMessage,
            token,
            totalBytes
        );
        if (!extraction.Success)
        {
            return ResolvedInput.Skip(extraction.ErrorMessage);
        }

        return await ResolveExtractedArchiveContentsAsync(
            tempDir,
            originalName,
            outputFolder,
            tempDirs,
            token
        );
    }

    /// <summary>
    ///     Picks the convertible file out of an extracted archive volume set and routes it through
    ///     the same classification a loose input gets. Shared by the 7za (7z/ZIP) and RAR volume-set
    ///     paths.
    /// </summary>
    /// <param name="tempDir">Directory the set was extracted into.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="outputFolder">Conversion output folder, used to pick a temp location.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<ResolvedInput> ResolveExtractedArchiveContentsAsync(
        string tempDir,
        string originalName,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        var found = await ArchiveService.CollectExtractedPrimaryFilesAsync(
            tempDir,
            LogMessage,
            token
        );
        if (!found.Success)
        {
            return ResolvedInput.Skip(found.ErrorMessage);
        }

        // Drop raw images that a descriptor in the archive already covers, mirroring the loose
        // archive path, so a cue/bin set converts once, through its cue.
        var primaries = await InputFileFilter.RemoveCompanionDataFilesAsync(
            found.FilePaths,
            LogMessage,
            token
        );
        if (primaries.Count == 0)
        {
            return ResolvedInput.Skip(
                "the extracted set contained no supported disc image or descriptor."
            );
        }

        if (primaries.Count > 1)
        {
            LogWarning(
                $" The extracted set contains {primaries.Count} convertible files; converting {Path.GetFileName(primaries[0])} only. Extract the set manually to convert the others."
            );
        }

        var selected = primaries[0];
        var selectedExt = Path.GetExtension(selected).ToLowerInvariant();
        switch (selectedExt)
        {
            // An archived ISZ is decompressed exactly like a loose one before anything can read it.
            case FileExtensions.Isz:
                return await ResolveIszAsync(selected, originalName, outputFolder, tempDirs, token);
            // chdman cannot read these descriptors directly and their resolvers need a conversion
            // loop this single-path resolution cannot run, so they stay a manual job.
            case FileExtensions.Ccd or FileExtensions.Mds or FileExtensions.Mdx:
                return ResolvedInput.Skip(
                    $"the extracted set contains a {selectedExt.TrimStart('.')} descriptor. Extract the set manually and add the descriptor directly."
                );
            case FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc:
                return ResolvedInput.Convert(selected, false);
        }

        // A bare image needs the same treatment a joined one gets: a cue for raw sectors, or a
        // DVD classification for cooked ones.
        return await ClassifyRecoveredImageAsync(
            selected,
            tempDir,
            "Extracted image",
            "the extracted image is not a whole number of 2352-byte CD sectors or 2048-byte data sectors, so the archive is probably damaged.",
            token
        );
    }

    /// <summary>
    ///     Decodes an ECM-encoded image and decides how the result should be converted. Nothing external
    ///     is needed: the sector parity ECM strips out is regenerated in-process.
    /// </summary>
    /// <param name="inputFile">Path of the .ecm file.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="outputFolder">Conversion output folder, used to pick a temp location.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<ResolvedInput> ResolveEcmAsync(
        string inputFile,
        string originalName,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        // ECM typically halves an image, so allow for the decoded size being well above the input.
        long estimatedBytes;
        try
        {
            estimatedBytes = new FileInfo(inputFile).Length * 3;
        }
        catch (Exception)
        {
            estimatedBytes = 0;
        }

        var tempDir = PathUtils.GetBestTempDirectory(
            inputFile,
            outputFolder,
            TempDirPrefix,
            estimatedBytes
        );
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);
        tempDirs.Add(tempDir);

        var decodedPath = Path.Combine(tempDir, EcmImageDecoder.GetDecodedFileName(inputFile));
        LogMessage($" {originalName} is ECM-encoded; restoring the sectors it had stripped.");

        var decoded = await EcmImageDecoder.DecodeAsync(inputFile, decodedPath, LogMessage, token);
        if (!decoded.Success)
        {
            // A partial image would convert and look fine, so it does not survive a failure.
            await TryDeleteFileAsync(decodedPath, "incomplete ECM decode", CancellationToken.None);

            return ResolvedInput.Skip(decoded.FailureReason!);
        }

        return await ClassifyRecoveredImageAsync(
            decoded.OutputPath!,
            tempDir,
            "Decoded image",
            "the decoded image is not a whole number of 2352-byte CD sectors or 2048-byte data sectors, so the .ecm file is probably damaged.",
            token
        );
    }

    /// <summary>
    ///     Decompresses an ISZ image into a temp directory and decides how the restored image should be
    ///     converted. Nothing external is needed: both ISZ compressors are already available in-process.
    /// </summary>
    /// <param name="inputFile">Path of the .isz file, the first segment when the image is split.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="outputFolder">Conversion output folder, used to pick a temp location.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<ResolvedInput> ResolveIszAsync(
        string inputFile,
        string originalName,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        var header = await IszDecoder.TryReadHeaderAsync(inputFile, token);
        if (header is null)
        {
            return ResolvedInput.Skip(
                "the file starts with an ISZ signature but its header could not be read, so it is damaged."
            );
        }

        var unusable = header.GetUnusableReason();
        if (unusable is not null) return ResolvedInput.Skip(unusable);

        // The restored image is the size the header declares, and it is written whole before chdman
        // reads it, so the temp location has to hold all of it.
        var tempDir = PathUtils.GetBestTempDirectory(
            inputFile,
            outputFolder,
            TempDirPrefix,
            header.ImageSizeBytes
        );
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);
        tempDirs.Add(tempDir);

        var decodedPath = Path.Combine(tempDir, IszDecoder.GetDecodedFileName(inputFile));
        LogMessage(
            $" {originalName} is a compressed ISZ image; decompressing it to {header.ImageSizeBytes / (1024.0 * 1024.0):F0} MB."
        );

        var decoded = await IszDecoder.DecodeAsync(inputFile, decodedPath, LogMessage, token);
        if (!decoded.Success)
        {
            // A partial image is worse than none: it would convert and look fine.
            await TryDeleteFileAsync(
                decodedPath,
                "incomplete ISZ decompression",
                CancellationToken.None
            );

            return ResolvedInput.Skip(decoded.FailureReason!);
        }

        return await ClassifyRecoveredImageAsync(
            decoded.OutputPath!,
            tempDir,
            "Decompressed image",
            "the decompressed image is not a whole number of 2352-byte CD sectors or 2048-byte data sectors, so the .isz file is probably damaged.",
            token
        );
    }

    /// <summary>
    ///     Works out how an image recovered into a temp directory - joined from parts, decoded from ECM
    ///     or decompressed from ISZ - should be handed to chdman, then wraps the classifier's answer
    ///     as a conversion request.
    /// </summary>
    /// <param name="imagePath">The recovered image.</param>
    /// <param name="workDir">Directory holding it, where any cue is written.</param>
    /// <param name="description">How to refer to the image in log messages.</param>
    /// <param name="misalignedReason">Skip reason when the size fits no known sector layout.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<ResolvedInput> ClassifyRecoveredImageAsync(
        string imagePath,
        string workDir,
        string description,
        string misalignedReason,
        CancellationToken token
    )
    {
        var result = await RecoveredImageClassifier
            .ClassifyAsync(imagePath, workDir, description, misalignedReason, LogMessage, token)
            .ConfigureAwait(false);

        if (!result.Success) return ResolvedInput.Skip(result.SkipReason!);

        return result.DvdImagePath is not null
            ? ResolvedInput.Convert(result.DvdImagePath, true)
            : ResolvedInput.Convert(result.CuePath!, false);
    }

    /// <summary>True when the file's size is a whole number of 2048-byte sectors.</summary>
    /// <param name="path">File to measure.</param>
    private static bool IsCookedImageSize(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            return length > 0 && length % MdsDisc.CookedSectorSize == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     Creates a temp directory and writes a cue in it that references <paramref name="imagePath" />
    ///     where it lies. Returns the cue path, or null when the image cannot be referenced relatively.
    /// </summary>
    /// <param name="imagePath">Disc image to describe.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="trackMode">Cue track mode, e.g. "MODE2/2352".</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<string?> StageCueForImageAsync(
        string imagePath,
        string originalName,
        string trackMode,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        // The cue has to be on the image's volume, not merely somewhere with space, because chdman
        // joins a cue's FILE entry to the cue's own directory and cannot follow an absolute path.
        var tempDir = await Task.Run(
            () => PathUtils.CreateTempDirectoryOnSameVolume(imagePath, TempDirPrefix),
            token
        );
        if (tempDir is null)
        {
            // Informational: the image sits on read-only media, but the conversion still
            // proceeds with the image as-is, so this is not an error condition.
            LogMessage(
                $" {originalName}: no writable location on the same volume for a generated cue; converting the image as-is."
            );
            return null;
        }

        tempDirs.Add(tempDir);

        var cuePath = await RawCdImageDetector.TryWriteCueAsync(
            imagePath,
            trackMode,
            tempDir,
            token
        );
        if (cuePath is null)
        {
            LogMessage(
                $" {originalName}: a generated cue could not reference the image relatively; converting the image as-is."
            );
        }

        return cuePath;
    }

    /// <summary>
    ///     Builds a cue for a disc image that chdman cannot interpret from its extension alone, and
    ///     returns the cue path to convert instead of the image. Returns null when the image needs no
    ///     help, in which case the original input is converted unchanged.
    /// </summary>
    /// <param name="inputFile">Full path of the disc image.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<string?> TryStageCueForRawImageAsync(
        string inputFile,
        string originalName,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        var ext = Path.GetExtension(inputFile).ToLowerInvariant();
        if (!RawCdImageDetector.IsCandidateExtension(ext)) return null;

        // A companion cue already describes this image, and ConvertToChdAsync redirects to it.
        if (File.Exists(Path.ChangeExtension(inputFile, FileExtensions.Cue))) return null;

        var trackMode = RawCdImageDetector.DetectTrackMode(inputFile);
        if (trackMode is not null)
        {
            LogMessage(
                $" {originalName} holds raw {RawCdImageDetector.RawSectorSize}-byte CD sectors ({trackMode}); generating a cue so it converts as a CD."
            );
        }
        else if (ext.Equals(FileExtensions.Bin, StringComparison.OrdinalIgnoreCase))
        {
            // A bare .bin has no descriptor and chdman cannot read one directly, so fall back to the
            // same single-track assumption the archive path makes. If the mode guess is wrong the
            // alternate-mode retry settles it. Audio tracks cannot be recovered without a cue or
            // TOC, so a multi-track disc converted this way will be missing its CDDA.
            trackMode = BinCueGenerator.Mode2;
            // Informational, not a malfunction: this is the user's data shape (e.g. a console BIOS
            // dropped into the input folder), so it goes to the UI log only - no bug report.
            LogMessage(
                $" {originalName} has no cue and no readable sector header; assuming a single {trackMode} data track. Any CDDA audio tracks cannot be recovered without a cue. If this file is not a disc image (e.g. a console BIOS), remove it from the input folder."
            );
        }
        else
        {
            // A cooked 2048-byte image: the existing extension-based routing is correct.
            return null;
        }

        return await StageCueForImageAsync(inputFile, originalName, trackMode, tempDirs, token);
    }

    /// <summary>
    ///     Returns the CHD path a loose input file converts to, mirroring the input folder structure.
    ///     The batch collision preflight and the conversion itself must agree, so both call this.
    /// </summary>
    /// <param name="inputFile">Full path of the input file.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    private static string ComputeOutputChdPath(
        string inputFile,
        string inputFolder,
        string outputFolder
    )
    {
        var chdBase = Path.GetFileNameWithoutExtension(inputFile);

        // Maintain directory structure if searching subfolders
        var relativePath = PathUtils.GetSafeRelativePath(
            inputFolder,
            Path.GetDirectoryName(inputFile) ?? inputFolder
        );
        var targetDir = string.Equals(relativePath, ".", StringComparison.Ordinal)
            ? outputFolder
            : Path.Combine(outputFolder, relativePath);

        return Path.Combine(targetDir, PathUtils.SanitizeFileName(chdBase) + FileExtensions.Chd);
    }

    /// <summary>
    ///     True when the input is an archive (including split .001 and RAR volume sets), whose output
    ///     name is derived from its contents rather than the input's own base name.
    /// </summary>
    /// <param name="path">Full path of the input file.</param>
    /// <returns>True when the input is an archive.</returns>
    private static bool IsArchiveInput(string path)
    {
        return FileExtensions.ArchiveExtensionsSet.Contains(Path.GetExtension(path))
            || path.EndsWith(".zip.001", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".7z.001", StringComparison.OrdinalIgnoreCase)
            || RarVolumeSet.TryGetPartInfo(path, out _, out _);
    }

    /// <summary>
    ///     Drops inputs whose output CHD path is already produced by another input in the batch.
    ///     Converting both would only overwrite one product with the other, so the redundant
    ///     conversion is skipped up front and the resolution is logged. Archives are left out of
    ///     the prediction: their product is named after a file inside them, which cannot be known
    ///     without extracting, so real archive collisions are caught when the output is moved.
    /// </summary>
    /// <param name="filesToConvert">The inputs about to be processed.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    private string[] ResolveOutputCollisions(
        string[] filesToConvert,
        string inputFolder,
        string outputFolder
    )
    {
        var (kept, skipped) = InputFileFilter.ResolveOutputCollisions(
            filesToConvert,
            f => IsArchiveInput(f) ? f : ComputeOutputChdPath(f, inputFolder, outputFolder)
        );

        foreach (var duplicate in skipped)
        {
            LogMessage(
                $" {Path.GetFileName(duplicate.SkippedFile)} also converts to {Path.GetFileName(duplicate.OutputPath)}; skipping it because {Path.GetFileName(duplicate.KeptFile)} already targets the same output file."
            );
        }

        return kept;
    }

    /// <summary>
    ///     Computes the output CHD path for a file extracted from an archive, mirroring the original
    ///     input's folder structure plus the extracted file's own directory inside the archive.
    /// </summary>
    /// <param name="extractedFilePath">Full path of the extracted file.</param>
    /// <param name="extractionRoot">Directory the archive was extracted into.</param>
    /// <param name="originalInputFile">Full path of the archive the file came from.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <returns>The full output CHD path.</returns>
    private static string ComputeOutputChdPathForExtractedFile(
        string extractedFilePath,
        string extractionRoot,
        string originalInputFile,
        string inputFolder,
        string outputFolder
    )
    {
        // Use the original input file (e.g. the archive) to determine the relative path
        var archiveRelative = PathUtils.GetSafeRelativePath(
            inputFolder,
            Path.GetDirectoryName(originalInputFile) ?? inputFolder
        );

        // Discs with the same name in different archive subfolders (Disc1/game.cue and
        // Disc2/game.cue) must not both land on output/game.chd, where the second would overwrite
        // the first, so the archive's internal structure is preserved too.
        var innerRelative = PathUtils.GetSafeRelativePath(
            extractionRoot,
            Path.GetDirectoryName(extractedFilePath) ?? extractionRoot
        );

        var relativePath = (archiveRelative, innerRelative) switch
        {
            (".", ".") => ".",
            (".", _) => innerRelative,
            (_, ".") => archiveRelative,
            _ => Path.Combine(archiveRelative, innerRelative)
        };

        var targetDir = string.Equals(relativePath, ".", StringComparison.Ordinal)
            ? outputFolder
            : Path.Combine(outputFolder, relativePath);
        var chdBase = Path.GetFileNameWithoutExtension(extractedFilePath);
        return Path.Combine(targetDir, PathUtils.SanitizeFileName(chdBase) + FileExtensions.Chd);
    }

    /// <summary>Decompresses a CSO image to a temporary ISO and converts it to CHD.</summary>
    /// <param name="inputFile">Full path of the CSO file.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="outputChd">Destination CHD path.</param>
    /// <param name="cores">Worker threads to give the encoder.</param>
    /// <param name="forceCd">Whether to force the createcd verb.</param>
    /// <param name="forceDvd">Whether to force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout in minutes, or null for none.</param>
    /// <param name="deleteOriginal">Whether to delete the source after a successful conversion.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <returns>True when the file was converted successfully.</returns>
    private async Task<bool> ProcessCsoFileForConversionAsync(
        string inputFile,
        string originalName,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token,
        string chdmanPath,
        string outputChd,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        bool deleteOriginal,
        string inputFolder
    )
    {
        long csoSize = 0;
        try
        {
            csoSize = new FileInfo(inputFile).Length;
        }
        catch
        {
            /* ignored */
        }

        var tempDir = PathUtils.GetBestTempDirectory(
            inputFile,
            outputFolder,
            TempDirPrefix,
            csoSize
        );
        tempDirs.Add(tempDir);
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);
        var tempIso = PathUtils.GetSafeTempFileName(originalName, "iso", tempDir);

        var result = await ArchiveService.ExtractCsoAsync(
            inputFile,
            tempIso,
            tempDir,
            LogMessage,
            token
        );
        if (!result.Success)
            return false;

        var fileToProcess = result.FilePath;
        UpdateWriteSpeedDisplay(0);
        var outputDir = Path.GetDirectoryName(outputChd) ?? outputFolder;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        var success = await TryDirectConversionAsync(
            chdmanPath,
            fileToProcess,
            outputChd,
            cores,
            forceCd,
            forceDvd,
            timeoutMinutes,
            token,
            originalName
        );
        return await HandleConversionResultAsync(
            success,
            inputFile,
            originalName,
            Path.GetExtension(inputFile).ToLowerInvariant(),
            inputFolder,
            outputChd,
            deleteOriginal,
            token
        );
    }

    /// <summary>Extracts an archive and converts each supported disc image it contains.</summary>
    /// <param name="inputFile">Full path of the archive.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="cores">Worker threads to give the encoder.</param>
    /// <param name="forceCd">Whether to force the createcd verb.</param>
    /// <param name="forceDvd">Whether to force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout in minutes, or null for none.</param>
    /// <param name="deleteOriginal">Whether to delete the archive after all conversions succeed.</param>
    /// <returns>True when every extracted image was converted successfully.</returns>
    private async Task<bool> ProcessArchiveFileForConversionAsync(
        string inputFile,
        string inputFolder,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token,
        string chdmanPath,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        bool deleteOriginal
    )
    {
        long archiveSize = 0;
        try
        {
            archiveSize = new FileInfo(inputFile).Length;
        }
        catch
        {
            /* ignored */
        }

        var tempDir = PathUtils.GetBestTempDirectory(
            inputFile,
            outputFolder,
            TempDirPrefix,
            archiveSize
        );
        tempDirs.Add(tempDir);
        var result = await _archiveService.ExtractArchiveAsync(
            inputFile,
            tempDir,
            LogMessage,
            token
        );
        if (!result.Success)
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                LogError($" {result.ErrorMessage}");
            return false;
        }

        var allSucceeded = true;

        // Drop raw images that a descriptor in the archive already covers, so a cue/bin or CloneCD
        // set inside an archive converts once, through its descriptor, instead of once per file with
        // both attempts aimed at the same output name.
        var filesToConvert = await InputFileFilter.RemoveCompanionDataFilesAsync(
            result.FilePaths,
            LogMessage,
            token
        );

        foreach (var extractedFile in filesToConvert)
        {
            token.ThrowIfCancellationRequested();
            var extractedFileOutputChd = ComputeOutputChdPathForExtractedFile(
                extractedFile,
                tempDir,
                inputFile,
                inputFolder,
                outputFolder
            );
            if (BinCueGenerator.IsAutoCue(extractedFile))
            {
                // Auto-generated cue ("Game.autocue.cue") should produce "Game.chd", not "Game.autocue.chd".
                var outputDir = Path.GetDirectoryName(extractedFileOutputChd) ?? outputFolder;
                extractedFileOutputChd = Path.Combine(
                    outputDir,
                    Path.GetFileNameWithoutExtension(
                        Path.GetFileNameWithoutExtension(extractedFile)
                    ) + FileExtensions.Chd
                );
            }

            // Archive extractions skip the regular dependency validation, so a cue whose bins are
            // missing (incomplete download, separate bin archive, CRC-skipped entries) would
            // otherwise fail deep inside chdman with a cryptic "couldn't find bin file" error.
            // Detect that up front and skip with a clear warning.
            var extractedExt = Path.GetExtension(extractedFile).ToLowerInvariant();
            if (extractedExt is FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc)
            {
                try
                {
                    var missingNames = await GetMissingDependentFileNamesAsync(
                        extractedExt,
                        extractedFile,
                        token
                    );
                    if (missingNames.Count > 0)
                    {
                        LogWarning(
                            $" {Path.GetFileName(extractedFile)} — referenced files are missing: {string.Join(", ", missingNames)}. Skipping (data files not found in the archive)."
                        );
                        allSucceeded = false;
                        continue;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogWarning(
                        $" {Path.GetFileName(extractedFile)} — could not validate referenced files: {ex.Message}. Skipping."
                    );
                    allSucceeded = false;
                    continue;
                }
            }

            var extractedOutputDir = Path.GetDirectoryName(extractedFileOutputChd) ?? outputFolder;
            if (!Directory.Exists(extractedOutputDir))
                Directory.CreateDirectory(extractedOutputDir);

            LogMessage($"Converting extracted file: {Path.GetFileName(extractedFile)}");

            bool converted;
            if (extractedExt.Equals(FileExtensions.Ccd, StringComparison.OrdinalIgnoreCase))
            {
                // chdman cannot read a .ccd, so a CloneCD set inside an archive has to go through
                // CCDSharp exactly as a loose one does.
                converted = await ConvertCcdViaCueAsync(
                    chdmanPath,
                    extractedFile,
                    extractedFileOutputChd,
                    tempDirs,
                    outputFolder,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    token
                );
            }
            else if (
                extractedExt.Equals(FileExtensions.Mds, StringComparison.OrdinalIgnoreCase)
                || extractedExt.Equals(FileExtensions.Mdx, StringComparison.OrdinalIgnoreCase)
            )
            {
                // Same for an Alcohol set: the descriptor has to become a cue first.
                converted = await ConvertMdsViaCueAsync(
                    chdmanPath,
                    extractedFile,
                    extractedFileOutputChd,
                    tempDirs,
                    outputFolder,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    token
                );
            }
            else if (extractedExt.Equals(FileExtensions.Isz, StringComparison.OrdinalIgnoreCase))
            {
                // An archived ISZ has to be decompressed before anything can read it. It is treated
                // the same as a loose one, including the case where it is an ordinary image that was
                // merely given the extension.
                converted = await ConvertIszViaImageAsync(
                    chdmanPath,
                    extractedFile,
                    extractedFileOutputChd,
                    tempDirs,
                    outputFolder,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    token
                );
            }
            else
            {
                converted = await ConvertToChdAsync(
                    chdmanPath,
                    extractedFile,
                    extractedFileOutputChd,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    token
                );
            }

            if (!converted && BinCueGenerator.IsAutoCue(extractedFile))
            {
                // The auto-generated cue guessed the track mode; retry once with the alternate
                // mode (MODE2/2352 <-> MODE1/2352) before giving up.
                var mode = await BinCueGenerator.ReadTrackModeAsync(extractedFile, token);
                var alternateMode = BinCueGenerator.GetAlternateMode(mode);
                await BinCueGenerator.RewriteCueAsync(extractedFile, alternateMode, token);
                LogMessage(
                    $"Auto-generated cue failed with {mode}; retrying with {alternateMode}..."
                );
                converted = await ConvertToChdAsync(
                    chdmanPath,
                    extractedFile,
                    extractedFileOutputChd,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    token
                );
            }

            if (!converted) allSucceeded = false;
        }

        if (allSucceeded && deleteOriginal)
            await TryDeleteFileAsync(inputFile, "original archive", token);

        return allSucceeded;
    }

    /// <summary>
    ///     Extracts a PBP file's discs to cue/bin pairs and converts each disc to CHD, handling
    ///     mislabelled PBPs.
    /// </summary>
    /// <param name="inputFile">Full path of the PBP file.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="cores">Worker threads to give the encoder.</param>
    /// <param name="forceCd">Whether to force the createcd verb.</param>
    /// <param name="forceDvd">Whether to force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout in minutes, or null for none.</param>
    /// <param name="deleteOriginal">Whether to delete the source after a successful conversion.</param>
    /// <returns>True when every extracted disc was converted successfully.</returns>
    private async Task<bool> ProcessPbpFileForConversionAsync(
        string inputFile,
        string originalName,
        string inputFolder,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token,
        string chdmanPath,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        bool deleteOriginal
    )
    {
        long pbpSize = 0;
        try
        {
            pbpSize = new FileInfo(inputFile).Length;
        }
        catch
        {
            /* ignored */
        }

        var tempDir = PathUtils.GetBestTempDirectory(
            inputFile,
            outputFolder,
            TempDirPrefix,
            pbpSize
        );
        tempDirs.Add(tempDir);
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);

        var result = await ExtractPbpToCueBinAsync(inputFile, tempDir, LogMessage, token);
        if (!result.Success || result.CueFilePaths.Count == 0)
        {
            // A file whose PBP magic is wrong is not a PBP at all. Route it by what its bytes
            // actually are, so a mislabelled archive or disc image still converts.
            if (result.ErrorCode == PbpError.InvalidHeader)
            {
                var mislabelled = await TryConvertMislabelledPbpAsync(
                    inputFile,
                    originalName,
                    inputFolder,
                    outputFolder,
                    tempDirs,
                    token,
                    chdmanPath,
                    cores,
                    forceCd,
                    forceDvd,
                    timeoutMinutes,
                    deleteOriginal
                );
                if (mislabelled is not null)
                    return mislabelled.Value;
            }

            // PSP homebrew / application EBOOT.PBPs have no PlayStation disc image to convert,
            // and a truncated PSX eboot has no readable index. Both are user-data conditions,
            // so they are reported to the user without raising a bug report.
            if (result.ErrorCode is PbpError.InvalidPsarHeader or PbpError.TruncatedPsar)
            {
                LogMessage(
                    result.ErrorCode == PbpError.TruncatedPsar
                        ? $" {originalName} does not contain a complete PlayStation disc image (the PBP is truncated or incomplete) — skipping. Re-download the file."
                        : $" {originalName} does not contain a PlayStation disc image (PSP application, unsupported variant, or corrupt file) — skipping."
                );
            }
            else
            {
                var errorDetail = string.IsNullOrWhiteSpace(result.Error)
                    ? string.Empty
                    : $" - {result.Error}";
                var sizeDetail = pbpSize > 0 ? $" ({pbpSize:N0} bytes)" : string.Empty;
                LogError($" Failed to extract PBP file: {originalName}{sizeDetail}{errorDetail}");

                switch (result.ErrorCode)
                {
                    case PbpError.InvalidHeader:
                        LogMessage(
                            "       The file does not begin with a PBP header - it is not a PlayStation EBOOT."
                        );
                        break;
                    case PbpError.CorruptFile or PbpError.DecompressionError or PbpError.InvalidSfo:
                        LogMessage(
                            "       The file may be truncated or corrupt (re-download it), or it may not be a PSX PBP."
                        );
                        break;
                    case PbpError.IoError:
                        LogMessage(
                            "       The file could not be read - close any program using it and check the drive for errors."
                        );
                        break;
                }
            }

            return false;
        }

        var allSucceeded = true;
        foreach (var cueFile in result.CueFilePaths)
        {
            token.ThrowIfCancellationRequested();
            var cueFileOutputChd = ComputeOutputChdPathForExtractedFile(
                cueFile,
                tempDir,
                inputFile,
                inputFolder,
                outputFolder
            );
            var cueOutputDir = Path.GetDirectoryName(cueFileOutputChd) ?? outputFolder;
            if (!Directory.Exists(cueOutputDir))
                Directory.CreateDirectory(cueOutputDir);

            if (result.CueFilePaths.Count > 1)
                LogMessage($"Converting disc: {Path.GetFileName(cueFile)}");

            var converted = await ConvertToChdAsync(
                chdmanPath,
                cueFile,
                cueFileOutputChd,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                token
            );
            if (!converted) allSucceeded = false;
        }

        if (allSucceeded && deleteOriginal)
            await TryDeleteFileAsync(inputFile, "original PBP", token);

        return allSucceeded;
    }

    /// <summary>
    ///     Handles a .pbp whose content is not a PBP at all: an archive is extracted, a CSO is
    ///     decompressed, an ISZ/ECM is restored, and a plain disc image is converted directly.
    ///     Returns null when the content is not a format the app knows, so the caller keeps the
    ///     original PBP error report.
    /// </summary>
    /// <param name="inputFile">The mislabelled .pbp input.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="chdmanPath">Path of chdman.exe.</param>
    /// <param name="cores">Worker threads to give chdman.</param>
    /// <param name="forceCd">Force the CD verb.</param>
    /// <param name="forceDvd">Force the DVD verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout, or null for none.</param>
    /// <param name="deleteOriginal">Whether to delete the input after a successful conversion.</param>
    private async Task<bool?> TryConvertMislabelledPbpAsync(
        string inputFile,
        string originalName,
        string inputFolder,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token,
        string chdmanPath,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        bool deleteOriginal
    )
    {
        var kind = DiscImageSignature.Detect(inputFile);
        if (kind is DiscImageKind.Pbp or DiscImageKind.Unknown) return null;

        var outputChd = ComputeOutputChdPath(inputFile, inputFolder, outputFolder);

        if (DiscImageSignature.IsArchive(kind))
        {
            LogMessage(
                $" {originalName} is named .pbp but contains {DiscImageSignature.Describe(kind)}; extracting it instead."
            );
            return await ProcessArchiveFileForConversionAsync(
                inputFile,
                inputFolder,
                outputFolder,
                tempDirs,
                token,
                chdmanPath,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                deleteOriginal
            );
        }

        if (kind == DiscImageKind.Cso)
        {
            LogMessage(
                $" {originalName} is named .pbp but contains {DiscImageSignature.Describe(kind)}; decompressing it instead."
            );
            return await ProcessCsoFileForConversionAsync(
                inputFile,
                originalName,
                outputFolder,
                tempDirs,
                token,
                chdmanPath,
                outputChd,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                deleteOriginal,
                inputFolder
            );
        }

        var resolved = kind switch
        {
            DiscImageKind.Ecm => await ResolveEcmAsync(
                inputFile,
                originalName,
                outputFolder,
                tempDirs,
                token
            ),
            DiscImageKind.Isz => await ResolveIszAsync(
                inputFile,
                originalName,
                outputFolder,
                tempDirs,
                token
            ),
            DiscImageKind.Chd => ResolvedInput.Skip(
                "this file is already a CHD. Copy it to the output folder rather than converting it."
            ),
            _ => await ResolveMislabelledContainerAsync(
                inputFile,
                originalName,
                "a PlayStation PBP",
                kind,
                tempDirs,
                token
            )
        };

        if (resolved.SkipReason is not null)
        {
            LogWarning($" {originalName}: {resolved.SkipReason}");
            return false;
        }

        if (resolved.PathToConvert is null)
        {
            LogWarning($" {originalName}: resolved path is null; skipping.");
            return false;
        }

        var outputDir = Path.GetDirectoryName(outputChd) ?? outputFolder;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        UpdateWriteSpeedDisplay(0);
        var success = await ConvertToChdAsync(
            chdmanPath,
            resolved.PathToConvert,
            outputChd,
            cores,
            forceCd,
            resolved.ForceDvd || forceDvd,
            timeoutMinutes,
            token
        );

        return await HandleConversionResultAsync(
            success,
            inputFile,
            originalName,
            FileExtensions.Pbp,
            inputFolder,
            outputChd,
            deleteOriginal,
            token
        );
    }

    /// <summary>Converts a CloneCD (.ccd) set to CHD and optionally deletes the source files.</summary>
    /// <param name="inputFile">Full path of the .ccd descriptor.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="cores">Worker threads to give the encoder.</param>
    /// <param name="forceCd">Whether to force the createcd verb.</param>
    /// <param name="forceDvd">Whether to force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout in minutes, or null for none.</param>
    /// <param name="deleteOriginal">Whether to delete the source files after a successful conversion.</param>
    /// <returns>True when the file was converted successfully.</returns>
    private async Task<bool> ProcessCcdFileForConversionAsync(
        string inputFile,
        string inputFolder,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token,
        string chdmanPath,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        bool deleteOriginal
    )
    {
        long imgSize = 0;
        DiscImage? parsedDisc = null;
        try
        {
            parsedDisc = CcdConverter.Parse(inputFile);
            if (parsedDisc.ImgFilePath != null && File.Exists(parsedDisc.ImgFilePath))
                imgSize = new FileInfo(parsedDisc.ImgFilePath).Length;
        }
        catch
        {
            /* ignored - will fail later with a proper error */
        }

        var tempDir = PathUtils.GetBestTempDirectory(
            inputFile,
            outputFolder,
            TempDirPrefix,
            imgSize
        );
        tempDirs.Add(tempDir);
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);

        try
        {
            var cueFileOutputChd = ComputeOutputChdPath(inputFile, inputFolder, outputFolder);
            var cueOutputDir = Path.GetDirectoryName(cueFileOutputChd) ?? outputFolder;
            if (!Directory.Exists(cueOutputDir))
                Directory.CreateDirectory(cueOutputDir);

            var converted = await ConvertCcdInTempDirAsync(
                chdmanPath,
                inputFile,
                cueFileOutputChd,
                tempDir,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                token
            );
            if (!converted) return false;

            if (deleteOriginal)
            {
                // Parse the CCD before deleting it (if we didn't parse it earlier)
                parsedDisc ??= CcdConverter.Parse(inputFile);

                await TryDeleteFileAsync(inputFile, "original CCD", token);

                if (parsedDisc.ImgFilePath != null)
                    await TryDeleteReferencedFileAsync(
                        parsedDisc.ImgFilePath,
                        "original IMG",
                        inputFolder,
                        token
                    );
                if (parsedDisc.SubFilePath != null)
                    await TryDeleteReferencedFileAsync(
                        parsedDisc.SubFilePath,
                        "original SUB",
                        inputFolder,
                        token
                    );

                var cdtPath = Path.ChangeExtension(inputFile, ".cdt");
                if (File.Exists(cdtPath))
                    await TryDeleteReferencedFileAsync(cdtPath, "original CDT", inputFolder, token);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is FileNotFoundException)
            {
                LogError(
                    $"CCDSharp: Conversion error - {ex.Message}. Ensure the .img file exists alongside the .ccd file with the same base name."
                );
            }
            else
            {
                LogError($"CCDSharp: Conversion error - {ex.Message}");
            }

            return false;
        }
    }

    /// <summary>
    ///     Estimates the bytes the MDS preparation step writes to its work directory: the decoded
    ///     track data for v2/MDX images, or a full copy of the source data when subchannel stripping,
    ///     pregap rebuilding or joining several data files needs one.
    /// </summary>
    /// <param name="disc">The parsed Alcohol descriptor.</param>
    /// <returns>The required bytes, or 0 when preparation works in place.</returns>
    private static long EstimateMdsWorkBytes(MdsDisc disc)
    {
        if (disc.HasEncryptedTrackData || disc.HasCompressedTrackData || disc.IsMdxContainer)
        {
            return disc.Tracks.Sum(static t => t.LengthSectors * t.SectorSize);
        }

        if (
            disc is { MdfPath: not null }
            && (disc.NeedsSubchannelStrip || disc.HasPregapInfo || disc.DataFilePaths.Count > 1)
        )
        {
            try
            {
                return disc.DataFilePaths.Count > 0
                    ? disc.DataFilePaths.Sum(f => new FileInfo(f).Length)
                    : new FileInfo(disc.MdfPath).Length;
            }
            catch
            {
                /* ignored */
            }
        }

        return 0;
    }

    /// <summary>
    ///     Converts an Alcohol 120% .mds/.mdf pair. chdman cannot read either file, so the descriptor's
    ///     track table is turned into a cue and, when the sectors carry subchannel data, the image is
    ///     repacked to plain 2352-byte sectors first.
    /// </summary>
    /// <param name="inputFile">Path of the .mds descriptor.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="chdmanPath">Path to chdman.exe.</param>
    /// <param name="cores">Processor count passed to chdman.</param>
    /// <param name="forceCd">Force the createcd verb.</param>
    /// <param name="forceDvd">Force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout, or null for none.</param>
    /// <param name="deleteOriginal">Delete the source files after a successful conversion.</param>
    private async Task<bool> ProcessMdsFileForConversionAsync(
        string inputFile,
        string originalName,
        string inputFolder,
        string outputFolder,
        List<string> tempDirs,
        CancellationToken token,
        string chdmanPath,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        bool deleteOriginal
    )
    {
        MdsDisc disc;
        try
        {
            disc = await Task.Run(() => MdsParser.Parse(inputFile), token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError($" {originalName} could not be read as an Alcohol descriptor: {ex.Message}");
            return false;
        }

        LogMessage($"MDS: {originalName} - {disc.Summary}");

        // Stripping subchannel data, rebuilding pregaps or joining several data files writes a whole
        // second copy of the disc, so the work directory has to be chosen with room for it.
        var requiredBytes = EstimateMdsWorkBytes(disc);

        var tempDir = PathUtils.GetBestTempDirectory(
            inputFile,
            outputFolder,
            TempDirPrefix,
            requiredBytes
        );
        tempDirs.Add(tempDir);
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);

        MdsInputPreparer.Result prepared;
        try
        {
            prepared = await MdsInputPreparer.PrepareAsync(disc, tempDir, LogMessage, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsDiskSpaceException(ex))
            {
                LogError(
                    $" Not enough disk space to repack {originalName}. Free up space and try again."
                );
            }
            else
            {
                LogError($" Failed to prepare {originalName} for conversion: {ex.Message}", ex);
            }

            return false;
        }

        if (!prepared.Success)
        {
            LogError($" {originalName} cannot be converted: {prepared.FailureReason}.");
            if (
                prepared.FailureReason?.Contains("password", StringComparison.OrdinalIgnoreCase)
                == true
            )
            {
                LogMessage(
                    "       Password-protected MDS v2/MDX images cannot be prompted for; re-save the image without a password first."
                );
            }

            return false;
        }

        var outputChd = ComputeOutputChdPath(inputFile, inputFolder, outputFolder);
        var outputDir = Path.GetDirectoryName(outputChd) ?? outputFolder;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        UpdateWriteSpeedDisplay(0);

        // A 2048-byte-sector .mdf is an ISO in all but name, so it is converted as a DVD image.
        var success = prepared.DvdImagePath is not null
            ? await ConvertToChdAsync(
                chdmanPath,
                prepared.DvdImagePath,
                outputChd,
                cores,
                false,
                true,
                timeoutMinutes,
                token
            )
            : await ConvertToChdAsync(
                chdmanPath,
                prepared.CuePath!,
                outputChd,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                token
            );

        if (!success)
        {
            if (deleteOriginal)
            {
                LogMessage(
                    $"KEEPING source: {originalName} (Conversion failed, skipping deletion for safety)"
                );
            }

            return false;
        }

        LogMessage($"Converted: {originalName}");

        if (deleteOriginal)
        {
            LogMessage($"Deleting source: {originalName} (Option 'Delete originals' is enabled)");
            await TryDeleteFileAsync(inputFile, "original MDS", token);

            // Every file the descriptor names is a source, and a first volume stands for its whole
            // set; deleting only the first would leave the rest of the image behind.
            var dataFiles = disc.DataFilePaths.Count > 0
                ? disc.DataFilePaths
                : disc.MdfPath is not null
                    ? [disc.MdfPath]
                    : [];
            var sources = new List<string>();
            foreach (var dataFile in dataFiles)
            {
                var volumeSet = SplitImageJoiner.TryGetVolumeSet(dataFile);
                if (volumeSet is null) sources.Add(dataFile);
                else sources.AddRange(volumeSet);
            }

            foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
                await TryDeleteReferencedFileAsync(source, "original MDF", inputFolder, token);

            var subfolder = Path.GetDirectoryName(inputFile);
            if (!string.IsNullOrEmpty(subfolder))
                await TryDeleteEmptySubfolderAsync(subfolder, inputFolder, token);
        }

        return true;
    }

    /// <summary>
    ///     Converts an ISZ found inside an archive, by the same route a loose one takes: decompress it,
    ///     or recognise that it is an ordinary image wearing the extension, then convert the result.
    /// </summary>
    /// <param name="chdmanPath">Path of chdman.exe.</param>
    /// <param name="iszPath">The extracted .isz file.</param>
    /// <param name="outputChd">Destination CHD path.</param>
    /// <param name="tempDirs">Temp directories to clean up when the archive is done.</param>
    /// <param name="outputFolder">Conversion output folder, used to pick a temp location.</param>
    /// <param name="cores">Worker threads to give chdman.</param>
    /// <param name="forceCd">Force the CD verb.</param>
    /// <param name="forceDvd">Force the DVD verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout, or null for none.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<bool> ConvertIszViaImageAsync(
        string chdmanPath,
        string iszPath,
        string outputChd,
        List<string> tempDirs,
        string outputFolder,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        CancellationToken token
    )
    {
        var name = Path.GetFileName(iszPath);

        var kind = DiscImageSignature.Detect(iszPath);

        var resolved =
            kind == DiscImageKind.Isz
                ? await ResolveIszAsync(iszPath, name, outputFolder, tempDirs, token)
                : await ResolveMislabelledContainerAsync(
                    iszPath,
                    name,
                    IszContainerDescription,
                    kind,
                    tempDirs,
                    token
                );

        if (resolved.SkipReason is not null)
        {
            LogWarning($" {name}: {resolved.SkipReason}");
            return false;
        }

        return await ConvertToChdAsync(
            chdmanPath,
            resolved.PathToConvert!,
            outputChd,
            cores,
            forceCd,
            resolved.ForceDvd || forceDvd,
            timeoutMinutes,
            token
        );
    }

    /// <summary>
    ///     Handles a file whose extension promises a container - an archive, or a compressed ISZ - but
    ///     which holds an ordinary image. The image is converted where it lies, with a generated cue
    ///     when it is raw CD sectors.
    /// </summary>
    /// <param name="imagePath">The image with the misleading extension.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="claimed">What the extension claims, for the log message.</param>
    /// <param name="kind">What the content was detected as.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<ResolvedInput> ResolveMislabelledContainerAsync(
        string imagePath,
        string originalName,
        string claimed,
        DiscImageKind kind,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        var trackMode = RawCdImageDetector.DetectTrackMode(imagePath);
        if (trackMode is not null)
        {
            LogMessage(
                $" {originalName} is named as {claimed} but contains {DiscImageSignature.Describe(kind)} ({trackMode}); converting it as a CD."
            );
            var cuePath = await StageCueForImageAsync(
                imagePath,
                originalName,
                trackMode,
                tempDirs,
                token
            );

            return cuePath is not null
                ? ResolvedInput.Convert(cuePath, false)
                : ResolvedInput.Skip(
                    "could not place a generated cue on the same volume as the image."
                );
        }

        if (IsCookedImageSize(imagePath))
        {
            LogMessage(
                $" {originalName} is named as {claimed} but contains a disc image; converting it as a DVD image."
            );
            return ResolvedInput.Convert(imagePath, true);
        }

        return ResolvedInput.Skip(
            $"the extension says {claimed} but the content is {DiscImageSignature.Describe(kind)}, and it is not a usable disc image. The download is probably incomplete."
        );
    }

    /// <summary>
    ///     Converts an extracted Alcohol .mds set by preparing it and converting the resulting cue or
    ///     image.
    /// </summary>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="mdsPath">Path of the .mds descriptor.</param>
    /// <param name="outputChd">Destination CHD path.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <param name="cores">Worker threads to give the encoder.</param>
    /// <param name="forceCd">Whether to force the createcd verb.</param>
    /// <param name="forceDvd">Whether to force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout in minutes, or null for none.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True when the file was converted successfully.</returns>
    private async Task<bool> ConvertMdsViaCueAsync(
        string chdmanPath,
        string mdsPath,
        string outputChd,
        List<string> tempDirs,
        string outputFolder,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        CancellationToken token
    )
    {
        MdsDisc disc;
        try
        {
            disc = await Task.Run(() => MdsParser.Parse(mdsPath), token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError(
                $" {Path.GetFileName(mdsPath)} could not be read as an Alcohol descriptor: {ex.Message}"
            );
            return false;
        }

        LogMessage($"MDS: {Path.GetFileName(mdsPath)} - {disc.Summary}");

        var requiredBytes = EstimateMdsWorkBytes(disc);

        var tempDir = PathUtils.GetBestTempDirectory(
            mdsPath,
            outputFolder,
            TempDirPrefix,
            requiredBytes
        );
        tempDirs.Add(tempDir);
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);

        MdsInputPreparer.Result prepared;
        try
        {
            prepared = await MdsInputPreparer.PrepareAsync(disc, tempDir, LogMessage, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError(
                $" Failed to prepare {Path.GetFileName(mdsPath)} for conversion: {ex.Message}",
                ex
            );
            return false;
        }

        if (!prepared.Success)
        {
            LogError(
                $" {Path.GetFileName(mdsPath)} cannot be converted: {prepared.FailureReason}."
            );
            if (
                prepared.FailureReason?.Contains("password", StringComparison.OrdinalIgnoreCase)
                == true
            )
            {
                LogMessage(
                    "       Password-protected MDS v2/MDX images cannot be prompted for; re-save the image without a password first."
                );
            }

            return false;
        }

        return prepared.DvdImagePath is not null
            ? await ConvertToChdAsync(
                chdmanPath,
                prepared.DvdImagePath,
                outputChd,
                cores,
                false,
                true,
                timeoutMinutes,
                token
            )
            : await ConvertToChdAsync(
                chdmanPath,
                prepared.CuePath!,
                outputChd,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                token
            );
    }

    /// <summary>
    ///     Converts a CloneCD set by generating a cue for it in a fresh temp directory. Used for both
    ///     loose .ccd files and .ccd files extracted from an archive, since chdman cannot read a .ccd.
    /// </summary>
    /// <param name="chdmanPath">Path to chdman.exe.</param>
    /// <param name="ccdPath">Path of the .ccd descriptor.</param>
    /// <param name="outputChd">Destination CHD path.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="outputFolder">Conversion output folder, used to pick a temp location.</param>
    /// <param name="cores">Processor count passed to chdman.</param>
    /// <param name="forceCd">Force the createcd verb.</param>
    /// <param name="forceDvd">Force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout, or null for none.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task<bool> ConvertCcdViaCueAsync(
        string chdmanPath,
        string ccdPath,
        string outputChd,
        List<string> tempDirs,
        string outputFolder,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        CancellationToken token
    )
    {
        var tempDir = PathUtils.GetBestTempDirectory(ccdPath, outputFolder, TempDirPrefix);
        tempDirs.Add(tempDir);
        await Task.Run(() => Directory.CreateDirectory(tempDir), token);

        try
        {
            return await ConvertCcdInTempDirAsync(
                chdmanPath,
                ccdPath,
                outputChd,
                tempDir,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                token
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError($"CCDSharp: Conversion error - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Writes a cue for <paramref name="ccdPath" /> into <paramref name="tempDir" /> and converts it.
    ///     The .img is referenced from the cue rather than copied, so no extra disc-sized write happens.
    /// </summary>
    private async Task<bool> ConvertCcdInTempDirAsync(
        string chdmanPath,
        string ccdPath,
        string outputChd,
        string tempDir,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        CancellationToken token
    )
    {
        LogMessage($"CCDSharp: Converting {Path.GetFileName(ccdPath)}");

        var tempCuePath = Path.Combine(
            tempDir,
            Path.GetFileNameWithoutExtension(ccdPath) + FileExtensions.Cue
        );
        await Task.Run(() => CcdConverter.ConvertToCueBin(ccdPath, tempCuePath), token);

        return await ConvertToChdAsync(
            chdmanPath,
            tempCuePath,
            outputChd,
            cores,
            forceCd,
            forceDvd,
            timeoutMinutes,
            token
        );
    }

    /// <summary>Checks that the files referenced by a cue, gdi or toc descriptor exist next to it.</summary>
    /// <param name="ext">Descriptor extension.</param>
    /// <param name="inputFile">Path of the descriptor.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True when all referenced files are present.</returns>
    private async Task<bool> ValidateDependentFilesAsync(
        string ext,
        string inputFile,
        string originalName,
        CancellationToken token
    )
    {
        var normalizedExt = ext.ToLowerInvariant();
        if (normalizedExt is not (FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc))
            return true;

        try
        {
            var missingNames = await GetMissingDependentFileNamesAsync(
                normalizedExt,
                inputFile,
                token
            );
            if (missingNames.Count > 0)
            {
                LogWarning(
                    $" {originalName} — referenced files are missing: {string.Join(", ", missingNames)}"
                );
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWarning($" {originalName} — could not validate referenced files: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Returns the names of files referenced by a .cue/.gdi/.toc descriptor that cannot be
    ///     resolved next to the descriptor. For cue files this uses the normalizer's resolution
    ///     (exact → case-insensitive → zero-padding-tolerant), for gdi/toc a plain existence check.
    /// </summary>
    private async Task<List<string>> GetMissingDependentFileNamesAsync(
        string ext,
        string filePath,
        CancellationToken token
    )
    {
        ext = ext.ToLowerInvariant();
        if (string.Equals(ext, FileExtensions.Cue, StringComparison.Ordinal))
        {
            var normalization = await CueNormalizer
                .NormalizeAsync(filePath, token)
                .ConfigureAwait(false);
            return [.. normalization.UnresolvedNames];
        }

        var referencedFiles = string.Equals(ext, FileExtensions.Gdi, StringComparison.Ordinal)
            ? await GameFileParser
                .GetReferencedFilesFromGdiAsync(filePath, LogMessage, token)
                .ConfigureAwait(false)
            : await GameFileParser
                .GetReferencedFilesFromTocAsync(filePath, LogMessage, token)
                .ConfigureAwait(false);

        return referencedFiles
            .Where(static f => !File.Exists(f))
            .Select(static f => Path.GetFileName(f))
            .ToList();
    }

    /// <summary>
    ///     True when the cue references any MP3 audio track. chdman cannot consume MP3 tracks, so
    ///     such cues must go through the MP3→WAV work-directory preparation; if that preparation
    ///     fails, running chdman on the raw cue would only produce a misleading error.
    /// </summary>
    private static async Task<bool> CueHasMp3TracksAsync(string cuePath, CancellationToken token)
    {
        try
        {
            var normalization = await CueNormalizer
                .NormalizeAsync(cuePath, token)
                .ConfigureAwait(false);
            return normalization.References.Any(static r =>
                string.Equals(r.TrackType, "MP3", StringComparison.Ordinal)
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs a direct conversion and reports classified errors instead of letting them propagate.</summary>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="fileToProcess">File to hand to the encoder.</param>
    /// <param name="outputChd">Destination CHD path.</param>
    /// <param name="cores">Worker threads to give the encoder.</param>
    /// <param name="forceCd">Whether to force the createcd verb.</param>
    /// <param name="forceDvd">Whether to force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout in minutes, or null for none.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <returns>True when the conversion succeeded.</returns>
    private async Task<bool> TryDirectConversionAsync(
        string chdmanPath,
        string fileToProcess,
        string outputChd,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        CancellationToken token,
        string originalName
    )
    {
        try
        {
            return await ConvertToChdAsync(
                chdmanPath,
                fileToProcess,
                outputChd,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                token
            );
        }
        catch (Exception ex) when (!IsCancellationException(ex))
        {
            if (IsDiskSpaceException(ex))
            {
                LogError(
                    $" Not enough disk space to convert {originalName}. Free up disk space and try again."
                );
            }
            else if (ex is DriveNotFoundException or DirectoryNotFoundException)
            {
                LogError(
                    $" Cannot convert {originalName}: the output folder is not available ({ex.Message})."
                );
            }
            else
            {
                LogError($"Direct conversion attempt error for {originalName}: {ex.Message}", ex);
            }

            return false;
        }
    }

    /// <summary>Retries a failed conversion from a temp copy of the input and its referenced files.</summary>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="inputFile">Full path of the input file.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="ext">Extension of the input file.</param>
    /// <param name="outputFolder">Root of the conversion output folder.</param>
    /// <param name="outputChd">Destination CHD path.</param>
    /// <param name="cores">Worker threads to give the encoder.</param>
    /// <param name="forceCd">Whether to force the createcd verb.</param>
    /// <param name="forceDvd">Whether to force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout in minutes, or null for none.</param>
    /// <param name="tempDirs">Temp directories to clean up when the file is done.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True when the retry converted the file successfully.</returns>
    private async Task<bool> TryRetryConversionViaTempCopyAsync(
        string chdmanPath,
        string inputFile,
        string originalName,
        string ext,
        string outputFolder,
        string outputChd,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        List<string> tempDirs,
        CancellationToken token
    )
    {
        LogMessage(
            $"Direct conversion failed for {originalName}. Retrying via temporary directory copy..."
        );

        ext = ext.ToLowerInvariant();

        try
        {
            List<string> filesToCopy;
            if (ext is FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc)
            {
                filesToCopy = [inputFile];
                switch (ext)
                {
                    case FileExtensions.Cue:
                        filesToCopy.AddRange(
                            await GameFileParser.GetReferencedFilesFromCueAsync(
                                inputFile,
                                LogMessage,
                                token
                            )
                        );
                        break;
                    case FileExtensions.Gdi:
                        filesToCopy.AddRange(
                            await GameFileParser.GetReferencedFilesFromGdiAsync(
                                inputFile,
                                LogMessage,
                                token
                            )
                        );
                        break;
                    default:
                        filesToCopy.AddRange(
                            await GameFileParser.GetReferencedFilesFromTocAsync(
                                inputFile,
                                LogMessage,
                                token
                            )
                        );
                        break;
                }

                var missingFiles = filesToCopy
                    .Distinct(StringComparer.Ordinal)
                    .Where(static f => !File.Exists(f))
                    .ToList();
                if (missingFiles.Count > 0)
                {
                    var missingNames = string.Join(", ", missingFiles.Select(Path.GetFileName));
                    LogWarning(
                        $" Skipping temp retry for {originalName} because referenced files are missing: {missingNames}"
                    );
                    return false;
                }
            }
            else
            {
                filesToCopy = [inputFile];
            }

            long totalBytesNeeded = 0;
            foreach (var file in filesToCopy.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    totalBytesNeeded += new FileInfo(file).Length;
                }
                catch
                {
                    /* skip */
                }
            }

            var tempDir = PathUtils.GetBestTempDirectory(
                inputFile,
                outputFolder,
                TempDirPrefix,
                totalBytesNeeded
            );
            tempDirs.Add(tempDir);
            await Task.Run(() => Directory.CreateDirectory(tempDir), token);

            try
            {
                var tempDriveRoot = Path.GetPathRoot(tempDir);
                if (!string.IsNullOrEmpty(tempDriveRoot))
                {
                    var tempDrive = new DriveInfo(tempDriveRoot);
                    if (tempDrive.IsReady && tempDrive.AvailableFreeSpace < totalBytesNeeded)
                    {
                        var availableGb = tempDrive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                        var neededGb = totalBytesNeeded / (1024.0 * 1024.0 * 1024.0);
                        LogError(
                            $" Not enough disk space for temp copy of {originalName}. Need {neededGb:F1} GB but only {availableGb:F1} GB available on {tempDriveRoot.TrimEnd('\\')}."
                        );
                        return false;
                    }
                }
            }
            catch
            {
                /* proceed */
            }

            string tempInputFile;
            if (ext is FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc)
            {
                LogMessage("Copying game with dependencies to temporary directory...");
                foreach (var file in filesToCopy.Distinct(StringComparer.Ordinal))
                {
                    var destPath = Path.Combine(tempDir, Path.GetFileName(file));
                    await CopyFileWithRetryAsync(file, destPath, token);
                }

                tempInputFile = Path.Combine(tempDir, Path.GetFileName(inputFile));

                // chdman's cue parser does not skip a UTF-8 BOM (it produces "couldn't find bin
                // file []"); the temp copy is ours, so strip the BOM in place to surface the real
                // conversion error instead of the confusing empty-bin failure.
                if (ext is FileExtensions.Cue or FileExtensions.Toc)
                    await StripUtf8BomIfPresentAsync(tempInputFile, token);
            }
            else
            {
                tempInputFile = Path.Combine(tempDir, originalName);
                await CopyFileWithRetryAsync(inputFile, tempInputFile, token);
            }

            return await ConvertToChdAsync(
                chdmanPath,
                tempInputFile,
                outputChd,
                cores,
                forceCd,
                forceDvd,
                timeoutMinutes,
                token
            );
        }
        catch (Exception ex) when (!IsCancellationException(ex))
        {
            if (IsDiskSpaceException(ex))
            {
                LogError(
                    $" Not enough disk space to convert {originalName} (via temp). Free up disk space and try again."
                );
            }
            else if (IsCorruptionException(ex) || IsCrcErrorException(ex))
            {
                LogError($" Source file appears to be corrupt: {originalName}");
            }
            else
            {
                LogError($"Retry via temp failed for {originalName}: {ex.Message}", ex);
            }

            return false;
        }
    }

    /// <summary>
    ///     Logs the conversion outcome and deletes the source files when requested and successful.
    /// </summary>
    /// <param name="success">Whether the conversion succeeded.</param>
    /// <param name="inputFile">Full path of the input file.</param>
    /// <param name="originalName">File name used in log messages.</param>
    /// <param name="ext">Extension of the input file.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="outputChd">Destination CHD path.</param>
    /// <param name="deleteOriginal">Whether to delete the source after a successful conversion.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True when the conversion succeeded.</returns>
    private async Task<bool> HandleConversionResultAsync(
        bool success,
        string inputFile,
        string originalName,
        string ext,
        string inputFolder,
        string outputChd,
        bool deleteOriginal,
        CancellationToken token
    )
    {
        if (success)
        {
            LogMessage($"Converted: {originalName}");
            if (deleteOriginal)
            {
                LogMessage(
                    $"Deleting source: {originalName} (Option 'Delete originals' is enabled)"
                );

                if (
                    ext
                    is FileExtensions.Cue
                    or FileExtensions.Gdi
                    or FileExtensions.Toc
                    or FileExtensions.Ccd
                )
                {
                    await DeleteOriginalGameFilesAsync(inputFile, inputFolder, token);
                }
                else
                {
                    await TryDeleteFileAsync(inputFile, "original file", token);
                }

                var subfolder = Path.GetDirectoryName(inputFile);
                if (!string.IsNullOrEmpty(subfolder))
                    await TryDeleteEmptySubfolderAsync(subfolder, inputFolder, token);
            }

            return true;
        }

        if (deleteOriginal)
        {
            LogMessage(
                $"KEEPING source: {originalName} (Conversion failed, skipping deletion for safety)"
            );
        }

        // No delete at the destination. Conversions are staged and moved into place only on
        // success, so a failure leaves whatever was already there untouched - including a good
        // CHD produced by a different input that resolves to the same name.
        if (File.Exists(outputChd))
        {
            LogMessage(
                $"KEEPING existing output: {Path.GetFileName(outputChd)} (not produced by this attempt)"
            );
        }

        return false;
    }

    /// <summary>
    ///     Runs the verification batch and moves each file into the success or failed folder when
    ///     enabled.
    /// </summary>
    /// <param name="inputFolder">Root of the verification input folder.</param>
    /// <param name="includeSub">Whether subfolders were included in the scan.</param>
    /// <param name="moveSuccess">Whether verified files are moved into a success folder.</param>
    /// <param name="successFolder">Folder that receives verified files.</param>
    /// <param name="moveFailed">Whether failed files are moved into a failed folder.</param>
    /// <param name="failedFolder">Folder that receives failed files.</param>
    /// <param name="selectedFiles">Full paths of the CHD files to verify.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the batch has finished.</returns>
    private async Task PerformBatchVerificationAsync(
        string inputFolder,
        bool includeSub,
        bool moveSuccess,
        string successFolder,
        bool moveFailed,
        string failedFolder,
        string[] selectedFiles,
        CancellationToken token
    )
    {
        _totalFilesProcessed = selectedFiles.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} CHD files to verify.");
        if (_totalFilesProcessed == 0) return;

        // Create success/failed folders if needed
        if (moveSuccess && !string.IsNullOrEmpty(successFolder) && !Directory.Exists(successFolder))
            Directory.CreateDirectory(successFolder);

        if (moveFailed && !string.IsNullOrEmpty(failedFolder) && !Directory.Exists(failedFolder))
            Directory.CreateDirectory(failedFolder);

        await Dispatcher.UIThread.InvokeAsync(() =>
            ProgressBar.Maximum = _totalFilesProcessed
        );
        var processed = 0;
        ResetSpeedCounters();

        foreach (var file in selectedFiles)
        {
            token.ThrowIfCancellationRequested();

            // Show current file in text, but bar shows 'processed' (completed) count
            UpdateProgressDisplay(
                processed,
                _totalFilesProcessed,
                Path.GetFileName(file),
                "Verifying"
            );

            var success = await VerifyChdAsync(file, token);

            if (success)
            {
                LogMessage($"✓ Verified: {Path.GetFileName(file)}");
                Interlocked.Increment(ref _processedOkCount);

                // Move to success folder if option is enabled
                if (moveSuccess && !string.IsNullOrEmpty(successFolder))
                {
                    await MoveVerifiedFileAsync(
                        file,
                        successFolder,
                        inputFolder,
                        includeSub,
                        token
                    );
                }
            }
            else
            {
                LogMessage($"✗ Failed: {Path.GetFileName(file)}");
                Interlocked.Increment(ref _failedCount);

                // Move to failed folder if option is enabled
                if (moveFailed && !string.IsNullOrEmpty(failedFolder))
                    await MoveVerifiedFileAsync(file, failedFolder, inputFolder, includeSub, token);
            }

            processed++;
            UpdateProgressDisplay(
                processed,
                _totalFilesProcessed,
                Path.GetFileName(file),
                "Finishing"
            );
            UpdateStatsDisplay();
            UpdateProcessingTimeDisplay();
            UpdateReadSpeedFromPerformanceCounter();
        }
    }

    /// <summary>Moves a verified CHD into the target folder, preserving subfolders when requested.</summary>
    /// <param name="sourceFile">Full path of the CHD to move.</param>
    /// <param name="targetFolder">Folder that receives the file.</param>
    /// <param name="inputFolder">Root of the verification input folder.</param>
    /// <param name="includeSub">Whether to preserve the subfolder structure.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the file has been moved.</returns>
    private async Task MoveVerifiedFileAsync(
        string sourceFile,
        string targetFolder,
        string inputFolder,
        bool includeSub,
        CancellationToken token
    )
    {
        try
        {
            string destFile;
            if (includeSub)
            {
                // Maintain directory structure
                var relativePath = PathUtils.GetSafeRelativePath(
                    inputFolder,
                    Path.GetDirectoryName(sourceFile) ?? inputFolder
                );
                var targetSubDir = string.Equals(relativePath, ".", StringComparison.Ordinal)
                    ? targetFolder
                    : Path.Combine(targetFolder, relativePath);
                if (!Directory.Exists(targetSubDir)) Directory.CreateDirectory(targetSubDir);

                destFile = Path.Combine(targetSubDir, Path.GetFileName(sourceFile));
            }
            else
            {
                destFile = Path.Combine(targetFolder, Path.GetFileName(sourceFile));
            }

            // Delete destination if it already exists
            if (File.Exists(destFile))
            {
                var deleted = await RetryingFileOperations
                    .TryDeleteAsync(destFile, token)
                    .ConfigureAwait(false);
                if (!deleted) throw new IOException($"Could not delete existing destination '{destFile}'.");
            }

            // The file may still be held open (antivirus, file indexer) right after verification,
            // so retry transient lock failures before giving up.
            var moved = await RetryingFileOperations
                .TryMoveAsync(sourceFile, destFile, token)
                .ConfigureAwait(false);
            if (!moved)
            {
                throw new IOException(
                    $"Could not move '{sourceFile}' to '{destFile}' after retries."
                );
            }
        }
        catch (Exception ex)
        {
            // Log error but don't fail the verification
            LogError($"Failed to move file {sourceFile}", ex);
            UpdateStatusBarMessage("Failed to move a verified file");
            SafeFireAndForget(ReportBugAsync($"Failed to move file {sourceFile}", ex));
        }
    }

    /// <summary>
    ///     Extracts a CHD with the built-in reader, falling back to chdman, and optionally deletes the
    ///     source.
    /// </summary>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="chdFile">Full path of the CHD file.</param>
    /// <param name="inputFolder">Root of the extraction input folder.</param>
    /// <param name="outputFolder">Root of the extraction output folder.</param>
    /// <param name="deleteOriginal">Whether to delete the CHD after a successful extraction.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True when the CHD was extracted successfully.</returns>
    private async Task<bool> ExtractChdAsync(
        string chdmanPath,
        string chdFile,
        string inputFolder,
        string outputFolder,
        bool deleteOriginal,
        CancellationToken token
    )
    {
        var fileName = Path.GetFileNameWithoutExtension(chdFile);

        // Maintain directory structure if searching subfolders
        var relativePath = PathUtils.GetSafeRelativePath(
            inputFolder,
            Path.GetDirectoryName(chdFile) ?? inputFolder
        );
        var targetDir = string.Equals(relativePath, ".", StringComparison.Ordinal)
            ? outputFolder
            : Path.Combine(outputFolder, relativePath);
        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        // Get extraction type based on user-selected output format
        var extractCommand = await GetSelectedExtractCommandAsync(chdFile, token);

        // Determine output extension based on selection or detected command
        var outputExt = FileExtensions.Cue; // Default for extractcd
        if (ExtractGdiRadioButton.IsChecked == true)
        {
            outputExt = FileExtensions.Gdi;
        }
        else if (ExtractDvdRadioButton.IsChecked == true)
        {
            outputExt = FileExtensions.Iso;
        }
        else if (ExtractHdRadioButton.IsChecked == true)
        {
            outputExt = FileExtensions.Img;
        }
        else if (ExtractAutoRadioButton.IsChecked == true)
        {
            outputExt = extractCommand switch
            {
                "extractdvd" => FileExtensions.Iso,
                "extracthd" => FileExtensions.Img,
                _ => FileExtensions.Cue
            };

            if (
                string.Equals(extractCommand, "extractcd", StringComparison.Ordinal)
                && await IsGdiChdAsync(chdFile, token)
            )
            {
                outputExt = FileExtensions.Gdi;
            }
        }

        var outputFile = Path.Combine(targetDir, fileName + outputExt);

        // An extracted file takes the CHD's own base name, so extracting into a folder that already
        // holds a set of that name - most often the folder the CHD was made in - would replace it.
        // Rather than overwrite, or ask, the disc goes into a subfolder of its own name. This covers
        // cue/gdi sets (their BIN is checked too) as well as single files, and targetDir is moved
        // with the output so the chdman fallback cannot replace the existing set either.
        if (ExtractionOutputExists(targetDir, fileName, outputExt))
        {
            var isolatedDir = PathUtils.ReserveFreeSubdirectory(targetDir, fileName);
            Directory.CreateDirectory(isolatedDir);
            targetDir = isolatedDir;
            outputFile = Path.Combine(isolatedDir, fileName + outputExt);

            LogMessage(
                $" {fileName}{outputExt} already exists here; extracting into \"{Path.GetFileName(isolatedDir)}\" so the existing file is kept."
            );
        }

        var success = false;
        try
        {
            success = await Task.Run(
                async () =>
                {
                    var err = ChdFile.Open(chdFile, out var chd);
                    if (err != ChdError.Chderrnone || chd == null)
                    {
                        LogError(
                            $" Failed to open '{Path.GetFileName(chdFile)}': {err.GetMessage()}"
                        );
                        return false;
                    }

                    await using (chd)
                    {
                        try
                        {
                            if (extractCommand is "extractdvd" or "extracthd")
                            {
                                ExtractChdToSingleFile(chd, outputFile, token);
                            }
                            else
                            {
                                await ExtractChdTracksToDirectoryAsync(
                                    chd,
                                    chdFile,
                                    targetDir,
                                    fileName,
                                    token
                                );
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // Attach the CHD header fields that identify the image while it is
                            // still open, then surface the enriched failure to the handler below
                            // for the chdman fallback.
                            throw new InvalidDataException(
                                $"{ex.Message} [{BuildChdDiagnostics(chd)}]",
                                ex
                            );
                        }
                    }

                    return true;
                },
                token
            );
        }
        catch (OperationCanceledException)
        {
            if (extractCommand is "extractdvd" or "extracthd")
            {
                await TryDeleteFileAsync(
                    outputFile,
                    "partially extracted file",
                    CancellationToken.None
                );
            }

            throw;
        }
        catch (Exception ex)
        {
            if (IsDiskSpaceException(ex))
            {
                LogError(
                    $" Not enough disk space to extract '{Path.GetFileName(chdFile)}'. Free up disk space on the output drive and try again."
                );
            }
            else
            {
                // CHDSharp could not decode this CHD (corrupt file, A/V laserdisc CHD, or a
                // library limitation). Fall back to chdman, which supports every CHD variant
                // (extractcd/dvd/hd, plus extractld/extractraw for laserdisc CHDs). The
                // CHDSharp failure is reported only when chdman cannot extract it either - a
                // successful fallback means the extraction worked and is not an app bug. The
                // reason still goes to the user's log at informational level.
                var reason = GetChdExtractionErrorMessage(ex.Message);
                LogMessage(
                    $" Built-in reader could not extract '{Path.GetFileName(chdFile)}': {reason} Trying chdman..."
                );

                try
                {
                    var chdmanExtracted = await TryExtractWithChdmanAsync(
                            chdmanPath,
                            chdFile,
                            targetDir,
                            fileName,
                            extractCommand,
                            outputExt,
                            token
                        )
                        .ConfigureAwait(false);
                    if (chdmanExtracted)
                    {
                        LogMessage(
                            $" Extracted '{Path.GetFileName(chdFile)}' using chdman fallback (the built-in reader could not decode this CHD)."
                        );
                        success = true;
                    }
                    else
                    {
                        LogError(
                            $" Failed to extract '{Path.GetFileName(chdFile)}': {reason}",
                            ex
                        );
                        LogError(
                            $" chdman could not extract '{Path.GetFileName(chdFile)}' either. The file may be corrupt or use an unsupported codec."
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelled mid-fallback: still clean up partial direct-write output
                    // before propagating the cancellation.
                    if (extractCommand is "extractdvd" or "extracthd") TryBestEffortDelete(outputFile);

                    throw;
                }
            }

            if (!success && extractCommand is "extractdvd" or "extracthd")
            {
                await TryDeleteFileAsync(
                    outputFile,
                    "partially extracted file",
                    CancellationToken.None
                );
            }
        }

        if (success && deleteOriginal) await TryDeleteFileAsync(chdFile, "original CHD file", token);

        return success;
    }

    /// <summary>
    ///     True when extracting into <paramref name="targetDir" /> would replace an existing file of
    ///     the same name: the descriptor itself for any format, or the BIN of a cue/gdi set.
    /// </summary>
    /// <param name="targetDir">Directory the extraction would write to.</param>
    /// <param name="fileName">Base name of the extraction.</param>
    /// <param name="outputExt">Extension of the descriptor being written.</param>
    /// <returns>True when a same-named extraction output already exists.</returns>
    private static bool ExtractionOutputExists(string targetDir, string fileName, string outputExt)
    {
        if (File.Exists(Path.Combine(targetDir, fileName + outputExt))) return true;

        return outputExt is FileExtensions.Cue or FileExtensions.Gdi
            && File.Exists(Path.Combine(targetDir, fileName + FileExtensions.Bin));
    }

    /// <summary>Writes the whole CHD content to a single output file in chunks.</summary>
    /// <param name="chd">The opened CHD.</param>
    /// <param name="outputFile">Destination file path.</param>
    /// <param name="token">Cancellation token.</param>
    private static void ExtractChdToSingleFile(
        ChdFile chd,
        string outputFile,
        CancellationToken token
    )
    {
        using var fs = new FileStream(
            outputFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096
        );
        const int bufferSize = 4 * 1024 * 1024; // 4MB chunks
        var buffer = new byte[bufferSize];
        var remaining = chd.TotalBytes;
        ulong offset = 0;

        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min((ulong)buffer.Length, remaining);
            chd.Read(offset, buffer, 0, toRead, token);
            fs.Write(buffer, 0, toRead);
            offset += (ulong)toRead;
            remaining -= (ulong)toRead;
        }
    }

    /// <summary>
    ///     Extracts a CHD's tracks into a temp directory and moves them into the destination, isolating
    ///     them when names would clash.
    /// </summary>
    /// <param name="chd">The opened CHD.</param>
    /// <param name="chdFile">Full path of the CHD file.</param>
    /// <param name="targetDir">Directory that receives the extracted tracks.</param>
    /// <param name="baseFileName">Base name for the extracted files.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the tracks have been extracted.</returns>
    private async Task ExtractChdTracksToDirectoryAsync(
        ChdFile chd,
        string chdFile,
        string targetDir,
        string baseFileName,
        CancellationToken token
    )
    {
        var tempExtractDir = Path.Combine(
            targetDir,
            "_extract_temp_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(tempExtractDir);

        var allMoved = false;
        try
        {
            token.ThrowIfCancellationRequested();
            // The reporting variant returns per-track results instead of stopping at the first
            // failure, so the log and bug report can name the track(s) that failed to decode.
            var result = chd.ExtractToDirectoryWithReporting(
                tempExtractDir,
                baseFileName,
                null,
                token
            );
            if (!result.IsCompleteSuccess)
            {
                var builder = new StringBuilder("Extraction failed");
                if (result.Error != ChdError.Chderrnone)
                    builder.Append(": ").Append(result.Error);
                foreach (var track in result.TrackResults.Where(static r => !r.IsSuccess))
                {
                    builder.Append("; track ").Append(track.TrackNumber).Append(": ").Append(track.Error);
                }

                throw new InvalidDataException(builder.ToString());
            }

            var extractedFiles = result.CreatedFiles;

            if (extractedFiles.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No files extracted from '{Path.GetFileName(chdFile)}'."
                );
            }

            // A multi-track extraction writes a descriptor plus its track files, all named after the
            // CHD, so extracting into a folder that already holds that set would replace it. When any
            // of them would clash the whole set goes into a subfolder of its own name instead: the
            // descriptor's FILE entries are relative and the tracks travel with it, so the set stays
            // valid without rewriting anything.
            var destinationDir = targetDir;
            if (extractedFiles.Any(f => File.Exists(Path.Combine(targetDir, Path.GetFileName(f)))))
            {
                destinationDir = PathUtils.ReserveFreeSubdirectory(targetDir, baseFileName);
                Directory.CreateDirectory(destinationDir);

                LogMessage(
                    $" Files named after this disc already exist here; extracting into \"{Path.GetFileName(destinationDir)}\" so they are kept."
                );
            }

            // Move files from temp to the destination. Retry transient lock failures
            // (antivirus/indexer) so a locked file doesn't abort the whole disc.
            foreach (var srcPath in extractedFiles)
            {
                token.ThrowIfCancellationRequested();
                var destPath = Path.Combine(destinationDir, Path.GetFileName(srcPath));
                if (File.Exists(destPath))
                {
                    var deleted = await RetryingFileOperations
                        .TryDeleteAsync(destPath, token)
                        .ConfigureAwait(false);
                    if (!deleted)
                    {
                        throw new IOException(
                            $"Could not delete existing destination '{destPath}'."
                        );
                    }
                }

                var moved = await RetryingFileOperations
                    .TryMoveAsync(srcPath, destPath, token)
                    .ConfigureAwait(false);
                if (!moved)
                {
                    throw new IOException(
                        $"Failed to move extracted file '{srcPath}' to '{destPath}'."
                    );
                }

                LogMessage($" Extracted: {Path.GetFileName(destPath)}");
            }

            allMoved = true;
        }
        finally
        {
            if (allMoved)
            {
                try
                {
                    Directory.Delete(tempExtractDir, true);
                }
                catch
                {
                    /* best effort */
                }
            }
            else
            {
                // Extraction failed: clean up leftover files best-effort. A single-shot delete
                // per file is intentional — the retrying delete (~45 s/file) would stall the
                // batch for files that are still locked; whatever cannot be removed now is
                // reported below.
                var cleanedCount = 0;
                try
                {
                    if (Directory.Exists(tempExtractDir))
                    {
                        foreach (
                            var leftover in Directory.GetFiles(
                                tempExtractDir,
                                "*.*",
                                SearchOption.AllDirectories
                            )
                        )
                        {
                            try
                            {
                                File.Delete(leftover);
                                cleanedCount++;
                            }
                            catch
                            {
                                // ignored; reported below if it truly remains
                            }
                        }

                        Directory.Delete(tempExtractDir, true);
                    }
                }
                catch
                {
                    // ignored
                }

                if (cleanedCount > 0)
                {
                    Log.Debug(
                        "Cleaned up {Count} leftover file(s) from failed extraction of {File}",
                        cleanedCount,
                        Path.GetFileName(chdFile)
                    );
                }

                try
                {
                    var remaining = Directory.Exists(tempExtractDir)
                        ? Directory.GetFiles(tempExtractDir, "*.*", SearchOption.AllDirectories)
                        : [];
                    if (remaining.Length > 0)
                    {
                        LogWarning(
                            $" Partial extraction: {remaining.Length} file(s) remain in temp directory: {tempExtractDir}"
                        );
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    /// <summary>
    ///     Returns the chdman extraction command for the selected output format, auto-detecting when
    ///     needed.
    /// </summary>
    /// <param name="chdFile">Full path of the CHD file.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The chdman extraction command name.</returns>
    private async Task<string> GetSelectedExtractCommandAsync(
        string chdFile,
        CancellationToken token
    )
    {
        if (ExtractAutoRadioButton.IsChecked == true)
            return await DetectChdExtractCommandAsync(chdFile, token);
        if (ExtractDvdRadioButton.IsChecked == true)
            return "extractdvd";
        if (ExtractHdRadioButton.IsChecked == true)
            return "extracthd";

        // Both CD and GDI use the 'extractcd' command in chdman
        return "extractcd";
    }

    /// <summary>Returns whether the CHD's metadata identifies a GD-ROM image.</summary>
    /// <param name="chdFile">Full path of the CHD file.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True when the CHD is a GD-ROM image.</returns>
    private static Task<bool> IsGdiChdAsync(string chdFile, CancellationToken token)
    {
        return Task.Run(
            () =>
            {
                try
                {
                    var err = ChdFile.Open(chdFile, out var chd);
                    if (err != ChdError.Chderrnone || chd == null)
                        return false;

                    using (chd)
                    {
                        foreach (var meta in chd.Metadata)
                        {
                            if (
                                meta.ToString()
                                .Contains("gd-rom", StringComparison.OrdinalIgnoreCase)
                            )
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return false;
                }
            },
            token
        );
    }

    /// <summary>Detects the chdman extraction command from the CHD's metadata (DVD, hard disk or CD).</summary>
    /// <param name="chdFile">Full path of the CHD file.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The detected extraction command, defaulting to extractcd.</returns>
    private static Task<string> DetectChdExtractCommandAsync(
        string chdFile,
        CancellationToken token
    )
    {
        return Task.Run(
            () =>
            {
                try
                {
                    var err = ChdFile.Open(chdFile, out var chd);
                    if (err != ChdError.Chderrnone || chd == null)
                        return "extractcd";

                    using (chd)
                    {
                        foreach (var meta in chd.Metadata)
                        {
                            var text = meta.ToString();

                            if (text.Contains("dvd", StringComparison.OrdinalIgnoreCase))
                                return "extractdvd";
                            if (text.Contains("gd-rom", StringComparison.OrdinalIgnoreCase))
                                return "extractcd";
                            if (
                                text.Contains("hard disk", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("hdd", StringComparison.OrdinalIgnoreCase)
                            )
                            {
                                return "extracthd";
                            }
                        }
                    }

                    return "extractcd";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return "extractcd";
                }
            },
            token
        );
    }

    /// <summary>
    ///     When a cue/toc descriptor cannot be handed to chdman as-is (UTF-8 BOM, non-UTF-8 cue text,
    ///     non-ASCII names or paths, zero-padding name mismatches, MP3 audio tracks, or unresolved
    ///     references after correction), this creates an isolated ASCII work directory containing a
    ///     canonicalized cue plus every referenced file under safe ASCII names (MP3 tracks decoded to
    ///     WAV, which chdman requires), so chdman sees a self-contained cue set.
    ///     Returns (null, null) when the descriptor can be converted directly.
    /// </summary>
    private async Task<(string? WorkCuePath, string? WorkDir)> PrepareCueWorkDirAsync(
        string cuePath,
        CancellationToken token
    )
    {
        CueWorkDirectoryResult work;
        try
        {
            work = await CueWorkDirectory.PrepareAsync(
                cuePath,
                TempDirPrefix,
                Mp3Decoder,
                LogMessage,
                token
            );
        }
        catch (Exception ex) when (!IsCancellationException(ex))
        {
            // chdman cannot read MP3 tracks at all, so a failed work-dir preparation for an MP3
            // cue must not fall through to a direct chdman attempt ("Unhandled track type MP3").
            if (await CueHasMp3TracksAsync(cuePath, token))
            {
                LogError(
                    $" MP3 audio track could not be decoded to WAV for {Path.GetFileName(cuePath)}: {ex.Message}. The MP3 track(s) may be corrupt or in an unsupported format."
                );
            }
            else
            {
                LogMessage(
                    $" Cue normalization failed for {Path.GetFileName(cuePath)}: {ex.Message}"
                );
            }

            return (null, null);
        }

        if (work.UnresolvedNames.Count > 0)
        {
            LogWarning(
                $" {Path.GetFileName(cuePath)} — cue references could not be resolved: {string.Join(", ", work.UnresolvedNames)}"
            );
            return (null, null);
        }

        if (work.WorkCuePath is not null)
        {
            LogMessage(
                $" Prepared self-contained cue set for {Path.GetFileName(cuePath)} in a temporary directory."
            );
        }

        return (work.WorkCuePath, work.WorkDir);
    }


    /// <summary>
    ///     Converts a single input to CHD with chdman on Windows and the built-in CHDSharp encoder
    ///     elsewhere, staging the output and falling back between encoders.
    /// </summary>
    /// <param name="chdmanPath">Path of the chdman executable.</param>
    /// <param name="inputFile">Full path of the input file.</param>
    /// <param name="outputFile">Destination CHD path.</param>
    /// <param name="cores">Worker threads to give the encoder.</param>
    /// <param name="forceCd">Whether to force the createcd verb.</param>
    /// <param name="forceDvd">Whether to force the createdvd verb.</param>
    /// <param name="timeoutMinutes">Per-file timeout in minutes, or null for none.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="recursionDepth">Retry depth used when switching from createcd to createdvd.</param>
    /// <returns>True when the file was converted successfully.</returns>
    private async Task<bool> ConvertToChdAsync(
        string chdmanPath,
        string inputFile,
        string outputFile,
        int cores,
        bool forceCd,
        bool forceDvd,
        int? timeoutMinutes,
        CancellationToken token,
        int recursionDepth = 0
    )
    {
        // An .img or .bin sitting next to a .cue of the same name is the data half of a cue/bin
        // pair. The cue is the only file that carries the track layout, so hand chdman the cue:
        // passing the raw image instead selects createhd or a bare createcd and fails ("Data size
        // ... is not divisible by sector size 512" / "couldn't find bin file"). This also routes
        // the input through the cue work-directory preparation below.
        var companionCue = Path.ChangeExtension(inputFile, FileExtensions.Cue);
        if (
            (
                inputFile.EndsWith(FileExtensions.Img, StringComparison.OrdinalIgnoreCase)
                || inputFile.EndsWith(FileExtensions.Bin, StringComparison.OrdinalIgnoreCase)
            )
            && File.Exists(companionCue)
        )
        {
            LogMessage(
                $" {Path.GetFileName(inputFile)} is described by {Path.GetFileName(companionCue)}; converting the cue instead."
            );
            inputFile = companionCue;
        }

        var isCueDescriptor =
            inputFile.EndsWith(FileExtensions.Cue, StringComparison.OrdinalIgnoreCase)
            || inputFile.EndsWith(FileExtensions.Toc, StringComparison.OrdinalIgnoreCase);

        var isImg = inputFile.EndsWith(FileExtensions.Img, StringComparison.OrdinalIgnoreCase);
        var isRaw = inputFile.EndsWith(FileExtensions.Raw, StringComparison.OrdinalIgnoreCase);
        var isIso = inputFile.EndsWith(FileExtensions.Iso, StringComparison.OrdinalIgnoreCase);

        var command =
            forceCd || (!forceDvd && !isIso && !isImg && !isRaw) ? "createcd"
            : forceDvd || isIso ? "createdvd"
            : isImg ? "createhd"
            : "createraw";

        var args = $"{command} -i \"{inputFile}\" -o \"{outputFile}\" -f -np {cores}";
        if (isRaw)
        {
            args += " -us 2352";
        }
        else if (string.Equals(command, "createcd", StringComparison.Ordinal) && isCueDescriptor)
        {
            var refs = await GameFileParser
                .GetReferencedFilesFromCueAsync(inputFile, static _ => { }, token)
                .ConfigureAwait(false);
            if (
                refs.Any(static r =>
                    r.EndsWith(FileExtensions.Raw, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                args += " -us 2352";
            }
        }

        string? asciiTempDir = null;
        string? asciiInputFile = null;
        string? asciiOutputFile = null;
        var originalInputFile = inputFile;
        var originalOutputFile = outputFile;

        // Warn early about likely-corrupt disc images, but still let chdman try: some
        // legitimate images use non-standard sector layouts (e.g. 2448-byte sectors with
        // subchannel data) that chdman can convert. The post-failure check remains the hard gate.
        if (string.Equals(command, "createdvd", StringComparison.Ordinal))
        {
            var sectorWarning = IsoSectorValidator.GetSectorSizeWarning(originalInputFile);
            if (sectorWarning is not null)
            {
                LogWarning(
                    $" {Path.GetFileName(originalInputFile)}: {sectorWarning} Proceeding with conversion anyway."
                );
            }
        }

        // For cue/toc descriptors, hand chdman a canonicalized, self-contained cue set instead of the raw file:
        // this fixes UTF-8 BOMs, non-UTF-8 cue text (Korean/Cyrillic), zero-padding name mismatches, and
        // non-ASCII names/paths, which previously produced "couldn't find bin file" errors from chdman.
        // MP3 audio tracks are decoded to WAV in the work directory because chdman cannot read MP3.
        if (isCueDescriptor)
        {
            var work = await PrepareCueWorkDirAsync(inputFile, token);
            if (work.WorkDir is not null && work.WorkCuePath is not null)
            {
                asciiTempDir = work.WorkDir;
                asciiInputFile = work.WorkCuePath;
                inputFile = asciiInputFile;
                args = args.Replace($"\"{originalInputFile}\"", $"\"{inputFile}\"");
            }
            else if (await CueHasMp3TracksAsync(originalInputFile, token))
            {
                // The cue references MP3 tracks whose decode to WAV failed (the error was already
                // logged). chdman would only add a misleading "Unhandled track type MP3" error,
                // so stop here instead of attempting a direct conversion.
                return false;
            }
        }

        // chdman converts its UTF-16 command line down to the ANSI code page, so ANY non-ASCII
        // character along the path (an accented user name, a non-Latin folder name) can be
        // mangled before it reaches chdman's file APIs; paths at or beyond MAX_PATH fail the
        // same way. Check the whole path - checking only the file name misses unsafe directories,
        // e.g. "D:\Emulátory\PS2\Iso\God of War.iso" or "C:\Users\Kauê Chacon\Temp\game.cue".
        // Computed here, after cue work-dir preparation above, so an input that was already staged
        // into a safe work directory is not flagged again.
        var pathNeedsAscii = !PathUtils.IsChdmanSafePath(inputFile);
        var pathNeedsAsciiOut = !PathUtils.IsChdmanSafePath(outputFile);

        if (asciiTempDir == null && (pathNeedsAscii || pathNeedsAsciiOut))
        {
            // The staging location must itself be safe to hand to chdman: the system temp folder
            // lives under the user profile and can contain non-ASCII characters or be overlong,
            // which would reproduce the very failure this fallback exists to avoid.
            asciiTempDir = PathUtils.CreateAsciiSafeTempDirectory(TempDirPrefix);
            Directory.CreateDirectory(asciiTempDir);

            // Only the input needs staging when its own path is unsafe; an input chdman can read
            // in place (e.g. an ASCII cue whose destination path is overlong) keeps resolving its
            // FILE entries against its original directory.
            if (pathNeedsAscii)
            {
                asciiInputFile = Path.Combine(
                    asciiTempDir,
                    Guid.NewGuid().ToString("N") + Path.GetExtension(inputFile)
                );
                File.Copy(inputFile, asciiInputFile);
                inputFile = asciiInputFile;
            }

            asciiOutputFile = Path.Combine(
                asciiTempDir,
                Guid.NewGuid().ToString("N") + FileExtensions.Chd
            );
            outputFile = asciiOutputFile;
            args = args.Replace($"\"{originalInputFile}\"", $"\"{inputFile}\"")
                .Replace($"\"{originalOutputFile}\"", $"\"{outputFile}\"");
        }
        else if (asciiTempDir != null && pathNeedsAsciiOut)
        {
            // Work directory already prepared for the input; only the output name is non-ASCII.
            asciiOutputFile = Path.Combine(
                asciiTempDir,
                Guid.NewGuid().ToString("N") + FileExtensions.Chd
            );
            outputFile = asciiOutputFile;
            args = args.Replace($"\"{originalOutputFile}\"", $"\"{outputFile}\"");
        }

        // Otherwise write to a staging file beside the destination and only move it into place once
        // chdman has succeeded. chdman is invoked with -f, so aiming it straight at the destination
        // would truncate an existing CHD before failing - that is how a finished conversion could be
        // destroyed by a later, unrelated input that happened to resolve to the same output name.
        // Staging beside the destination keeps the move on one volume, so it stays a rename.
        if (asciiOutputFile == null)
        {
            var stagingDir = Path.GetDirectoryName(originalOutputFile);
            if (!string.IsNullOrEmpty(stagingDir))
            {
                if (!Directory.Exists(stagingDir))
                    Directory.CreateDirectory(stagingDir);

                asciiOutputFile = Path.Combine(
                    stagingDir,
                    Path.GetFileNameWithoutExtension(originalOutputFile)
                    + "."
                    + Guid.NewGuid().ToString("N")[..8]
                    + StagingExtension
                );
                outputFile = asciiOutputFile;
                args = args.Replace($"\"{originalOutputFile}\"", $"\"{outputFile}\"");
            }
        }

        if (!await HasRoomForOutputAsync(inputFile, originalInputFile, originalOutputFile, token))
        {
            TryCleanupAsciiTemp();
            return false;
        }

        // --- Primary encoder: chdman on Windows; the built-in CHDSharp encoder elsewhere ---
        var useChdman = OperatingSystem.IsWindows() && File.Exists(chdmanPath);
        if (!useChdman)
        {
            LogMessage($"CHDSHARP: {command} {Path.GetFileName(originalInputFile)}");
            if (await TryChdSharpInProcessAsync())
            {
                TryCleanupAsciiTemp();
                return true;
            }

            if (token.IsCancellationRequested)
            {
                TryCleanupAsciiTemp();
                return false;
            }

            LogError(
                $" Failed to convert '{Path.GetFileName(originalInputFile)}': the built-in CHDSharp encoder did not produce a usable output."
            );
            TryCleanupAsciiTemp();
            return false;
        }

        LogMessage($"CHDMAN: {command} {Path.GetFileName(originalInputFile)}");

        using var process = new Process();

        process.StartInfo = new ProcessStartInfo
        {
            FileName = chdmanPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false
        };

        var errorBuffer = new StringBuilder();
        process.OutputDataReceived += (_, a) =>
        {
            if (string.IsNullOrEmpty(a.Data))
                return;

            if (
                a.Data.Contains("Compression complete", StringComparison.Ordinal)
                || a.Data.Contains("final ratio", StringComparison.Ordinal)
            )
            {
                LogMessage($"[CHDMAN ✓] {a.Data}");
            }
            else if (
                !a.Data.Contains("% complete", StringComparison.Ordinal)
                && !a.Data.Contains("Compressing", StringComparison.Ordinal)
                && !a.Data.Contains("Output bytes", StringComparison.Ordinal)
                && !a.Data.Contains("Compression ratio", StringComparison.Ordinal)
            )
            {
                LogMessage($"[CHDMAN] {a.Data}");
            }
        };

        process.ErrorDataReceived += (_, a) =>
        {
            if (string.IsNullOrEmpty(a.Data))
                return;

            errorBuffer.AppendLine(a.Data);

            if (
                a.Data.Contains("Compression complete", StringComparison.Ordinal)
                || a.Data.Contains("final ratio", StringComparison.Ordinal)
            )
            {
                LogMessage($"[CHDMAN ✓] {a.Data}");
            }
            else if (
                !a.Data.Contains("% complete", StringComparison.Ordinal)
                && !a.Data.Contains("Compressing", StringComparison.Ordinal)
                && !a.Data.Contains("Output bytes", StringComparison.Ordinal)
                && !a.Data.Contains("Compression ratio", StringComparison.Ordinal)
            )
            {
                LogMessage($"[CHDMAN] {a.Data}");
            }
        };

        using var ctsSpeed = CancellationTokenSource.CreateLinkedTokenSource(token);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex) when (!IsCancellationException(ex))
        {
            TryCleanupAsciiTemp();
            LogError($" Failed to start chdman: {ex.Message}");
            return false;
        }

        var speedToken = ctsSpeed.Token;
        var speedMonitoringTask = Task.Run(
            async () =>
            {
                try
                {
                    while (!speedToken.IsCancellationRequested)
                    {
                        UpdateWriteSpeedFromPerformanceCounter();
                        await Task.Delay(AppConfig.WriteSpeedUpdateIntervalMs, speedToken);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            speedToken
        );

        var cleanupAfterProcessKill = false;
        try
        {
            token.ThrowIfCancellationRequested();

            if (timeoutMinutes is > 0)
            {
                using var timeoutCts = new CancellationTokenSource();
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes.Value));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    token,
                    timeoutCts.Token
                );

                await process.WaitForExitAsync(linkedCts.Token);
            }
            else
            {
                await process.WaitForExitAsync(token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            if (token.IsCancellationRequested)
            {
                cleanupAfterProcessKill = true;
                throw;
            }

            if (timeoutMinutes != null)
            {
                LogMessage(
                    $"TIMEOUT: Conversion of '{Path.GetFileName(inputFile)}' exceeded {timeoutMinutes.Value} minute(s). Marking as failed."
                );
            }

            cleanupAfterProcessKill = true;
            return false;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
            }

            ctsSpeed.Cancel();
            await Task.WhenAny(speedMonitoringTask, Task.Delay(500, CancellationToken.None));
            process.CancelOutputRead();
            process.CancelErrorRead();

            // On cancellation/timeout the process was just killed; wait for it to release its
            // file handles before deleting the temp directory, otherwise the cleanup silently fails.
            if (cleanupAfterProcessKill)
            {
                await Task.Delay(300, CancellationToken.None);
                TryCleanupAsciiTemp();
            }
        }

        try
        {
            var exitCode = process.ExitCode;
            var success = exitCode == 0 && !token.IsCancellationRequested;

            if (!success && !token.IsCancellationRequested && exitCode != 0)
            {
                var errorText = errorBuffer.ToString().TrimEnd();

                if (
                    errorText.Contains(
                        "Unrecognized track type",
                        StringComparison.OrdinalIgnoreCase
                    )
                    && string.Equals(command, "createcd", StringComparison.Ordinal)
                    && !forceCd
                )
                {
                    if (recursionDepth >= 1)
                    {
                        LogError(
                            $" Retry limit reached for {Path.GetFileName(originalInputFile)}; giving up."
                        );
                    }
                    else
                    {
                        LogMessage(
                            $" Retrying with createdvd (unrecognized track type) for {Path.GetFileName(originalInputFile)}..."
                        );
                        return await ConvertToChdAsync(
                            chdmanPath,
                            originalInputFile,
                            originalOutputFile,
                            cores,
                            false,
                            true,
                            timeoutMinutes,
                            token,
                            recursionDepth + 1
                        );
                    }
                }

                if (File.Exists(outputFile))
                {
                    try
                    {
                        // A nonzero exit with a non-empty file is not proof of success: a disk that
                        // fills mid-compression leaves a truncated CHD behind. The output is only
                        // accepted when it actually validates, otherwise it is deleted with the
                        // rest of the failed staging output and the source is kept.
                        using var outputStream = File.OpenRead(outputFile);
                        var check = Chd.CheckFile(outputStream, Path.GetFileName(outputFile), true);
                        if (check.IsSuccess)
                        {
                            LogMessage(
                                $" chdman exited with code {exitCode} but produced a valid CHD ({new FileInfo(outputFile).Length} bytes). Treating as success."
                            );
                            success = true;
                        }
                        else
                        {
                            LogError(
                                $" chdman exited with code {exitCode} and its output is not a valid CHD ({check.Error.GetMessage()}); discarding it."
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError(
                            $" chdman exited with code {exitCode} and its output could not be validated: {ex.Message}; discarding it."
                        );
                    }
                }
            }

            if (success)
            {
                // Two inputs in one batch can still resolve to the same CHD (an archive's contents
                // are not known when the collision preflight runs). Keep the first product and
                // report the duplicate instead of silently replacing it.
                lock (_batchOutputPathsLock)
                {
                    if (!_batchOutputPaths.Add(Path.GetFullPath(originalOutputFile)))
                    {
                        LogWarning(
                            $" {Path.GetFileName(originalOutputFile)} was already produced earlier in this batch; keeping the first one."
                        );
                        TryBestEffortDelete(outputFile);
                        return false;
                    }
                }

                if (asciiOutputFile != null)
                {
                    try
                    {
                        var targetDir = Path.GetDirectoryName(originalOutputFile);
                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                            Directory.CreateDirectory(targetDir);
                        if (File.Exists(originalOutputFile))
                        {
                            var deleted = await RetryingFileOperations
                                .TryDeleteAsync(originalOutputFile, token)
                                .ConfigureAwait(false);
                            if (!deleted)
                            {
                                throw new IOException(
                                    $"Could not delete existing destination '{originalOutputFile}'."
                                );
                            }
                        }

                        var moved = await RetryingFileOperations
                            .TryMoveAsync(outputFile, originalOutputFile, token)
                            .ConfigureAwait(false);
                        if (!moved)
                        {
                            throw new IOException(
                                $"Could not move temp output '{outputFile}' to '{originalOutputFile}'."
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($" Failed to move temp output to destination: {ex.Message}");
                        return false;
                    }
                }

                if (success)
                    return true;
            }

            if (token.IsCancellationRequested) return false;

            // --- Fallback encoder: built-in CHDSharp ---
            // Informational only: chdman failing on a user file is routine and the fallback
            // usually succeeds. When both encoders fail, the classified LogError below reports.
            LogMessage(
                $"chdman failed for '{Path.GetFileName(originalInputFile)}'. Falling back to the built-in CHDSharp encoder..."
            );
            if (await TryChdSharpInProcessAsync())
                return true;

            if (token.IsCancellationRequested)
                return false;

            // --- Both encoders failed: report the chdman diagnostics ---
            var errorTextFinal = errorBuffer.ToString().TrimEnd();

            try
            {
                var effectiveInput = asciiInputFile ?? originalInputFile;
                var inputExt = Path.GetExtension(effectiveInput).ToLowerInvariant();

                // Skip sector-size check for text-based descriptor files (.cue/.gdi/.toc).
                // These are plain text files that reference separate data files (.bin/.iso/.raw);
                // their file size is irrelevant to sector alignment. chdman handles them
                // correctly when the referenced data files are present.
                if (inputExt is not (".cue" or ".gdi" or ".toc"))
                {
                    var fileSize = new FileInfo(effectiveInput).Length;
                    if (fileSize > 0)
                    {
                        // Standard CD/DVD sector sizes to try.
                        // 2352: raw CD audio/data (2352 bytes/sector)
                        // 2048: Mode 1 / DVD data (2048 bytes/sector)
                        // 2336: Mode 2 XA (2336 bytes/sector)
                        // 2324: Mode 2 Form 1 (2324 bytes/sector)
                        var sectorSizes = new[] { 2352L, 2048L, 2336L, 2324L };
                        var isSectorAligned = sectorSizes.Any(ss => fileSize % ss == 0);

                        if (!isSectorAligned)
                        {
                            LogError(
                                $" Failed to convert '{Path.GetFileName(originalInputFile)}': file size ({fileSize:N0} bytes) is not divisible by any standard sector size (2048/2324/2336/2352). The file may be corrupt or truncated."
                            );
                            return false;
                        }
                    }
                }
            }
            catch
            {
                // ignored
            }

            if (IsDiskSpaceError(errorTextFinal))
            {
                LogError(
                    $" Conversion of '{Path.GetFileName(originalInputFile)}' failed due to insufficient disk space."
                );
                LogMessage("       Free up disk space on the output drive and try again.");
            }
            else if (IsIoError(errorTextFinal))
            {
                LogError(
                    $" Conversion of '{Path.GetFileName(originalInputFile)}' failed due to an I/O error. The source file may be on a failing disk, a disconnected network drive, or the file may be corrupt."
                );
                LogMessage(
                    "       Try copying the source file to a local drive and converting again."
                );
            }
            else if (IsPermissionError(errorTextFinal))
            {
                LogError(
                    $" Conversion of '{Path.GetFileName(originalInputFile)}' failed due to a permission error. The output folder may be write-protected or require administrator rights."
                );
                LogMessage(
                    "       Choose a different output folder (e.g. Documents or a data drive) or run as administrator."
                );
            }
            else if (IsMissingDeviceError(errorTextFinal))
            {
                LogError(
                    $" Conversion of '{Path.GetFileName(originalInputFile)}' failed because a drive or device is no longer available. The source or output drive was disconnected or removed while converting (USB drive, network share, or virtual drive)."
                );
                LogMessage(
                    "       Reconnect the drive, or copy the source file to a local drive and convert again."
                );
            }
            else if (errorTextFinal.Length > 0)
            {
                var errorLine = SelectChdmanErrorLine(errorTextFinal);
                LogError(
                    $" Failed to convert '{Path.GetFileName(originalInputFile)}': {errorLine}"
                );

                if (
                    errorLine.Contains("couldn't find bin file", StringComparison.OrdinalIgnoreCase)
                    || errorLine.Contains("Unknown error", StringComparison.OrdinalIgnoreCase)
                )
                {
                    LogWarning(
                        $"       Files found in input directory ({Path.GetDirectoryName(originalInputFile) ?? "?"}): {GetDirectoryDiagnostics(originalInputFile)}"
                    );
                }

                if (errorLine.Contains("Unknown error", StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage(
                        "       'Unknown error' from chdman typically indicates a corrupt source file, an unsupported disc format, or an I/O issue. Try converting the file from a local drive."
                    );
                }

                if (errorLine.Contains("Input/output error", StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage(
                        "       An input/output error while reading the source usually means a failing or disconnected drive, a file locked by antivirus or cloud sync, or a damaged disc image. Check the drive for errors and try converting from a local drive."
                    );
                }
            }
            else if (exitCode < 0)
            {
                // A negative exit code means Windows terminated chdman abnormally - it crashed
                // before it could print anything. The most common cause is a CPU missing the
                // SIMD instruction sets (SSE4.2/AVX) that recent MAME-based builds compile in;
                // antivirus quarantine damage produces the same class of crash.
                LogError(
                    $" Failed to convert '{Path.GetFileName(originalInputFile)}': chdman terminated abnormally (exit code {exitCode}{DescribeChdmanCrash(exitCode)})."
                );
                LogWarning(
                    "       The bundled chdman.exe may be incompatible with this computer's CPU or Windows version, or was damaged/quarantined by antivirus software."
                );
                LogMessage(
                    "       Replace chdman.exe with a build that matches your CPU and Windows version (e.g. an official MAME tools release) and add an antivirus exclusion for it."
                );
            }
            else
            {
                LogError(
                    $" Failed to convert '{Path.GetFileName(originalInputFile)}': chdman exited with code {exitCode} but produced no error output. The file may be corrupted or in an unsupported format."
                );
            }

            return false;
        }
        finally
        {
            TryCleanupAsciiTemp();
        }

        void TryCleanupAsciiTemp()
        {
            try
            {
                if (asciiInputFile != null && File.Exists(asciiInputFile))
                    File.Delete(asciiInputFile);
            }
            catch
            {
                // ignored
            }

            try
            {
                if (asciiOutputFile != null && File.Exists(asciiOutputFile))
                    File.Delete(asciiOutputFile);
            }
            catch
            {
                // ignored
            }

            try
            {
                if (Directory.Exists(asciiTempDir))
                    Directory.Delete(asciiTempDir, true);
            }
            catch
            {
                // ignored
            }
        }

        // Runs the built-in CHDSharp encoder for the same command the chdman attempt used, on a
        // fresh output staging path. The prepared input (ASCII copy or cue work directory) is kept
        // — only the stale output staging is replaced — so the encoder never re-prepares and a
        // cue's work set stays valid. Mutates asciiOutputFile/outputFile so the cleanup above
        // removes the path the encoder actually wrote to.
        async Task<bool> TryChdSharpInProcessAsync()
        {
            if (asciiOutputFile != null)
            {
                try
                {
                    if (File.Exists(asciiOutputFile))
                        File.Delete(asciiOutputFile);
                }
                catch
                {
                    // ignored
                }
            }

            if (asciiTempDir != null)
            {
                asciiOutputFile = Path.Combine(
                    asciiTempDir,
                    Guid.NewGuid().ToString("N") + FileExtensions.Chd
                );
            }
            else
            {
                var stagingDir = Path.GetDirectoryName(originalOutputFile);
                if (string.IsNullOrEmpty(stagingDir))
                    return false;

                if (!Directory.Exists(stagingDir))
                {
                    try
                    {
                        Directory.CreateDirectory(stagingDir);
                    }
                    catch (Exception ex)
                        when (ex is DriveNotFoundException or DirectoryNotFoundException)
                    {
                        // The output drive vanished mid-batch (unplugged, unmounted). Report the
                        // folder rather than letting the raw exception surface as an app bug.
                        LogError($" Output folder is not available: {stagingDir}");
                        return false;
                    }
                }

                asciiOutputFile = Path.Combine(
                    stagingDir,
                    Path.GetFileNameWithoutExtension(originalOutputFile)
                    + "."
                    + Guid.NewGuid().ToString("N")[..8]
                    + StagingExtension
                );
            }

            outputFile = asciiOutputFile;

            LogMessage($"CHDSHARP: {command} {Path.GetFileName(originalInputFile)}");

            // Same live speed display as the chdman path: sample the process write throughput for
            // as long as the encoder runs.
            using var fallbackSpeedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var fallbackSpeedToken = fallbackSpeedCts.Token;
            var fallbackSpeedTask = Task.Run(
                async () =>
                {
                    try
                    {
                        while (!fallbackSpeedToken.IsCancellationRequested)
                        {
                            UpdateWriteSpeedFromPerformanceCounter();
                            await Task.Delay(AppConfig.WriteSpeedUpdateIntervalMs, fallbackSpeedToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                },
                fallbackSpeedToken
            );

            using var fallbackTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (timeoutMinutes is > 0)
                fallbackTimeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes.Value));

            try
            {
                await Task.Run(
                    () =>
                        ChdSharpEncoderService.Encode(
                            command,
                            inputFile,
                            outputFile,
                            isRaw,
                            cores,
                            fallbackTimeoutCts.Token
                        ),
                    fallbackTimeoutCts.Token
                );
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested && timeoutMinutes is not null)
                {
                    LogMessage(
                        $"TIMEOUT: Conversion of '{Path.GetFileName(originalInputFile)}' exceeded {timeoutMinutes.Value} minute(s). Marking as failed."
                    );
                }

                return false;
            }
            catch (Exception ex)
            {
                LogError($" CHDSharp encoding failed: {ex.Message}");
                return false;
            }
            finally
            {
                fallbackSpeedCts.Cancel();
                await Task.WhenAny(fallbackSpeedTask, Task.Delay(500, CancellationToken.None));
            }

            // CHDSharp internally validates its output; trust the exit code.
            try
            {
                var targetDir = Path.GetDirectoryName(originalOutputFile);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);
                if (File.Exists(originalOutputFile))
                {
                    var deleted = await RetryingFileOperations
                        .TryDeleteAsync(originalOutputFile, token)
                        .ConfigureAwait(false);
                    if (!deleted)
                    {
                        throw new IOException(
                            $"Could not delete existing destination '{originalOutputFile}'."
                        );
                    }
                }

                var moved = await RetryingFileOperations
                    .TryMoveAsync(outputFile, originalOutputFile, token)
                    .ConfigureAwait(false);
                if (!moved)
                {
                    throw new IOException(
                        $"Could not move temp output '{outputFile}' to '{originalOutputFile}'."
                    );
                }
            }
            catch (Exception ex)
            {
                LogError($" Failed to move CHDSharp output to destination: {ex.Message}");
                return false;
            }

            return true;
        }
    }

    /// <summary>
    ///     Picks the most useful line from chdman's error output: the last non-empty line that is
    ///     not progress output. chdman streams progress ("Compressing, 0.0% complete... (ratio=100.0%)")
    ///     to stderr, so the first line of the error buffer is often a progress line rather than the
    ///     actual error; the real error (e.g. "couldn't find bin file [...]") comes last.
    ///     The final "Fatal error occurred: N" line is chdman's exit summary and is skipped as well,
    ///     because the actual cause is always printed on the line(s) before it.
    /// </summary>
    internal static string SelectChdmanErrorLine(string errorText)
    {
        var lines = errorText
            .Split('\n')
            .Select(static l => l.TrimEnd('\r').Trim())
            .Where(static l => l.Length > 0)
            .ToList();

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (
                line.Contains("% complete", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Compressing,", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Converting,", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Output bytes", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Compression ratio", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ratio=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Fatal error occurred", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            return line;
        }

        return lines.Count > 0
            ? lines[^1].StartsWith("Fatal error occurred", StringComparison.OrdinalIgnoreCase)
                ? "chdman encountered an error. The file may be corrupted, in an unsupported format, or a required codec may be missing."
                : lines[^1]
            : string.Empty;
    }

    /// <summary>
    ///     Describes the NTSTATUS code behind a negative chdman exit code. chdman prints nothing when
    ///     Windows kills it outright, so the raw number is all the user sees; naming the common crash
    ///     codes turns it into something actionable (most often a CPU that lacks the instruction sets
    ///     the bundled build was compiled with).
    /// </summary>
    internal static string DescribeChdmanCrash(int exitCode)
    {
        return exitCode switch
        {
            -1073741795 =>
                "; 0xC000001D, STATUS_ILLEGAL_INSTRUCTION - the CPU executed an unsupported instruction",
            -1073741819 => "; 0xC0000005, STATUS_ACCESS_VIOLATION",
            -1073741676 => "; 0xC0000094, integer divide by zero",
            -1073741571 => "; 0xC00000FD, stack overflow",
            -1073741515 => "; 0xC0000135, a required DLL could not be found",
            -1073741511 =>
                "; 0xC0000139, STATUS_ENTRYPOINT_NOT_FOUND - a required DLL entry point is missing (the build is incompatible with this Windows version; install Windows updates / the latest Visual C++ redistributable or use a chdman build for your OS)",
            -1073740791 => "; 0xC0000409, stack buffer overrun / fail fast",
            _ => string.Empty
        };
    }

    /// <summary>
    ///     Maps CHDSharp extraction exception messages to user-friendly text. Decompression failures
    ///     ("Failed to read hunk N", Chderrdecompressionerror) occur when a CHD is corrupt or uses the
    ///     A/V (laserdisc) codec variant that the built-in reader cannot decode; the message says so
    ///     instead of showing a cryptic codec error. It is logged only when the chdman fallback has
    ///     also failed, so the guidance covers both causes.
    /// </summary>
    internal static string GetChdExtractionErrorMessage(string? message)
    {
        message ??= string.Empty;

        if (
            message.Contains("Chderrdecompressionerror", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Failed to read hunk", StringComparison.OrdinalIgnoreCase)
        )
        {
            return message
                   + " The CHD file may be corrupt, or it may be an A/V (laserdisc) CHD, which the built-in reader cannot decode.";
        }

        return message;
    }

    /// <summary>
    ///     Builds the diagnostic suffix attached to a CHDSharp extraction failure: the CHD header
    ///     fields that identify the image (version, codecs, geometry, hashes), so a report is
    ///     classifiable even when the library returns only a numeric error code.
    /// </summary>
    /// <param name="chd">The opened CHD.</param>
    private static string BuildChdDiagnostics(ChdFile chd)
    {
        var builder = new StringBuilder();
        builder
            .Append("CHD v")
            .Append(chd.Version)
            .Append(" compression=")
            .AppendJoin(",", chd.Compression)
            .Append(" secondary=")
            .Append(chd.SecondaryCodec)
            .Append(" hunks=")
            .Append(chd.HunkCount)
            .Append(" hunkBytes=")
            .Append(chd.HunkBytes)
            .Append(" totalBytes=")
            .Append(chd.TotalBytes)
            .Append(" cd=")
            .Append(chd.IsCd)
            .Append(" gdRom=")
            .Append(chd.IsGdRom)
            .Append(" dvd=")
            .Append(chd.IsDvd)
            .Append(" sha1=")
            .Append(chd.Sha1 is { Length: > 0 } sha1 ? Convert.ToHexString(sha1) : "n/a");

        return builder.ToString();
    }

    /// <summary>
    ///     Builds the chdman argument string for an extraction command, matching the app's existing
    ///     chdman arg style (short -i/-o flags, -f to force overwrite). extractcd also pins the bin
    ///     output name (-ob) so the app knows exactly where the data file lands.
    /// </summary>
    internal static string BuildChdmanExtractArgs(
        string command,
        string inputFile,
        string outputPath
    )
    {
        return string.Equals(command, "extractcd", StringComparison.Ordinal)
            ? $"extractcd -i \"{inputFile}\" -o \"{outputPath}\" -ob \"{Path.ChangeExtension(outputPath, FileExtensions.Bin)}\" -f"
            : $"{command} -i \"{inputFile}\" -o \"{outputPath}\" -f";
    }

    /// <summary>
    ///     Attempts to extract a CHD with chdman after the built-in CHDSharp reader failed.
    ///     A/V (laserdisc) CHDs — which have no CD/DVD/HDD metadata — are extracted with
    ///     <c>extractld</c> (AVI, MAME 0.285+); if that command is unavailable, <c>extractraw</c>
    ///     (raw dump) is tried. Returns true when chdman produced the output file(s).
    /// </summary>
    private async Task<bool> TryExtractWithChdmanAsync(
        string chdmanPath,
        string chdFile,
        string targetDir,
        string fileName,
        string extractCommand,
        string outputExt,
        CancellationToken token
    )
    {
        if (string.IsNullOrEmpty(chdmanPath) || !File.Exists(chdmanPath))
        {
            LogWarning(" chdman.exe not found; skipping fallback extraction.");
            return false;
        }

        // Laserdisc CHDs have no CD/DVD/HDD metadata, so the selected extract command
        // (extractcd/dvd/hd) cannot handle them. Try the user's format first, then — when
        // the CHD is A/V — extractld (AVI, MAME 0.285+) and extractraw (raw dump).
        var isAvChd = await IsAvChdAsync(chdFile, token).ConfigureAwait(false);

        var attempts = new List<(string Command, string OutputPath)>
        {
            // chdman extractcd always writes a CUE sheet (plus BIN), even when the app's
            // auto-detection would have produced a .gdi descriptor for GD-ROM CHDs.
            (
                extractCommand,
                string.Equals(extractCommand, "extractcd", StringComparison.Ordinal)
                    ? Path.Combine(targetDir, fileName + FileExtensions.Cue)
                    : Path.Combine(targetDir, fileName + outputExt)
            )
        };

        if (isAvChd)
        {
            LogMessage(" CHD has no CD/DVD/HDD metadata; treating it as an A/V (laserdisc) CHD.");
            attempts.Add(("extractld", Path.Combine(targetDir, fileName + FileExtensions.Avi)));
            attempts.Add(("extractraw", Path.Combine(targetDir, fileName + FileExtensions.Raw)));
        }

        foreach (var (command, outputPath) in attempts)
        {
            LogMessage($" [CHDMAN fallback] {command} {Path.GetFileName(chdFile)}");

            try
            {
                if (
                    await RunChdmanExtractAsync(
                            chdmanPath,
                            BuildChdmanExtractArgs(command, chdFile, outputPath),
                            token
                        )
                        .ConfigureAwait(false)
                )
                {
                    if (string.Equals(command, "extractcd", StringComparison.Ordinal))
                    {
                        LogMessage(
                            string.Equals(outputExt, FileExtensions.Gdi, StringComparison.Ordinal)
                                ? $" Extracted: {Path.GetFileName(outputPath)} and {Path.GetFileName(Path.ChangeExtension(outputPath, FileExtensions.Bin))} (chdman fallback writes CUE/BIN; the GDI descriptor requires the built-in reader)"
                                : $" Extracted: {Path.GetFileName(outputPath)} and {Path.GetFileName(Path.ChangeExtension(outputPath, FileExtensions.Bin))}"
                        );
                    }
                    else
                    {
                        LogMessage($" Extracted: {Path.GetFileName(outputPath)}");
                    }

                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                // chdman ran with -f, so a cancelled run may have left truncated output behind.
                TryBestEffortDelete(outputPath);
                if (string.Equals(command, "extractcd", StringComparison.Ordinal))
                    TryBestEffortDelete(Path.ChangeExtension(outputPath, FileExtensions.Bin));

                throw;
            }

            // chdman ran with -f, so a failed attempt may have left truncated output behind.
            // Plain single-shot deletes only: the retrying delete would kill every chdman
            // process by name on lock, and our chdman has already exited.
            TryBestEffortDelete(outputPath);
            if (string.Equals(command, "extractcd", StringComparison.Ordinal))
                TryBestEffortDelete(Path.ChangeExtension(outputPath, FileExtensions.Bin));
        }

        return false;
    }

    /// <summary>
    ///     Silently deletes a file if it exists. Used to clean up partial chdman fallback output.
    /// </summary>
    private static void TryBestEffortDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignored — partial output may remain; the error log above already tells the user
        }
    }

    /// <summary>
    ///     Best-effort check for A/V (laserdisc) CHDs: the CHD header opens but carries no
    ///     CD/DVD/HDD metadata. Header parsing never decodes hunks, so this works even for
    ///     CHDs whose data CHDSharp cannot decompress. Any failure classifies as not-A/V.
    /// </summary>
    private static Task<bool> IsAvChdAsync(string chdFile, CancellationToken token)
    {
        return Task.Run(
            () =>
            {
                try
                {
                    var err = ChdFile.Open(chdFile, out var chd);
                    if (err != ChdError.Chderrnone || chd == null)
                        return false;

                    using (chd)
                    {
                        return chd is { IsCd: false, IsDvd: false, IsHdd: false, IsGdRom: false };
                    }
                }
                catch
                {
                    return false;
                }
            },
            token
        );
    }

    /// <summary>
    ///     Runs a chdman extraction command and returns whether it exited successfully.
    /// </summary>
    private static async Task<bool> RunChdmanExtractAsync(
        string chdmanPath,
        string args,
        CancellationToken token
    )
    {
        using var process = new Process();
        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = chdmanPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ErrorDialog = false
            };

            var errorBuffer = new StringBuilder();
            var errorBufferLock = new Lock();

            process.OutputDataReceived += (_, a) => CaptureOutput(a.Data);
            process.ErrorDataReceived += (_, a) => CaptureOutput(a.Data);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(token).ConfigureAwait(false);

            if (process.ExitCode == 0)
                return true;

            Log.Warning(
                "chdman {Command} failed (exit {ExitCode}): {Output}",
                args.Split(' ')[0],
                process.ExitCode,
                errorBuffer.ToString().TrimEnd()
            );
            return false;

            void CaptureOutput(string? data)
            {
                if (string.IsNullOrEmpty(data))
                    return;

                lock (errorBufferLock)
                {
                    errorBuffer.AppendLine(data);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposing the Process wrapper does not stop the child; without a kill it keeps
            // writing (and holding the output file) after the user cancelled or closed the app.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(3000);
                }
            }
            catch
            {
                // Process already exited or access denied.
            }

            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "chdman extract could not be started");
            return false;
        }
    }

    /// <summary>
    ///     Returns a capped, sorted listing of file names in the directory containing <paramref name="filePath" />,
    ///     used as a diagnostic when chdman reports a missing bin file.
    /// </summary>
    private static string GetDirectoryDiagnostics(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return "(input directory not accessible)";

        try
        {
            const int maxShown = 40;
            var names = Directory
                .GetFiles(directory)
                .Select(Path.GetFileName)
                .Where(static n => n is not null)
                .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var shown = names.Take(maxShown).ToList();
            var extra = names.Count - shown.Count;
            var suffix = extra > 0 ? $", ... and {extra} more" : string.Empty;
            return string.Join(", ", shown) + suffix;
        }
        catch (Exception ex)
        {
            return $"(directory listing failed: {ex.Message})";
        }
    }

    /// <summary>
    ///     Builds the failure message for a disc whose PSAR extraction failed, attaching the image
    ///     geometry and the library's block-level diagnostics so the report identifies the failing
    ///     block and the inflate errors rather than only the numeric code.
    /// </summary>
    /// <param name="pbpFile">The opened PBP container.</param>
    /// <param name="disc">The disc that failed to extract.</param>
    /// <param name="binPath">Partial BIN path, used to count the blocks written before the failure.</param>
    /// <param name="error">The error code the library returned.</param>
    private static string BuildPbpFailureDetail(
        PbpFile pbpFile,
        PbpDiscInfo disc,
        string binPath,
        PbpError error
    )
    {
        var builder = new StringBuilder();
        builder
            .Append("Failed to extract disc ")
            .Append(disc.Index)
            .Append(" of ")
            .Append(pbpFile.Discs.Count)
            .Append(": ")
            .Append(error)
            .Append(" (code ")
            .Append((int)error)
            .Append("); blocks=")
            .Append(disc.BlockCount)
            .Append(", isoSize=")
            .Append(disc.IsoSize)
            .Append(", discId=")
            .Append(disc.DiscId)
            .Append(", psarOffset=0x")
            .Append(pbpFile.Header.DataPsarOffset.ToString("X", CultureInfo.InvariantCulture));

        try
        {
            if (File.Exists(binPath))
            {
                builder
                    .Append(", blocksWritten=")
                    .Append(new FileInfo(binPath).Length / PbpDiscInfo.IsoBlockSize);
            }
        }
        catch
        {
            // Diagnostics must never mask the original failure.
        }

        var detail = PbpDiagnostics.TakeDetail();
        if (!string.IsNullOrWhiteSpace(detail)) builder.Append("; ").Append(detail);

        return builder.ToString();
    }

    /// <summary>Extracts every disc in a PBP file to cue/bin pairs.</summary>
    /// <param name="inputFile">Full path of the PBP file.</param>
    /// <param name="outputFolder">Folder that receives the cue/bin pairs.</param>
    /// <param name="onLog">Callback that receives progress messages.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The extraction result, including the cue paths on success.</returns>
    private static async Task<PbpExtractionResult> ExtractPbpToCueBinAsync(
        string inputFile,
        string outputFolder,
        Action<string> onLog,
        CancellationToken token
    )
    {
        onLog($"PBPSharp: Extracting {Path.GetFileName(inputFile)}");

        try
        {
            var extractionResult = await Task.Run(
                () =>
                {
                    var error = PbpFile.Open(inputFile, out var pbpFile);
                    if (error != PbpError.None || pbpFile == null)
                    {
                        return (
                            Success: false,
                            CuePaths: new List<string>(),
                            Error: $"Failed to open PBP file: {error} (code {(int)error})",
                            ErrorCode: error
                        );
                    }

                    using (pbpFile)
                    {
                        var cuePaths = new List<string>();

                        foreach (var t in pbpFile.Discs)
                        {
                            token.ThrowIfCancellationRequested();

                            var suffix = pbpFile.IsMultiDisc ? $" - Disc {t.Index}" : "";
                            var binPath = Path.Combine(
                                outputFolder,
                                $"{Path.GetFileNameWithoutExtension(inputFile)}{suffix}.bin"
                            );
                            var cuePath = Path.ChangeExtension(binPath, ".cue");

                            var extractError = t.ExtractToBinCue(binPath, cuePath, null, token);
                            if (extractError != PbpError.None)
                            {
                                return (
                                    Success: false,
                                    CuePaths: new List<string>(),
                                    Error: BuildPbpFailureDetail(
                                        pbpFile,
                                        t,
                                        binPath,
                                        extractError
                                    ),
                                    ErrorCode: extractError
                                );
                            }

                            cuePaths.Add(cuePath);
                        }

                        return (
                            Success: true,
                            CuePaths: cuePaths,
                            Error: string.Empty,
                            ErrorCode: PbpError.None
                        );
                    }
                },
                token
            );

            if (!extractionResult.Success)
            {
                onLog($"PBPSharp: Extraction failed - {extractionResult.Error}");
                return new PbpExtractionResult
                {
                    Success = false,
                    ErrorCode = extractionResult.ErrorCode,
                    Error = extractionResult.Error
                };
            }

            onLog($"PBPSharp: Extracted {extractionResult.CuePaths.Count} disc(s)");
            return new PbpExtractionResult
            {
                Success = true,
                CueFilePaths = extractionResult.CuePaths,
                OutputFolder = outputFolder
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            onLog($"PBPSharp: Extraction error - {ex.Message}");
            return new PbpExtractionResult
            {
                Success = false,
                ErrorCode = PbpError.CorruptFile,
                Error = ex.Message
            };
        }
    }

    /// <summary>Samples the write-throughput counter and updates the write speed display.</summary>
    private void UpdateWriteSpeedFromPerformanceCounter()
    {
        try
        {
            double writeBytesPerSec;
            lock (_performanceCounterLock)
            {
                writeBytesPerSec = _writeBytesCounter?.NextValue() ?? 0;
            }

            if (writeBytesPerSec > 0) UpdateWriteSpeedDisplay(writeBytesPerSec / 1048576.0); // Convert to MB/s
        }
        catch
        {
            // Ignore performance counter errors
        }
    }

    /// <summary>Samples the read-throughput counter and updates the read speed display.</summary>
    private void UpdateReadSpeedFromPerformanceCounter()
    {
        try
        {
            double readBytesPerSec;
            lock (_performanceCounterLock)
            {
                readBytesPerSec = _readBytesCounter?.NextValue() ?? 0;
            }

            if (readBytesPerSec > 0) UpdateReadSpeedDisplay(readBytesPerSec / 1048576.0); // Convert to MB/s
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>Verifies a CHD file and logs its version and SHA1.</summary>
    /// <param name="chdFile">Full path of the CHD file.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>True when verification passed.</returns>
    private Task<bool> VerifyChdAsync(string chdFile, CancellationToken token)
    {
        return Task.Run(
            () =>
            {
                try
                {
                    using var stream = File.OpenRead(chdFile);
                    var result = Chd.CheckFile(stream, Path.GetFileName(chdFile), true);

                    if (result.IsSuccess)
                    {
                        LogMessage($"  V{result.Version} — SHA1: {result.Sha1Hex}");
                        return true;
                    }

                    LogMessage($"  Error: {result.Error.GetMessage()}");
                    return false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogMessage($"  Verification error: {ex.Message}");
                    return false;
                }
            },
            token
        );
    }

    /// <summary>Resets the operation statistics, timer and progress display.</summary>
    private void ResetOperationStats()
    {
        _totalFilesProcessed = 0;
        _processedOkCount = 0;
        _failedCount = 0;
        _operationTimer.Reset();
        UpdateStatsDisplay();
        UpdateProcessingTimeDisplay();
        ResetSpeedCounters();
        ClearProgressDisplay();
    }

    /// <summary>
    ///     Starts the operation stopwatch and the one-second UI tick that keeps the elapsed-time
    ///     stat card counting while the operation runs.
    /// </summary>
    private void StartOperationTimer()
    {
        _operationTimer.Restart();
        _elapsedTimeTimer.Start();
    }

    /// <summary>Updates the total, success and failed counters in the UI.</summary>
    private void UpdateStatsDisplay()
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            TotalFilesValue.Text = $"{_totalFilesProcessed}";
            SuccessValue.Text = $"{_processedOkCount}";
            FailedValue.Text = $"{_failedCount}";
        });
    }

    /// <summary>Updates the elapsed processing time in the UI.</summary>
    private void UpdateProcessingTimeDisplay()
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
            ProcessingTimeValue.Text = $@"{_operationTimer.Elapsed:hh\:mm\:ss}"
        );
    }

    /// <summary>Updates the write speed label and status message.</summary>
    /// <param name="speed">Speed in megabytes per second.</param>
    private void UpdateWriteSpeedDisplay(double speed)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Update the actual label
            SpeedValue.Text = $"{speed:F1} MB/s";

            if (speed > 0 && !StartConversionButton.IsEnabled) StatusBarMessage.Text = "Converting...";
        });
    }

    /// <summary>Updates the read speed label and status message for the active operation.</summary>
    /// <param name="speed">Speed in megabytes per second.</param>
    private void UpdateReadSpeedDisplay(double speed)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            SpeedValue.Text = $"{speed:F1} MB/s";
            StatusBarMessage.Text = speed switch
            {
                > 0 when string.Equals(_activeOperation, "Extraction", StringComparison.Ordinal) =>
                    "Extracting...",
                > 0 when string.Equals(_activeOperation, "Verification", StringComparison.Ordinal) =>
                    "Verifying...",
                _ => StatusBarMessage.Text
            };
        });
    }

    /// <summary>Updates the progress bar and text for the file currently being processed.</summary>
    /// <param name="completedCount">Number of finished files.</param>
    /// <param name="tot">Total number of files.</param>
    /// <param name="name">Name of the file being processed.</param>
    /// <param name="verb">Verb describing the operation.</param>
    private void UpdateProgressDisplay(int completedCount, int tot, string name, string verb)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            // If we haven't finished all files, show the next one in the text (completed + 1)
            var displayIndex = Math.Min(completedCount + 1, tot);
            ProgressText.Text =
                completedCount < tot
                    ? $"{verb} {displayIndex}/{tot}: {name}"
                    : $"{verb} process complete.";

            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = completedCount;
            ProgressBar.Maximum = tot > 0 ? tot : 1;
            ProgressText.IsVisible = true;
            ProgressBar.IsVisible = true;
        });
    }

    /// <summary>Hides the progress bar and clears the progress text.</summary>
    private void ClearProgressDisplay()
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            ProgressBar.Value = 0;
            ProgressBar.IsVisible = false;
            ProgressText.Text = "";
            ProgressText.IsVisible = false;
        });
    }

    /// <summary>
    ///     Deletes the source files of a successfully converted game, including those referenced by its
    ///     descriptor.
    /// </summary>
    /// <param name="inputFile">Full path of the descriptor or image.</param>
    /// <param name="inputFolder">Root of the conversion input folder; referenced files outside it are kept.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task DeleteOriginalGameFilesAsync(
        string inputFile,
        string inputFolder,
        CancellationToken token
    )
    {
        try
        {
            var files = new List<string>();
            var ext = Path.GetExtension(inputFile);
            if (ext.Equals(FileExtensions.Cue, StringComparison.OrdinalIgnoreCase))
            {
                files.AddRange(
                    await GameFileParser.GetReferencedFilesFromCueAsync(
                        inputFile,
                        LogMessage,
                        token
                    )
                );
            }
            else if (ext.Equals(FileExtensions.Gdi, StringComparison.OrdinalIgnoreCase))
            {
                files.AddRange(
                    await GameFileParser.GetReferencedFilesFromGdiAsync(
                        inputFile,
                        LogMessage,
                        token
                    )
                );
            }
            else if (ext.Equals(FileExtensions.Toc, StringComparison.OrdinalIgnoreCase))
            {
                files.AddRange(
                    await GameFileParser.GetReferencedFilesFromTocAsync(
                        inputFile,
                        LogMessage,
                        token
                    )
                );
            }
            else if (ext.Equals(FileExtensions.Ccd, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var disc = CcdConverter.Parse(inputFile);
                    if (disc.ImgFilePath != null)
                        files.Add(disc.ImgFilePath);
                    if (disc.SubFilePath != null)
                        files.Add(disc.SubFilePath);
                }
                catch
                {
                    /* ignore parse errors, just delete what we can */
                }

                var cdtPath = Path.ChangeExtension(inputFile, ".cdt");
                if (File.Exists(cdtPath))
                    files.Add(cdtPath);
            }

            await TryDeleteReferencedFileAsync(inputFile, "game file", inputFolder, token);

            foreach (var f in files.Distinct(StringComparer.Ordinal))
                await TryDeleteReferencedFileAsync(f, "game file", inputFolder, token);
        }
        catch (Exception ex)
        {
            LogError($"Delete error: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Rewrites <paramref name="filePath" /> without its UTF-8 BOM when present. chdman's cue parser
    ///     does not skip a BOM (the first token becomes "\uFEFFFILE", so the FILE directive is never
    ///     parsed and chdman reports "couldn't find bin file []"). Best effort — failures are ignored
    ///     so the real conversion error still surfaces.
    /// </summary>
    internal static async Task StripUtf8BomIfPresentAsync(string filePath, CancellationToken token)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath, token).ConfigureAwait(false);
            if (bytes is [0xEF, 0xBB, 0xBF, ..])
                await File.WriteAllBytesAsync(filePath, bytes[3..], token).ConfigureAwait(false);
        }
        catch
        {
            // best effort — the conversion will surface the real error otherwise
        }
    }

    /// <summary>Copies a file with exponential-backoff retries for transient I/O failures.</summary>
    /// <param name="source">Source file path.</param>
    /// <param name="dest">Destination file path.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the copy has finished.</returns>
    private static async Task CopyFileWithRetryAsync(
        string source,
        string dest,
        CancellationToken token
    )
    {
        const int baseDelayMs = 500;

        for (var attempt = 0; attempt < MaxFileOperationRetries; attempt++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await Task.Run(() => File.Copy(source, dest, true), token);
                return;
            }
            catch (IOException ex)
                when (attempt < MaxFileOperationRetries - 1
                      && !IsDiskSpaceException(ex)
                      && !IsCrcErrorException(ex)
                     )
            {
                await Task.Delay(baseDelayMs * (1 << attempt), token);
            }
        }
    }

    /// <summary>
    ///     Determines whether the given exception represents an operation cancellation
    ///     (either user-requested or timeout-based).
    /// </summary>
    /// <param name="ex">The exception to check.</param>
    /// <returns><c>true</c> if the exception is an <see cref="OperationCanceledException" />; otherwise, <c>false</c>.</returns>
    internal static bool IsCancellationException(Exception ex)
    {
        return ex is OperationCanceledException;
    }

    /// <summary>
    ///     Determines whether the given exception indicates a disk-full condition
    ///     by checking the Windows error codes ERROR_DISK_FULL (0x80070070) or ERROR_SEM_TIMEOUT (0x80070079).
    /// </summary>
    /// <param name="ex">The exception to check.</param>
    /// <returns>
    ///     <c>true</c> if the exception is an <see cref="IOException" /> with a disk-full HRESULT; otherwise,
    ///     <c>false</c>.
    /// </returns>
    internal static bool IsDiskSpaceException(Exception ex)
    {
        // HResult 0x80070070 = ERROR_DISK_FULL, 0x80070079 = ERROR_SEM_TIMEOUT (can indicate disk issues)
        return ex is IOException { HResult: -2147024784 or -2147024775 };
    }

    /// <summary>
    ///     Determines whether the given exception indicates a CRC (cyclic redundancy check) error,
    ///     typically caused by corrupted files or failing storage media.
    ///     Checks for Windows error code ERROR_CRC (0x80070017) and relevant message keywords.
    /// </summary>
    /// <param name="ex">The exception to check.</param>
    /// <returns><c>true</c> if the exception indicates a CRC error; otherwise, <c>false</c>.</returns>
    internal static bool IsCrcErrorException(Exception ex)
    {
        // HResult 0x80070017 = ERROR_CRC (cyclic redundancy check)
        // Also check message as fallback for cases where HResult may differ
        return ex is IOException
               && (
                   ex.HResult == -2147024873
                   || ex.Message.Contains(
                       "cyclic redundancy check",
                       StringComparison.OrdinalIgnoreCase
                   )
                   || ex.Message.Contains("data error", StringComparison.OrdinalIgnoreCase)
               );
    }

    /// <summary>
    ///     Determines whether the given exception indicates data corruption in an archive or
    ///     compressed file, checking for known SharpCompress corruption exception types and
    ///     standard .NET corruption-related exceptions.
    /// </summary>
    /// <param name="ex">The exception to check.</param>
    /// <returns><c>true</c> if the exception type indicates data corruption; otherwise, <c>false</c>.</returns>
    internal static bool IsCorruptionException(Exception ex)
    {
        return ex
                   is InvalidDataException
                   or IndexOutOfRangeException
                   or NullReferenceException
                   or CryptographicException
               || ex.GetType().FullName
                   is "SharpCompress.Common.IncompleteArchiveException"
                   or "SharpCompress.Common.ArchiveOperationException"
                   or "SharpCompress.Common.InvalidFormatException"
                   or "SharpCompress.Compressors.LZMA.DataErrorException";
    }

    /// <summary>Returns whether chdman's error output indicates a disk-full condition.</summary>
    /// <param name="errorOutput">Captured chdman error output.</param>
    /// <returns>True when the output mentions a lack of disk space.</returns>
    private static bool IsDiskSpaceError(string? errorOutput)
    {
        if (string.IsNullOrEmpty(errorOutput))
            return false;

        return errorOutput.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("not enough disk space", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("disk full", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("no space left", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("insufficient disk space", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns whether chdman's error output indicates an I/O error.</summary>
    /// <param name="errorOutput">Captured chdman error output.</param>
    /// <returns>True when the output mentions an I/O failure.</returns>
    private static bool IsIoError(string? errorOutput)
    {
        if (string.IsNullOrEmpty(errorOutput))
            return false;

        return errorOutput.Contains("Input/output error", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("read error", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("write error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns whether chdman's error output indicates a permission error.</summary>
    /// <param name="errorOutput">Captured chdman error output.</param>
    /// <returns>True when the output mentions denied access.</returns>
    private static bool IsPermissionError(string? errorOutput)
    {
        if (string.IsNullOrEmpty(errorOutput))
            return false;

        return errorOutput.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     True when chdman reports a Win32 "device does not exist" class of failure, which means the
    ///     source or output drive disappeared mid-conversion (an unplugged USB drive, a dropped network
    ///     share, or a removed virtual drive) rather than the disc image being at fault.
    /// </summary>
    internal static bool IsMissingDeviceError(string? errorOutput)
    {
        if (string.IsNullOrEmpty(errorOutput))
            return false;

        return errorOutput.Contains(
                   "A device which does not exist was specified",
                   StringComparison.OrdinalIgnoreCase
               )
               || errorOutput.Contains("The device is not ready", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains(
                   "The system cannot find the drive specified",
                   StringComparison.OrdinalIgnoreCase
               );
    }

    /// <summary>
    ///     Probes the output folder once by creating and deleting a uniquely named file. Returns false
    ///     only on a definitive access denial; other probe failures (transient locks, network quirks)
    ///     return true so a flaky probe never blocks a batch that would in fact have worked.
    /// </summary>
    private static bool IsOutputFolderWritable(string outputFolder)
    {
        var probePath = Path.Combine(outputFolder, $".write_test_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probePath, string.Empty);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch
        {
            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }
            catch
            {
                // ignored - a leftover zero-byte probe file is harmless
            }
        }
    }

    /// <summary>
    ///     Logs a warning when the output or temp drive has less free space than the batch may need.
    /// </summary>
    /// <param name="outputFolder">Root of the output folder.</param>
    /// <param name="filesToProcess">Files that will be processed.</param>
    /// <param name="isConversion">True for conversion, false for extraction.</param>
    private void CheckDiskSpace(string outputFolder, string[] filesToProcess, bool isConversion)
    {
        try
        {
            var outputRoot = Path.GetPathRoot(Path.GetFullPath(outputFolder));
            if (string.IsNullOrEmpty(outputRoot))
                return;

            var driveInfo = new DriveInfo(outputRoot);
            if (!driveInfo.IsReady)
                return;

            var availableGb = driveInfo.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
            var totalInputSize = 0L;
            foreach (var file in filesToProcess)
            {
                try
                {
                    totalInputSize += new FileInfo(file).Length;
                }
                catch
                {
                    /* skip inaccessible files */
                }
            }

            var totalInputGb = totalInputSize / (1024.0 * 1024.0 * 1024.0);

            if (isConversion)
            {
                // CHD compression typically reduces size, but warn if available space < 50% of input
                if (availableGb < totalInputGb * 0.5)
                {
                    LogMessage(
                        $" Output drive ({outputRoot.TrimEnd('\\')}) has {availableGb:F1} GB free, input files total {totalInputGb:F1} GB."
                    );
                    LogMessage(
                        "         CHD compression usually reduces file size, but you may run out of disk space."
                    );
                }
            }
            else
            {
                // Extraction: output can be larger than CHD input
                if (availableGb < totalInputGb)
                {
                    LogMessage(
                        $" Output drive ({outputRoot.TrimEnd('\\')}) has {availableGb:F1} GB free, CHD files total {totalInputGb:F1} GB."
                    );
                    LogMessage(
                        "         Extracted files are typically larger than CHD files. You may run out of disk space."
                    );
                }
            }

            // Also check temp drive if conversion (temp files are created)
            if (isConversion)
            {
                var tempRoot = Path.GetPathRoot(Path.GetTempPath());
                if (
                    !string.IsNullOrEmpty(tempRoot)
                    && !string.Equals(tempRoot, outputRoot, StringComparison.OrdinalIgnoreCase)
                )
                {
                    var tempDrive = new DriveInfo(tempRoot);
                    if (tempDrive.IsReady)
                    {
                        var tempFreeGb = tempDrive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                        if (tempFreeGb < totalInputGb)
                        {
                            LogMessage(
                                $" Temp drive ({tempRoot.TrimEnd('\\')}) has {tempFreeGb:F1} GB free, input files total {totalInputGb:F1} GB."
                            );
                            LogMessage(
                                "         Temporary files are created during conversion. You may run out of disk space."
                            );
                        }
                    }
                }
            }
        }
        catch
        {
            // Best effort - don't fail the operation if disk check itself fails
        }
    }

    /// <summary>Deletes a file with retries and logs the result.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <param name="desc">Description used in log messages.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the delete attempt has finished.</returns>
    private async Task TryDeleteFileAsync(string path, string desc, CancellationToken token)
    {
        var deleted = await RetryingFileOperations.TryDeleteAsync(
            path,
            token,
            attempt =>
            {
                if (attempt >= 2)
                    KillChdmanProcesses();
            }
        );

        if (deleted)
            LogMessage($"Deleted {desc}: {Path.GetFileName(path)}");
        else
            LogError($"Failed to delete {desc}: {Path.GetFileName(path)}");
    }

    /// <summary>
    ///     Deletes a file referenced by a descriptor only when it lives inside the batch's input
    ///     folder. A descriptor can name a path anywhere ("..\..\other\game.bin", an absolute path),
    ///     and deleting outside the folder the user chose would destroy files the batch never owned.
    /// </summary>
    /// <param name="path">Full path of the referenced file.</param>
    /// <param name="desc">Description used in log messages.</param>
    /// <param name="inputFolder">Root of the conversion input folder.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the delete attempt has finished.</returns>
    private async Task TryDeleteReferencedFileAsync(
        string path,
        string desc,
        string inputFolder,
        CancellationToken token
    )
    {
        if (!PathUtils.IsSameOrInsideDirectory(inputFolder, path))
        {
            LogMessage(
                $" Skipping delete of {Path.GetFileName(path)} ({desc}) - it is outside the input folder."
            );
            return;
        }

        await TryDeleteFileAsync(path, desc, token);
    }

    /// <summary>Process names (without extension) of the bundled chdman builds.</summary>
    private static readonly string[] ChdmanProcessNames = ["chdman", "chdman_arm64"];

    /// <summary>Process names (without extension) of every bundled tool, across architectures.</summary>
    private static readonly string[] OrphanedToolProcessNames =
    [
        "chdman",
        "chdman_arm64",
        "7za",
        "7za_arm64",
        "7zz"
    ];

    /// <summary>Kills any chdman processes other than the current process.</summary>
    private static void KillChdmanProcesses()
    {
        KillProcessesByName(ChdmanProcessNames);
    }

    /// <summary>Kills every running process whose name is in <paramref name="processNames" />.</summary>
    /// <param name="processNames">Process names without extension.</param>
    private static void KillProcessesByName(string[] processNames)
    {
        try
        {
            var currentPid = Environment.ProcessId;
            foreach (var processName in processNames)
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            if (process.Id != currentPid)
                            {
                                process.Kill(true);
                                process.WaitForExit(3000);
                            }
                        }
                        catch
                        {
                            // Process already exited or access denied.
                        }
                    }
                }
                catch
                {
                    // Process name not found or access denied.
                }
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    ///     Deletes a directory with retries, clearing read-only attributes first and logging the result.
    /// </summary>
    /// <param name="path">Full path of the directory.</param>
    /// <param name="desc">Description used in log messages.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the delete attempt has finished.</returns>
    private async Task TryDeleteDirectoryAsync(string path, string desc, CancellationToken token)
    {
        for (var attempt = 0; attempt < MaxFileOperationRetries; attempt++)
        {
            try
            {
                await Task.Run(
                    () =>
                    {
                        // Files staged into temp dirs can carry the ReadOnly attribute (copied
                        // from read-only sources or set by other tools); Directory.Delete
                        // refuses those and reports "access denied" naming the file itself.
                        // Clear the attributes first so the real delete can proceed.
                        ClearDirectoryAttributes(path);
                        Directory.Delete(path, true);
                    },
                    token
                );
                return;
            }
            catch (DirectoryNotFoundException)
            {
                LogMessage($"{desc} already deleted: {Path.GetFileName(path)}");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (attempt < MaxFileOperationRetries - 1)
            {
                await Task.Delay(500 * (attempt + 1), token);
            }
            catch
            {
                // Final attempt failed. Cleanup is best effort: rethrowing here would abort
                // the whole run and report an app bug for what is a leftover temp folder the
                // OS will happily reclaim later.
                break;
            }
        }

        LogWarning($"Could not delete {desc}: {Path.GetFileName(path)}");
    }

    /// <summary>Clears the read-only attribute on every file and directory under a path before deletion.</summary>
    /// <param name="path">Root directory to clear.</param>
    private static void ClearDirectoryAttributes(string path)
    {
        try
        {
            foreach (
                var file in Directory.EnumerateFiles(
                    path,
                    "*",
                    SearchOption.AllDirectories
                )
            )
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // ignored - the delete attempt reports the real cause
                }
            }

            // Read-only directories also make Directory.Delete fail; clear them, including
            // the root itself, so nothing the delete touches can stay protected.
            var directoriesToClear = new List<string> { path };
            try
            {
                directoriesToClear.AddRange(
                    Directory.EnumerateDirectories(
                        path,
                        "*",
                        SearchOption.AllDirectories
                    )
                );
            }
            catch
            {
                // ignored - the files above were cleared and the delete attempt reports the rest
            }

            foreach (var directory in directoriesToClear)
            {
                try
                {
                    File.SetAttributes(directory, FileAttributes.Normal);
                }
                catch
                {
                    // ignored - the delete attempt reports the real cause
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>Deletes a subfolder when it is empty, unless it is the input folder itself.</summary>
    /// <param name="subfolderPath">Folder to delete when empty.</param>
    /// <param name="inputFolder">Root of the input folder, which is never deleted.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>A task that completes when the delete attempt has finished.</returns>
    private async Task TryDeleteEmptySubfolderAsync(
        string subfolderPath,
        string inputFolder,
        CancellationToken token
    )
    {
        try
        {
            // Don't delete the root input folder
            if (
                string.Equals(
                    Path.GetFullPath(subfolderPath),
                    Path.GetFullPath(inputFolder),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return;
            }

            if (
                Directory.Exists(subfolderPath)
                && !Directory.EnumerateFileSystemEntries(subfolderPath).Any()
            )
            {
                await Task.Run(() => Directory.Delete(subfolderPath, false), token);
                LogMessage($"Deleted empty folder: {Path.GetFileName(subfolderPath)}");
            }
        }
        catch
        {
            // Ignore folder deletion errors
        }
    }

    /// <summary>Reloads the active tab's file list when the search-subfolders option changes.</summary>
    private void SearchSubfoldersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RefreshFileListForActiveTab();
    }

    /// <summary>Clears the force-DVD option when force-CD is checked.</summary>
    private void ForceCreateCdCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        // Only react to the box becoming checked: IsCheckedChanged also fires when it is
        // unchecked, and clearing the other box then would undo the user's selection.
        if (ForceCreateCdCheckBox.IsChecked == true)
        {
            ForceCreateDvdCheckBox.IsChecked = false;
        }
    }

    /// <summary>Clears the force-CD option when force-DVD is checked.</summary>
    private void ForceCreateDvdCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (ForceCreateDvdCheckBox.IsChecked == true)
        {
            ForceCreateCdCheckBox.IsChecked = false;
        }
    }

    /// <summary>Logs the operation result summary and shows the completion message box.</summary>
    /// <param name="op">Name of the operation.</param>
    private void LogOperationSummary(string op)
    {
        var verb = _wasCancelled ? "canceled" : "completed";
        LogMessage(
            $"--- {op} {verb}. Total: {_totalFilesProcessed}, OK: {_processedOkCount}, Failed: {_failedCount}"
        );
        UpdateStatusBarMessage($"{op} {verb}" + (_failedCount > 0 ? " with errors" : ""));
        _ = ShowMessageBoxAsync(
            $"{op} {verb}.\nTotal: {_totalFilesProcessed}\nOK: {_processedOkCount}\nFailed: {_failedCount}",
            "Complete",
            MessageBoxButton.Ok,
            _failedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information
        );
    }

    /// <summary>Shows a message box owned by this window.</summary>
    /// <param name="msg">Message text.</param>
    /// <param name="title">Window title.</param>
    /// <param name="btns">Buttons to show.</param>
    /// <param name="icon">Icon to show.</param>
    /// <returns>The result chosen by the user.</returns>
    private Task<MessageBoxResult> ShowMessageBoxAsync(
        string msg,
        string title,
        MessageBoxButton btns,
        MessageBoxImage icon
    )
    {
        return MessageBox.ShowAsync(this, msg, title, btns, icon);
    }

    /// <summary>Shows an error message box on the UI thread.</summary>
    /// <param name="msg">Error message text.</param>
    private void ShowError(string msg)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
            _ = MessageBox.ShowAsync(
                this,
                msg,
                "Error",
                MessageBoxButton.Ok,
                MessageBoxImage.Error
            )
        );
    }

    /// <summary>
    ///     Shows the "new version available" prompt and opens the release page when accepted.
    ///     Invoked by <see cref="UpdateService" /> when a newer GitHub release is detected; the
    ///     callback may arrive on a background thread, so the UI work is marshalled explicitly.
    /// </summary>
    private async Task ShowUpdatePromptAsync(GitHubRelease release)
    {
        await Dispatcher.UIThread.InvokeAsync(() => ShowUpdatePromptCoreAsync(release));
    }

    /// <summary>Prompts the user about an available update and opens the release page or copies its URL.</summary>
    /// <param name="release">The detected release.</param>
    /// <returns>A task that completes when the prompt has been handled.</returns>
    private async Task ShowUpdatePromptCoreAsync(GitHubRelease release)
    {
        var remoteVersionString = UpdateService.ParseVersionFromTag(release.TagName);

        var result = await MessageBox.ShowAsync(
            this,
            $"A new version ({remoteVersionString}) of {AppConfig.ApplicationName} is available!\n\nWould you like to go to the download page?",
            "New Version Available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information
        );

        if (result != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo(release.HtmlUrl) { UseShellExecute = true });
        }
        catch (Exception urlEx)
        {
            LogMessage($"Failed to open browser: {urlEx.Message}");
            await ReportBugAsync("Failed to open browser", urlEx);

            try
            {
                var clipboard = GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(release.HtmlUrl);
                }
            }
            catch (Exception clipboardEx)
            {
                LogMessage($"Failed to copy URL to clipboard: {clipboardEx.Message}");
                await ReportBugAsync("Failed to copy URL to clipboard", clipboardEx);
            }

            await MessageBox.ShowAsync(
                this,
                $"Unable to open browser automatically. The update URL has been copied to your clipboard.\n\nURL: {release.HtmlUrl}\n\nPlease paste it into your browser manually.",
                "Browser Launch Failed",
                MessageBoxButton.Ok,
                MessageBoxImage.Information
            );
        }
    }

    /// <summary>
    ///     Observes a fire-and-forget task and logs its exception instead of letting it go unobserved.
    /// </summary>
    /// <param name="task">The task to observe.</param>
    private static void SafeFireAndForget(Task task)
    {
        _ = task.ContinueWith(
            static t =>
            {
                if (t.Exception is not null)
                    Log.Debug(t.Exception.Flatten(), "Fire-and-forget task failed");
            },
            TaskContinuationOptions.OnlyOnFaulted
        );
    }

    /// <summary>Sends a bug report through the shared bug report service, ignoring failures.</summary>
    /// <param name="msg">Description of the problem.</param>
    /// <param name="ex">Optional exception that caused the report.</param>
    /// <returns>A task that completes when the report has been sent.</returns>
    private static async Task ReportBugAsync(string msg, Exception? ex = null)
    {
        try
        {
            if (App.SharedBugReportService != null) await App.SharedBugReportService.SendBugReportAsync(msg, ex);
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>Resets the read and write performance counters to take fresh readings.</summary>
    private void ResetSpeedCounters()
    {
        // Reset performance counters to get fresh readings
        lock (_performanceCounterLock)
        {
            _writeBytesCounter?.NextValue();
            _readBytesCounter?.NextValue();
        }
    }

    /// <summary>Closes the window from the Exit menu item.</summary>
    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // Ensure the window close process is initiated
        // The Window_Closing event will handle proper cleanup and shutdown
        Close();
    }

    /// <summary>Opens the project's donation page in the default browser.</summary>
    private void DonateMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(
                new ProcessStartInfo { FileName = AppConfig.DonationUrl, UseShellExecute = true }
            );
        }
        catch (Exception ex)
        {
            LogError("Failed to open the donation page", ex);
        }
    }

    /// <summary>Opens the About window.</summary>
    private void AboutMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        _ = new AboutWindow().ShowDialog(this);
    }

    /// <summary>Opens the application's local AppData folder in the file manager.</summary>
    private void OpenAppDataFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppConfig.ApplicationName
            );
            Directory.CreateDirectory(appDataPath);
            Process.Start(new ProcessStartInfo { FileName = appDataPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogError("Failed to open AppData folder", ex);
        }
    }

    /// <summary>Kills leftover chdman and 7-Zip processes from previous runs.</summary>
    private static void KillOrphanedProcesses()
    {
        KillProcessesByName(OrphanedToolProcessNames);
    }

    /// <summary>
    ///     What content inspection decided about an input: something to convert, or a reason to skip.
    /// </summary>
    /// <param name="PathToConvert">File to hand chdman, or null when skipping.</param>
    /// <param name="ForceDvd">True when the resolved file must be converted as a DVD image.</param>
    /// <param name="SkipReason">User-facing explanation, or null when there is something to convert.</param>
    private sealed record ResolvedInput(string? PathToConvert, bool ForceDvd, string? SkipReason)
    {
        /// <summary>Creates a result that converts the given path.</summary>
        /// <param name="path">File to convert.</param>
        /// <param name="forceDvd">True when the file must be converted as a DVD image.</param>
        /// <returns>A conversion result.</returns>
        internal static ResolvedInput Convert(string path, bool forceDvd)
        {
            return new ResolvedInput(path, forceDvd, null);
        }

        /// <summary>Creates a result that skips the input with the given reason.</summary>
        /// <param name="reason">User-facing explanation for the skip.</param>
        /// <returns>A skip result.</returns>
        internal static ResolvedInput Skip(string reason)
        {
            return new ResolvedInput(null, false, reason);
        }
    }
}