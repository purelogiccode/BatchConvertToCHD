using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BatchConvertToCHD.Models;
using BatchConvertToCHD.Services;
using BatchConvertToCHD.Utilities;
using BatchConvertToCHD.Utilities.Ecm;
using BatchConvertToCHD.Utilities.Isz;
using BatchConvertToCHD.Utilities.Mds;
using CCDSharp;
using CCDSharp.Models;
using CHDSharp;
using CHDSharp.Models;
using Microsoft.Win32;
using PBPSharp;
using PBPSharp.Models;
using Serilog;

namespace BatchConvertToCHD;

/// <summary>
///     Main application window for BatchConvertToCHD.
///     Provides functionality for converting, verifying, and extracting CHD files.
/// </summary>
internal partial class MainWindow : IDisposable
{
    // Global hotkey for F8 screenshot
    private const int HotkeyId = 9001;
    private const int VkF8 = 0x77;
    private const int WmHotkey = 0x0312;

    // Temp Directory Prefix
    private const string TempDirPrefix = "BatchConvertToCHD_Temp_";

    // Extension used while a CHD is still being written. chdman ignores the output extension, and
    // keeping it off ".chd" means a leftover staging file is never mistaken for a finished CHD by
    // the verification and extraction tabs.
    private const string StagingExtension = ".chdtmp";

    // Performance counter for write speed monitoring
    private const int MaxLogLength = 100000; // Maximum characters before log truncation

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
    private readonly string _chdSharpExePath;
    private readonly string _chdSharpResolvedName;
    private readonly string _chdmanExePath;
    private readonly string _chdmanResolvedName;

    // File collections for DataGrids
    private readonly ObservableCollection<FileItem> _conversionFiles = new();
    private readonly Lock _ctsLock = new();
    private readonly ObservableCollection<FileItem> _extractionFiles = new();
    private readonly FileWatcherService _fileWatcher = new();
    private readonly bool _isChdSharpAvailable;
    private readonly bool _isChdmanAvailable;
    private readonly Stopwatch _operationTimer = new();
    private readonly Lock _performanceCounterLock = new();
    private readonly ScreenshotService _screenshotService;
    private readonly string _sevenZipExePath;

    // Services
    private readonly UpdateService _updateService;
    private readonly ObservableCollection<FileItem> _verificationFiles = new();
    private CancellationTokenSource _cts;
    private volatile int _failedCount;
    private HwndSource? _hwndSource;

    // Operation state tracking (0 = idle, >0 = running) - using Interlocked for thread safety
    private int _operationRunningState;

    // Tracks whether a close was requested while an operation was running
    private bool _pendingClose;
    private volatile int _processedOkCount;
    private PerformanceCounter? _readBytesCounter;

    // Statistics
    private volatile int _totalFilesProcessed;
    private bool _wasCancelled;
    private PerformanceCounter? _writeBytesCounter;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MainWindow" /> class.
    ///     Sets up services, checks for required executables, and initializes the UI.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _cts = new CancellationTokenSource();

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
            AppConfig.ChdmanExeCandidates
        );
        (_chdSharpExePath, _chdSharpResolvedName, _isChdSharpAvailable) = ResolveToolExecutable(
            appDirectory,
            AppConfig.ChdSharpExeCandidates
        );
        (_sevenZipExePath, _, var isSevenZipAvailable) = ResolveToolExecutable(
            appDirectory,
            AppConfig.SevenZipExeCandidates
        );

        // Initialize Services
        _updateService = new UpdateService(AppConfig.ApplicationName);
        _archiveService = new ArchiveService(_sevenZipExePath, isSevenZipAvailable);
        _screenshotService = new ScreenshotService();

        // Register global F8 hotkey once the window handle is available
        SourceInitialized += MainWindow_SourceInitialized;

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

        // Defer heavy initialization until after window is shown
        Loaded += MainWindow_LoadedAsync;

        // Hide speed display initially until we know counters are available
        SpeedStatCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    ///     Releases all resources used by the <see cref="MainWindow" />.
    ///     Cancels ongoing operations and disposes managed resources.
    /// </summary>
    public void Dispose()
    {
        if (_hwndSource != null)
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                    UnregisterHotKey(handle, HotkeyId);
            }
            catch (InvalidOperationException)
            {
                // Window handle already destroyed; skip hotkey cleanup
            }

            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

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

        KillOrphanedProcesses();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private async void MainWindow_LoadedAsync(object sender, RoutedEventArgs e)
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
            if (_writeBytesCounter != null || _readBytesCounter != null) SpeedStatCard.Visibility = Visibility.Visible;

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
        catch (Exception ex)
        {
            LogError("MainWindow_Loaded error", ex);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);

        var handle = new WindowInteropHelper(this).Handle;
        RegisterHotKey(handle, HotkeyId, 0, VkF8);
    }

    private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            try
            {
                var filePath = _screenshotService.TakeScreenshot();
                if (filePath != null)
                {
                    LogMessage($"Screenshot saved: {filePath}");
                    UpdateStatusBarMessage("Screenshot captured");
                }
                else
                {
                    LogMessage("Screenshot failed: could not capture active window.");
                    UpdateStatusBarMessage("Screenshot failed");
                }
            }
            catch (Exception ex)
            {
                LogError($"Screenshot error: {ex.Message}", ex);
            }

            handled = true;
        }

        return IntPtr.Zero;
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

        var preferred = candidateNames[0];
        return (Path.Combine(baseDirectory, preferred), preferred, false);
    }

    private void CheckDependenciesAndNotifyUser()
    {
        var missingDeps = new List<string>();
        if (!_isChdSharpAvailable) missingDeps.Add(_chdSharpResolvedName);

        if (!_isChdmanAvailable) missingDeps.Add(_chdmanResolvedName);

        // Critical dependency check
        if (missingDeps.Count > 0)
        {
            var msg =
                $"CRITICAL ERROR: The following required component is missing:\n\n"
                + $"{string.Join("\n", missingDeps)}\n\n"
                + $"Please ensure it is placed in the application folder.\n"
                + $"Download chdman from: https://github.com/rtissera/chdman/releases\n\n"
                + $"Conversion will NOT work without it.";

            LogError(" " + msg.Replace("\n", " "));
            ShowMessageBox(msg, "Missing Dependency", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static PerformanceCounter? CreateWritePerformanceCounter()
    {
        try
        {
            // Check if category exists first to avoid registry errors
            if (!PerformanceCounterCategory.Exists("PhysicalDisk")) return null;

            // Create a performance counter for disk write operations
            return new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        }
        catch (InvalidOperationException)
        {
            // System configuration issue - counters unavailable
            return null;
        }
        catch
        {
            // Best effort - return null if creation fails
            return null;
        }
    }

    private static PerformanceCounter? CreateReadPerformanceCounter()
    {
        try
        {
            // Check if category exists first to avoid registry errors
            if (!PerformanceCounterCategory.Exists("PhysicalDisk")) return null;

            // Create a performance counter for disk read operations
            return new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        }
        catch (InvalidOperationException)
        {
            // System configuration issue - counters unavailable
            return null;
        }
        catch
        {
            // Best effort - return null if creation fails
            return null;
        }
    }

    private void InitializeStatusBar()
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                StatusBarChdSharp.Text = " CHDSharp ";
                StatusBarChdSharp.Foreground = _isChdSharpAvailable
                    ? (Brush?)
                      Application.Current.FindResource("SuccessTextBrush")
                      ?? Brushes.Gray
                    : (Brush?)
                      Application.Current.FindResource("FailedTextBrush")
                      ?? Brushes.Gray;
                StatusBarChdman.Text = " CHDMAN ";
                StatusBarChdman.Foreground = _isChdmanAvailable
                    ? (Brush?)
                      Application.Current.FindResource("SuccessTextBrush")
                      ?? Brushes.Gray
                    : (Brush?)
                      Application.Current.FindResource("FailedTextBrush")
                      ?? Brushes.Gray;
                StatusBarMessage.Text = "Ready";
                SpeedValue.Text = "0.0 MB/s";
            }
            catch (Exception ex)
            {
                LogError("StatusBar Initialization Error", ex);
            }
        });
    }

    private static void CleanupLeftoverTempDirectories()
    {
        _ = Task.Run(static () =>
        {
            try
            {
                foreach (var basePath in PathUtils.GetPossibleTempBasePaths())
                    try
                    {
                        var directories = Directory.GetDirectories(basePath, $"{TempDirPrefix}*");
                        foreach (var dir in directories)
                            try
                            {
                                Directory.Delete(dir, true);
                            }
                            catch
                            {
                                /* ignore */
                            }
                    }
                    catch
                    {
                        /* ignore */
                    }
            }
            catch
            {
                /* ignore */
            }
        });
    }

    private void UpdateStatusBarMessage(string message)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() => StatusBarMessage.Text = message);
    }

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

            // Check if file has executable extension
            if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
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

            // Check for execution permissions by verifying file attributes
            var fileInfo = new FileInfo(exePath);
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReadOnly) && !IsRunningAsAdmin())
                // Read-only files can still be executed, but log a warning
                LogWarning($" {exeName} is read-only.");

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
    ///     Checks if the application is running with administrator privileges.
    /// </summary>
    private static bool IsRunningAsAdmin()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
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
                    "       The bundled chdman.exe is likely incompatible with this computer's CPU or was damaged/quarantined by antivirus software."
                );
                LogMessage(
                    "       Replace chdman.exe with a build that matches your CPU (e.g. an official MAME tools release) and add an antivirus exclusion for it."
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
                try
                {
                    process.Kill(true);
                    await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
                }
                catch
                {
                    // Best effort - ignore errors during cleanup
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
                try
                {
                    process.Kill(true);
                    await Task.Run(() => process.WaitForExit(5000), CancellationToken.None);
                }
                catch
                {
                    // Best effort - ignore errors during cleanup
                }

            // Other errors are acceptable - at least the exe started or we have a generic error
            LogWarning($"Could not validate chdman compatibility: {ex.Message}", ex);
            SafeFireAndForget(ReportBugAsync("Could not validate chdman compatibility", ex));
            return true;
        }
    }

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
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"CHDSharp executable: {_chdSharpResolvedName} ({(_isChdSharpAvailable ? "found" : "NOT FOUND")})"
            );
            LogMessage(sb.ToString());
        }
        catch
        {
            /* ignore */
        }
    }

    private void DisplayConversionInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Conversion Mode)");
        if (!_isChdSharpAvailable)
            LogWarning(
                " CHDSharp.exe not found! CHDSharp is the primary encoder. Place it in the application folder."
            );

        if (!_isChdmanAvailable)
            LogWarning(
                " chdman.exe not found! chdman is used as a fallback encoder. Download it from https://github.com/rtissera/chdman/releases and place it in the application folder."
            );

        LogMessage("--- Ready for Conversion ---");
    }

    private void DisplayVerificationInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Verification Mode)");

        LogMessage("--- Ready for Verification ---");
    }

    private void DisplayExtractionInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Extraction Mode)");

        LogMessage(
            "This feature extracts CHD files back to their original format (ISO/BIN/CUE etc.)"
        );
        LogMessage("--- Ready for Extraction ---");
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl control) return;

        if (!StartConversionButton.IsEnabled && !StartVerificationButton.IsEnabled) return;

        _ = Application.Current.Dispatcher.InvokeAsync((Action)(() => LogViewer.Clear()));
        if (control.SelectedItem is TabItem selectedTab)
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
            }

        UpdateWriteSpeedDisplay(0);
        UpdateReadSpeedDisplay(0);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // Check if any operation is currently running using thread-safe Interlocked check
        var isOperationRunning = Interlocked.CompareExchange(ref _operationRunningState, 0, 0) != 0;

        if (isOperationRunning)
            lock (_ctsLock)
            {
                if (!_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                    _pendingClose = true;
                    LogMessage("Cancelling operations before closing...");
                    UpdateStatusBarMessage("Cancelling...");
                    e.Cancel = true;
                    return;
                }
            }

        Dispose();

        Application.Current.Shutdown();
    }

    private void LogMessage(string message)
    {
        Log.Information(message);
        AppendToUiLog(message);
    }

    private void LogError(string message, Exception? ex = null)
    {
        Log.Error(ex, message.TrimStart());
        AppendToUiLog($"ERROR: {message.TrimStart()}");
    }

    private void LogWarning(string message, Exception? ex = null)
    {
        Log.Warning(ex, message.TrimStart());
        AppendToUiLog($"WARNING: {message.TrimStart()}");
    }

    private void AppendToUiLog(string message)
    {
        var timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
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

                LogViewer.AppendText($"{timestampedMessage}{Environment.NewLine}");
                LogViewer.ScrollToEnd();
            }
            catch
            {
                /* ignore logging errors */
            }
        });
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
            LogMessage($"Input folder set from command line: {path}");
            _ = LoadFilesForConversionAsync();
        }
        else
        {
            LogMessage($"Warning: Command line path does not exist: {path}");
        }
    }

    private void BrowseConversionInputButton_Click(object sender, RoutedEventArgs e)
    {
        HandleFolderBrowse(ConversionInputFolderTextBox, "Conversion input");
    }

    private void BrowseConversionOutputButton_Click(object sender, RoutedEventArgs e)
    {
        HandleFolderBrowse(ConversionOutputFolderTextBox, "Conversion output");
    }

    private void BrowseVerificationInputButton_Click(object sender, RoutedEventArgs e)
    {
        HandleFolderBrowse(VerificationInputFolderTextBox, "Verification input");
    }

    private void BrowseExtractionInputButton_Click(object sender, RoutedEventArgs e)
    {
        HandleFolderBrowse(ExtractionInputFolderTextBox, "Extraction input");
    }

    private void BrowseExtractionOutputButton_Click(object sender, RoutedEventArgs e)
    {
        HandleFolderBrowse(ExtractionOutputFolderTextBox, "Extraction output");
    }

    private async void StartExtractionButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync((Action)(() => LogViewer.Clear()));
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
            await Task.Yield();
            _operationTimer.Restart();
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

    private void HandleFolderBrowse(TextBox targetBox, string logName)
    {
        var folder = SelectFolder($"Select {logName} folder");
        if (string.IsNullOrEmpty(folder)) return;

        var normalized = PathUtils.ValidateAndNormalizePath(folder, logName, ShowError, LogMessage);
        if (normalized != null)
        {
            targetBox.Text = normalized;
            RefreshFileListForActiveTab();
        }

        if (targetBox == ConversionInputFolderTextBox && normalized != null)
        {
            _fileWatcher.StartWatching(normalized);
            if (_fileWatcher.IsWatching)
                LogMessage($"Monitoring input folder for file changes: {normalized}");
        }

        UpdateStatusBarMessage($"{logName} folder selected");
    }

    private void RefreshFileListForActiveTab()
    {
        if (MainTabControl.SelectedItem is TabItem selectedTab)
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

    private Task LoadFilesForConversionAsync()
    {
        var inputFolder = ConversionInputFolderTextBox.Text;
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) return Task.CompletedTask;

        var includeSub = SearchSubfoldersConversionCheckBox.IsChecked ?? false;

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
                    _cts.Token
                );

                var files = paths
                    .Select(f => new FileItem
                    {
                        FileName = Path.GetRelativePath(inputFolder, f),
                        FullPath = f,
                        FileSize = new FileInfo(f).Length,
                        IsSelected = true
                    })
                    .ToList();

                Application.Current.Dispatcher.Invoke(() => _conversionFiles.Clear());

                // Add items in chunks to avoid freezing the UI thread if there are thousands of files
                const int chunkSize = 100;
                try
                {
                    for (var i = 0; i < files.Count; i += chunkSize)
                    {
                        var chunk = files.Skip(i).Take(chunkSize).ToList();
                        await Application.Current.Dispatcher.InvokeAsync(
                            () =>
                            {
                                foreach (var item in chunk)
                                    _conversionFiles.Add(item);
                                TotalFilesValue.Text = _conversionFiles.Count.ToString(
                                    CultureInfo.InvariantCulture
                                );
                            },
                            DispatcherPriority.Background,
                            _cts.Token
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during chunked load is expected; partial results remain visible
                }
            },
            _cts.Token
        );
    }

    private Task LoadFilesForVerificationAsync()
    {
        var inputFolder = VerificationInputFolderTextBox.Text;
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) return Task.CompletedTask;

        var includeSub = SearchSubfoldersVerificationCheckBox.IsChecked ?? false;

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

                Application.Current.Dispatcher.Invoke(() => _verificationFiles.Clear());

                // Add items in chunks to avoid freezing the UI thread
                const int chunkSize = 100;
                try
                {
                    for (var i = 0; i < files.Count; i += chunkSize)
                    {
                        var chunk = files.Skip(i).Take(chunkSize).ToList();
                        await Application.Current.Dispatcher.InvokeAsync(
                            () =>
                            {
                                foreach (var item in chunk)
                                    _verificationFiles.Add(item);
                                TotalFilesValue.Text = _verificationFiles.Count.ToString(
                                    CultureInfo.InvariantCulture
                                );
                            },
                            DispatcherPriority.Background,
                            _cts.Token
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during chunked load is expected; partial results remain visible
                }
            },
            _cts.Token
        );
    }

    private Task LoadFilesForExtractionAsync()
    {
        var inputFolder = ExtractionInputFolderTextBox.Text;
        if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) return Task.CompletedTask;

        var includeSub = SearchSubfoldersExtractionCheckBox.IsChecked ?? false;

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

                Application.Current.Dispatcher.Invoke(() => _extractionFiles.Clear());

                // Add items in chunks to avoid freezing the UI thread
                const int chunkSize = 100;
                try
                {
                    for (var i = 0; i < files.Count; i += chunkSize)
                    {
                        var chunk = files.Skip(i).Take(chunkSize).ToList();
                        await Application.Current.Dispatcher.InvokeAsync(
                            () =>
                            {
                                foreach (var item in chunk)
                                    _extractionFiles.Add(item);
                                TotalFilesValue.Text = _extractionFiles.Count.ToString(
                                    CultureInfo.InvariantCulture
                                );
                            },
                            DispatcherPriority.Background,
                            _cts.Token
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during chunked load is expected; partial results remain visible
                }
            },
            _cts.Token
        );
    }

    private void SelectAllConversion_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _conversionFiles) f.IsSelected = true;
    }

    private void DeselectAllConversion_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _conversionFiles) f.IsSelected = false;
    }

    private void SelectAllVerification_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _verificationFiles) f.IsSelected = true;
    }

    private void DeselectAllVerification_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _verificationFiles) f.IsSelected = false;
    }

    private void SelectAllExtraction_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _extractionFiles) f.IsSelected = true;
    }

    private void DeselectAllExtraction_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _extractionFiles) f.IsSelected = false;
    }

    private async void StartConversionButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync((Action)(() => LogViewer.Clear()));
            DisplayConversionInstructionsInLog();

            if (!_isChdSharpAvailable && !_isChdmanAvailable)
            {
                ShowError(
                    $"Neither {AppConfig.ChdSharpExeName} nor {AppConfig.ChdmanExeName} was found. Place at least one encoder in the application folder."
                );
                return;
            }

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
                LogMessage(
                    " The output folder is inside the source folder, so CHDs will be written alongside the originals."
                );

            RenewCancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            await Task.Yield();
            _operationTimer.Restart();
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
                    ? (int?)mins
                    : null;

            var selectedFiles = _conversionFiles
                .Where(static f => f.IsSelected)
                .Select(static f => f.FullPath)
                .ToArray();
            if (selectedFiles.Length == 0)
            {
                ShowError("No files selected for conversion.");
                return;
            }

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

    private async void StartVerificationButton_ClickAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync((Action)(() => LogViewer.Clear()));
            DisplayVerificationInstructionsInLog();

            var inputFolder = PathUtils.ValidateAndNormalizePath(
                VerificationInputFolderTextBox.Text,
                "CHD Files Folder",
                ShowError,
                LogMessage
            );
            if (inputFolder == null) return;

            RenewCancellationTokenSource();

            ResetOperationStats();
            SetControlsState(false);
            await Task.Yield();
            _operationTimer.Restart();
            ResetSpeedCounters();

            var includeSubfolders = SearchSubfoldersVerificationCheckBox.IsChecked ?? false;
            var moveSuccess = MoveSuccessFilesCheckBox.IsChecked ?? false;
            var moveFailed = MoveFailedFilesCheckBox.IsChecked ?? false;
            var successFolder = moveSuccess ? Path.Combine(inputFolder, "Success") : string.Empty;
            var failedFolder = moveFailed ? Path.Combine(inputFolder, "Failed") : string.Empty;

            var selectedFiles = _verificationFiles
                .Where(static f => f.IsSelected)
                .Select(static f => f.FullPath)
                .ToArray();
            if (selectedFiles.Length == 0)
            {
                ShowError("No files selected for verification.");
                return;
            }

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

    private void FinishOperation(string opName)
    {
        _operationTimer.Stop();
        UpdateProcessingTimeDisplay();
        UpdateWriteSpeedDisplay(0);
        UpdateReadSpeedDisplay(0);
        SetControlsState(true);
        LogOperationSummary(opName);

        // Clear progress display
        ClearProgressDisplay();

        if (_pendingClose) Close();
    }

    private void RenewCancellationTokenSource()
    {
        lock (_ctsLock)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        lock (_ctsLock)
        {
            _cts.Cancel();
        }

        LogMessage("Cancellation requested...");
        UpdateStatusBarMessage("Cancelling...");
    }

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
        ProgressAreaGrid.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.IsIndeterminate = !enabled; // Start moving immediately
        CancelButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

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

    private static string? SelectFolder(string description)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = description };
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

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
        if (!await ValidateExecutableAccessAsync(chdmanPath, "chdman.exe"))
            return;

        if (!await ValidateChdmanCompatibilityAsync(chdmanPath, token))
            return;

        var filesToConvert = selectedFiles;

        if (processSmallerFirst)
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

        filesToConvert = ResolveOutputCollisions(filesToConvert, inputFolder, outputFolder);

        _totalFilesProcessed = filesToConvert.Length;
        UpdateStatsDisplay();
        LogMessage($"Found {_totalFilesProcessed} files to process.");
        if (_totalFilesProcessed == 0) return;

        CheckDiskSpace(outputFolder, filesToConvert, true);

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

        await Application.Current.Dispatcher.InvokeAsync(() =>
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

        await Application.Current.Dispatcher.InvokeAsync(() =>
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

        var ext = Path.GetExtension(inputFile);
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
            else if (ext.Equals(FileExtensions.Mds, StringComparison.OrdinalIgnoreCase))
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
                LogError(
                    $" Not enough disk space to process {originalName}. Free up disk space and try again."
                );
            else if (IsCorruptionException(ex))
                LogError($" Archive appears to be corrupt or unsupported: {originalName}");
            else
                LogError($"Processing {originalName}: {ex.Message}", ex);

            // The destination is deliberately left alone. A failure here says nothing about the CHD
            // already sitting at that path, which may be a good conversion from another input.
            return false;
        }
        finally
        {
            foreach (var tempDir in tempDirs)
                if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                    await TryDeleteDirectoryAsync(tempDir, "temp dir", CancellationToken.None);
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
            LogWarning(
                $" {name}: only {freeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB free on {driveName} for a {sourceBytes / (1024.0 * 1024.0 * 1024.0):F1} GB source. Proceeding, but the conversion will fail if it does not compress enough."
            );

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
        var ext = Path.GetExtension(chdmanInputPath);
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
                catch (Exception)
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
        var ext = Path.GetExtension(inputFile);

        // Descriptors are text and have their own handlers; there is nothing to sniff.
        if (
            ext
            is FileExtensions.Cue
            or FileExtensions.Gdi
            or FileExtensions.Toc
            or FileExtensions.Ccd
            or FileExtensions.Mds
        )
            return null;

        var kind = DiscImageSignature.Detect(inputFile);
        var extensionClaimsArchive = FileExtensions.ArchiveExtensionsSet.Contains(ext);
        var extensionClaimsIsz = ext.Equals(FileExtensions.Isz, StringComparison.OrdinalIgnoreCase);

        // An archive that really is an archive: leave it to the archive handler.
        if (extensionClaimsArchive && DiscImageSignature.IsArchive(kind)) return null;

        var volumeSet = SplitImageJoiner.TryGetVolumeSet(inputFile);
        if (volumeSet is not null)
            return await ResolveSplitVolumeSetAsync(
                volumeSet,
                originalName,
                outputFolder,
                tempDirs,
                token
            );

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
            // The extension is not lying about being a container, so the normal path applies.
            return null;

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

        // A multi-part archive is a different thing entirely and needs its own tooling.
        var firstKind = DiscImageSignature.Detect(firstVolume);
        if (DiscImageSignature.IsArchive(firstKind))
            return ResolvedInput.Skip(
                $"this is part 1 of a {volumeSet.Count}-part {DiscImageSignature.Describe(firstKind)}. Extract the set manually and convert the extracted image."
            );

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
            return ResolvedInput.Skip(
                "the file starts with an ISZ signature but its header could not be read, so it is damaged."
            );

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
    ///     or decompressed from ISZ - should be handed to chdman: as a CD with a generated cue, or as a
    ///     DVD image.
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
        var trackMode = RawCdImageDetector.DetectTrackMode(imagePath);
        if (trackMode is not null)
        {
            LogMessage(
                $" {description} holds raw CD sectors ({trackMode}); generating a cue for it."
            );
            var cuePath = await RawCdImageDetector.TryWriteCueAsync(
                imagePath,
                trackMode,
                workDir,
                token
            );

            return cuePath is not null
                ? ResolvedInput.Convert(cuePath, false)
                : ResolvedInput.Skip(
                    $"could not write a cue for the {description.ToLowerInvariant()}."
                );
        }

        if (IsCookedImageSize(imagePath))
        {
            LogMessage(
                $" {description} holds {MdsDisc.CookedSectorSize}-byte sectors; converting it as a DVD image."
            );
            return ResolvedInput.Convert(imagePath, true);
        }

        return ResolvedInput.Skip(misalignedReason);
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
            LogWarning(
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
            LogWarning(
                $" {originalName}: a generated cue could not reference the image relatively; converting the image as-is."
            );

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
        var ext = Path.GetExtension(inputFile);
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
    ///     Drops inputs whose output CHD path is already produced by another input in the batch,
    ///     keeping the first non-archive input of each colliding group. Converting both would only
    ///     overwrite one product with the other, so the redundant conversion (and, for archives,
    ///     the redundant extraction) is skipped up front and the resolution is logged.
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
            f => ComputeOutputChdPath(f, inputFolder, outputFolder)
        );

        foreach (var duplicate in skipped)
            LogMessage(
                $" {Path.GetFileName(duplicate.SkippedFile)} also converts to {Path.GetFileName(duplicate.OutputPath)}; skipping it because {Path.GetFileName(duplicate.KeptFile)} already targets the same output file."
            );

        return kept;
    }

    private static string ComputeOutputChdPathForExtractedFile(
        string extractedFilePath,
        string originalInputFile,
        string inputFolder,
        string outputFolder
    )
    {
        // Use the original input file (e.g. the archive) to determine the relative path
        var relativePath = PathUtils.GetSafeRelativePath(
            inputFolder,
            Path.GetDirectoryName(originalInputFile) ?? inputFolder
        );
        var targetDir = string.Equals(relativePath, ".", StringComparison.Ordinal)
            ? outputFolder
            : Path.Combine(outputFolder, relativePath);
        var chdBase = Path.GetFileNameWithoutExtension(extractedFilePath);
        return Path.Combine(targetDir, PathUtils.SanitizeFileName(chdBase) + FileExtensions.Chd);
    }

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

        var result = await _archiveService.ExtractCsoAsync(
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
            Path.GetExtension(inputFile),
            inputFolder,
            outputChd,
            deleteOriginal,
            token
        );
    }

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
            var extractedExt = Path.GetExtension(extractedFile);
            if (extractedExt is FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc)
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

            var extractedOutputDir = Path.GetDirectoryName(extractedFileOutputChd) ?? outputFolder;
            if (!Directory.Exists(extractedOutputDir))
                Directory.CreateDirectory(extractedOutputDir);

            LogMessage($"Converting extracted file: {Path.GetFileName(extractedFile)}");

            bool converted;
            if (extractedExt.Equals(FileExtensions.Ccd, StringComparison.OrdinalIgnoreCase))
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
            else if (extractedExt.Equals(FileExtensions.Mds, StringComparison.OrdinalIgnoreCase))
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
            else if (extractedExt.Equals(FileExtensions.Isz, StringComparison.OrdinalIgnoreCase))
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
            else
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
            // PSP homebrew / application EBOOT.PBPs have no PlayStation disc image to convert,
            // and truncated/corrupt PSX eboots report the same header error. Inform the user
            // without raising a bug report either way.
            if (result.ErrorCode == PbpError.InvalidPsarHeader)
            {
                LogMessage(
                    $" {originalName} does not contain a PlayStation disc image (PSP application, unsupported variant, or corrupt file) — skipping."
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
                    case PbpError.TruncatedPsar:
                        LogMessage(
                            "       The PlayStation data section has no readable tracks - the file is most likely truncated or incomplete. Re-download it."
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
                    await TryDeleteFileAsync(parsedDisc.ImgFilePath, "original IMG", token);
                if (parsedDisc.SubFilePath != null)
                    await TryDeleteFileAsync(parsedDisc.SubFilePath, "original SUB", token);

                var cdtPath = Path.ChangeExtension(inputFile, ".cdt");
                if (File.Exists(cdtPath))
                    await TryDeleteFileAsync(cdtPath, "original CDT", token);
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
                LogError(
                    $"CCDSharp: Conversion error - {ex.Message}. Ensure the .img file exists alongside the .ccd file with the same base name."
                );
            else
                LogError($"CCDSharp: Conversion error - {ex.Message}");

            return false;
        }
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

        // Stripping subchannel data writes a whole second copy of the disc, so the work directory
        // has to be chosen with room for it.
        long requiredBytes = 0;
        if (disc is { NeedsSubchannelStrip: true, MdfPath: not null })
            try
            {
                requiredBytes = new FileInfo(disc.MdfPath).Length;
            }
            catch
            {
                /* ignored */
            }

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
                LogError(
                    $" Not enough disk space to repack {originalName}. Free up space and try again."
                );
            else
                LogError($" Failed to prepare {originalName} for conversion: {ex.Message}", ex);

            return false;
        }

        if (!prepared.Success)
        {
            LogError($" {originalName} cannot be converted: {prepared.FailureReason}.");
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
                LogMessage(
                    $"KEEPING source: {originalName} (Conversion failed, skipping deletion for safety)"
                );

            return false;
        }

        LogMessage($"Converted: {originalName}");

        if (deleteOriginal)
        {
            LogMessage($"Deleting source: {originalName} (Option 'Delete originals' is enabled)");
            await TryDeleteFileAsync(inputFile, "original MDS", token);

            if (disc.MdfPath is not null)
                await TryDeleteFileAsync(disc.MdfPath, "original MDF", token);

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

        long requiredBytes = 0;
        if (disc is { NeedsSubchannelStrip: true, MdfPath: not null })
            try
            {
                requiredBytes = new FileInfo(disc.MdfPath).Length;
            }
            catch
            {
                /* ignored */
            }

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

    private async Task<bool> ValidateDependentFilesAsync(
        string ext,
        string inputFile,
        string originalName,
        CancellationToken token
    )
    {
        if (ext is not (FileExtensions.Cue or FileExtensions.Gdi or FileExtensions.Toc))
            return true;

        try
        {
            var missingNames = await GetMissingDependentFileNamesAsync(ext, inputFile, token);
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
        catch (Exception ex)
        {
            if (IsCancellationException(ex))
                throw;

            if (IsDiskSpaceException(ex))
                LogError(
                    $" Not enough disk space to convert {originalName}. Free up disk space and try again."
                );
            else
                LogError($"Direct conversion attempt error for {originalName}: {ex.Message}", ex);

            return false;
        }
    }

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
                try
                {
                    totalBytesNeeded += new FileInfo(file).Length;
                }
                catch
                {
                    /* skip */
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
        catch (Exception ex)
        {
            if (IsCancellationException(ex))
                throw;

            if (IsDiskSpaceException(ex))
                LogError(
                    $" Not enough disk space to convert {originalName} (via temp). Free up disk space and try again."
                );
            else if (IsCorruptionException(ex) || IsCrcErrorException(ex))
                LogError($" Source file appears to be corrupt: {originalName}");
            else
                LogError($"Retry via temp failed for {originalName}: {ex.Message}", ex);

            return false;
        }
    }

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
                    await DeleteOriginalGameFilesAsync(inputFile, token);
                else
                    await TryDeleteFileAsync(inputFile, "original file", token);

                var subfolder = Path.GetDirectoryName(inputFile);
                if (!string.IsNullOrEmpty(subfolder))
                    await TryDeleteEmptySubfolderAsync(subfolder, inputFolder, token);
            }

            return true;
        }

        if (deleteOriginal)
            LogMessage(
                $"KEEPING source: {originalName} (Conversion failed, skipping deletion for safety)"
            );

        // No delete at the destination. Conversions are staged and moved into place only on
        // success, so a failure leaves whatever was already there untouched - including a good
        // CHD produced by a different input that resolves to the same name.
        if (File.Exists(outputChd))
            LogMessage(
                $"KEEPING existing output: {Path.GetFileName(outputChd)} (not produced by this attempt)"
            );

        return false;
    }

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

        await Application.Current.Dispatcher.InvokeAsync(() =>
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
                    await MoveVerifiedFileAsync(
                        file,
                        successFolder,
                        inputFolder,
                        includeSub,
                        token
                    );
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

    private static async Task MoveVerifiedFileAsync(
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
                throw new IOException(
                    $"Could not move '{sourceFile}' to '{destFile}' after retries."
                );
        }
        catch (Exception ex)
        {
            // Log error but don't fail the verification
            SafeFireAndForget(ReportBugAsync($"Failed to move file {sourceFile}", ex));
        }
    }

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
                outputExt = FileExtensions.Gdi;
        }

        var outputFile = Path.Combine(targetDir, fileName + outputExt);

        // An extracted file takes the CHD's own base name, so extracting into a folder that already
        // holds a set of that name - most often the folder the CHD was made in - would replace it.
        // Rather than overwrite, or ask, the disc goes into a subfolder of its own name. Nothing is
        // lost and no decision is required; the log says where it went.
        if (extractCommand is "extractdvd" or "extracthd" && File.Exists(outputFile))
        {
            var isolatedDir = PathUtils.ReserveFreeSubdirectory(targetDir, fileName);
            Directory.CreateDirectory(isolatedDir);
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
                        if (extractCommand is "extractdvd" or "extracthd")
                            ExtractChdToSingleFile(chd, outputFile, token);
                        else
                            await ExtractChdTracksToDirectory(
                                chd,
                                chdFile,
                                targetDir,
                                fileName,
                                token
                            );
                    }

                    return true;
                },
                token
            );
        }
        catch (OperationCanceledException)
        {
            if (extractCommand is "extractdvd" or "extracthd")
                await TryDeleteFileAsync(
                    outputFile,
                    "partially extracted file",
                    CancellationToken.None
                );

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
                LogError(
                    $" Failed to extract '{Path.GetFileName(chdFile)}': {GetChdExtractionErrorMessage(ex.Message)}"
                );

                // CHDSharp could not decode this CHD (corrupt file, A/V laserdisc CHD, or a
                // library limitation). Fall back to chdman, which supports every CHD variant
                // (extractcd/dvd/hd, plus extractld/extractraw for laserdisc CHDs). The
                // CHDSharp failure above is still reported as a bug — the CHDSharp
                // maintainer wants extraction failures to reach the bug API.
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
                            $" Extracted '{Path.GetFileName(chdFile)}' using chdman fallback (built-in reader failed)."
                        );
                        success = true;
                    }
                    else
                    {
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
                await TryDeleteFileAsync(
                    outputFile,
                    "partially extracted file",
                    CancellationToken.None
                );
        }

        if (success && deleteOriginal) await TryDeleteFileAsync(chdFile, "original CHD file", token);

        return success;
    }

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
            chd.Read(offset, buffer, 0, toRead);
            fs.Write(buffer, 0, toRead);
            offset += (ulong)toRead;
            remaining -= (ulong)toRead;
        }
    }

    private async Task ExtractChdTracksToDirectory(
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
            var extractedFiles = chd.ExtractToDirectory(tempExtractDir, baseFileName);

            if (extractedFiles.Count == 0)
                throw new InvalidOperationException(
                    $"No files extracted from '{Path.GetFileName(chdFile)}'."
                );

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
                        throw new IOException(
                            $"Could not delete existing destination '{destPath}'."
                        );
                }

                var moved = await RetryingFileOperations
                    .TryMoveAsync(srcPath, destPath, token)
                    .ConfigureAwait(false);
                if (!moved)
                    throw new IOException(
                        $"Failed to move extracted file '{srcPath}' to '{destPath}'."
                    );

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
                            try
                            {
                                File.Delete(leftover);
                                cleanedCount++;
                            }
                            catch
                            {
                                // ignored; reported below if it truly remains
                            }

                        Directory.Delete(tempExtractDir, true);
                    }
                }
                catch
                {
                    // ignored
                }

                if (cleanedCount > 0)
                    Log.Debug(
                        "Cleaned up {Count} leftover file(s) from failed extraction of {File}",
                        cleanedCount,
                        Path.GetFileName(chdFile)
                    );

                try
                {
                    var remaining = Directory.Exists(tempExtractDir)
                        ? Directory.GetFiles(tempExtractDir, "*.*", SearchOption.AllDirectories)
                        : [];
                    if (remaining.Length > 0)
                        LogWarning(
                            $" Partial extraction: {remaining.Length} file(s) remain in temp directory: {tempExtractDir}"
                        );
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

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
                            if (
                                meta.ToString()
                                .Contains("gd-rom", StringComparison.OrdinalIgnoreCase)
                            )
                                return true;
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
                                return "extracthd";
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
        catch (Exception ex)
        {
            if (IsCancellationException(ex))
                throw;

            // chdman cannot read MP3 tracks at all, so a failed work-dir preparation for an MP3
            // cue must not fall through to a direct chdman attempt ("Unhandled track type MP3").
            if (await CueHasMp3TracksAsync(cuePath, token))
                LogError(
                    $" MP3 audio track could not be decoded to WAV for {Path.GetFileName(cuePath)}: {ex.Message}. The MP3 track(s) may be corrupt or in an unsupported format."
                );
            else
                LogMessage(
                    $" Cue normalization failed for {Path.GetFileName(cuePath)}: {ex.Message}"
                );

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
            LogMessage(
                $" Prepared self-contained cue set for {Path.GetFileName(cuePath)} in a temporary directory."
            );

        return (work.WorkCuePath, work.WorkDir);
    }

    /// <summary>
    ///     Runs an encoder process (CHDSharp or chdman) with the given arguments and waits for completion.
    ///     Returns true if the process exited successfully (exit code 0) and was not cancelled/timed out.
    /// </summary>
    private async Task<bool> RunEncoderProcessAsync(
        string exePath,
        string args,
        string toolLabel,
        int? timeoutMinutes,
        CancellationToken token
    )
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exePath,
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
                LogMessage($"[{toolLabel} ✓] {a.Data}");
            else if (
                !a.Data.Contains("% complete", StringComparison.Ordinal)
                && !a.Data.Contains("Compressing", StringComparison.Ordinal)
                && !a.Data.Contains("Output bytes", StringComparison.Ordinal)
                && !a.Data.Contains("Compression ratio", StringComparison.Ordinal)
            )
                LogMessage($"[{toolLabel}] {a.Data}");
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
                LogMessage($"[{toolLabel} ✓] {a.Data}");
            else if (
                !a.Data.Contains("% complete", StringComparison.Ordinal)
                && !a.Data.Contains("Compressing", StringComparison.Ordinal)
                && !a.Data.Contains("Output bytes", StringComparison.Ordinal)
                && !a.Data.Contains("Compression ratio", StringComparison.Ordinal)
            )
                LogMessage($"[{toolLabel}] {a.Data}");
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            if (IsCancellationException(ex))
                throw;
            LogError($" Failed to start {toolLabel}: {ex.Message}");
            return false;
        }

        // Sample the system-wide write speed for as long as the encoder runs, so the speed
        // stat card shows live MB/s regardless of which CLI engine is driving the conversion.
        // (The chdman path in ConvertToChdAsync does the same; this shared runner used to skip
        // it, which froze the display at 0.0 MB/s while CHDSharp was converting.)
        using var ctsSpeed = CancellationTokenSource.CreateLinkedTokenSource(token);
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
                throw;

            if (timeoutMinutes != null)
                LogMessage(
                    $"TIMEOUT: {toolLabel} conversion exceeded {timeoutMinutes.Value} minute(s). Marking as failed."
                );

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
        }

        return process.ExitCode == 0 && !token.IsCancellationRequested;
    }

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
        if (!File.Exists(chdmanPath))
        {
            LogError(
                $" chdman.exe not found at '{chdmanPath}'. Download it from https://github.com/rtissera/chdman/releases and place it in the application folder."
            );
            return false;
        }

        // An .img sitting next to a .cue of the same name is the data half of a cue/bin pair. The cue
        // is the only file that carries the track layout, so hand chdman the cue: passing the raw
        // image instead selects createhd and reports "Data size ... is not divisible by sector size
        // 512". This also routes the input through the cue work-directory preparation below.
        var companionCue = Path.ChangeExtension(inputFile, FileExtensions.Cue);
        if (
            inputFile.EndsWith(FileExtensions.Img, StringComparison.OrdinalIgnoreCase)
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
                args += " -us 2352";
        }

        string? asciiTempDir = null;
        string? asciiInputFile = null;
        string? asciiOutputFile = null;
        var originalInputFile = inputFile;
        var originalOutputFile = outputFile;
        var usedAsciiStaging = false;

        // Warn early about likely-corrupt disc images, but still let chdman try: some
        // legitimate images use non-standard sector layouts (e.g. 2448-byte sectors with
        // subchannel data) that chdman can convert. The post-failure check remains the hard gate.
        if (string.Equals(command, "createdvd", StringComparison.Ordinal))
        {
            var sectorWarning = IsoSectorValidator.GetSectorSizeWarning(originalInputFile);
            if (sectorWarning is not null)
                LogWarning(
                    $" {Path.GetFileName(originalInputFile)}: {sectorWarning} Proceeding with conversion anyway."
                );
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
            usedAsciiStaging = true;

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

        // --- Primary encoder: CHDSharp ---
        if (_isChdSharpAvailable && File.Exists(_chdSharpExePath))
        {
            LogMessage($"CHDSharp: {command} {Path.GetFileName(originalInputFile)}");

            var chdSharpSuccess = await RunEncoderProcessAsync(
                _chdSharpExePath,
                args,
                "CHDSharp",
                timeoutMinutes,
                token
            );

            if (chdSharpSuccess)
            {
                // CHDSharp internally validates its output; trust the exit code.
                if (asciiOutputFile != null)
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
                                throw new IOException(
                                    $"Could not delete existing destination '{originalOutputFile}'."
                                );
                        }

                        var moved = await RetryingFileOperations
                            .TryMoveAsync(outputFile, originalOutputFile, token)
                            .ConfigureAwait(false);
                        if (!moved)
                            throw new IOException(
                                $"Could not move temp output '{outputFile}' to '{originalOutputFile}'."
                            );
                    }
                    catch (Exception ex)
                    {
                        LogError($" Failed to move CHDSharp output to destination: {ex.Message}");
                        TryCleanupAsciiTemp();
                        return false;
                    }

                TryCleanupAsciiTemp();
                return true;
            }

            LogWarning(
                $"CHDSharp failed for '{Path.GetFileName(originalInputFile)}'. Falling back to chdman..."
            );
            TryCleanupAsciiTemp();

            // Re-create staging for chdman fallback
            if (usedAsciiStaging)
            {
                asciiTempDir = PathUtils.CreateAsciiSafeTempDirectory(TempDirPrefix);
                Directory.CreateDirectory(asciiTempDir);
                if (pathNeedsAscii)
                {
                    asciiInputFile = Path.Combine(
                        asciiTempDir,
                        Guid.NewGuid().ToString("N") + Path.GetExtension(originalInputFile)
                    );
                    File.Copy(originalInputFile, asciiInputFile);
                    inputFile = asciiInputFile;
                }

                asciiOutputFile = Path.Combine(
                    asciiTempDir,
                    Guid.NewGuid().ToString("N") + FileExtensions.Chd
                );
                outputFile = asciiOutputFile;
            }
            else
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
                }
            }

            args = $"{command} -i \"{inputFile}\" -o \"{outputFile}\" -f -np {cores}";
            if (isRaw)
            {
                args += " -us 2352";
            }
            else if (
                string.Equals(command, "createcd", StringComparison.Ordinal) && isCueDescriptor
            )
            {
                var refs = await GameFileParser
                    .GetReferencedFilesFromCueAsync(inputFile, static _ => { }, token)
                    .ConfigureAwait(false);
                if (
                    refs.Any(static r =>
                        r.EndsWith(FileExtensions.Raw, StringComparison.OrdinalIgnoreCase)
                    )
                )
                    args += " -us 2352";
            }
        }

        // --- Fallback encoder: chdman ---
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
                LogMessage($"[CHDMAN ✓] {a.Data}");
            else if (
                !a.Data.Contains("% complete", StringComparison.Ordinal)
                && !a.Data.Contains("Compressing", StringComparison.Ordinal)
                && !a.Data.Contains("Output bytes", StringComparison.Ordinal)
                && !a.Data.Contains("Compression ratio", StringComparison.Ordinal)
            )
                LogMessage($"[CHDMAN] {a.Data}");
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
                LogMessage($"[CHDMAN ✓] {a.Data}");
            else if (
                !a.Data.Contains("% complete", StringComparison.Ordinal)
                && !a.Data.Contains("Compressing", StringComparison.Ordinal)
                && !a.Data.Contains("Output bytes", StringComparison.Ordinal)
                && !a.Data.Contains("Compression ratio", StringComparison.Ordinal)
            )
                LogMessage($"[CHDMAN] {a.Data}");
        };

        using var ctsSpeed = CancellationTokenSource.CreateLinkedTokenSource(token);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            if (IsCancellationException(ex))
                throw;

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
                LogMessage(
                    $"TIMEOUT: Conversion of '{Path.GetFileName(inputFile)}' exceeded {timeoutMinutes.Value} minute(s). Marking as failed."
                );
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
                    try
                    {
                        var outputSize = new FileInfo(outputFile).Length;
                        if (outputSize > 0)
                        {
                            LogMessage(
                                $" chdman exited with code {exitCode} but produced a valid output file ({outputSize} bytes). Treating as success."
                            );
                            success = true;
                        }
                    }
                    catch
                    {
                        // ignored
                    }
            }

            if (success)
            {
                if (asciiOutputFile != null)
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
                                throw new IOException(
                                    $"Could not delete existing destination '{originalOutputFile}'."
                                );
                        }

                        var moved = await RetryingFileOperations
                            .TryMoveAsync(outputFile, originalOutputFile, token)
                            .ConfigureAwait(false);
                        if (!moved)
                            throw new IOException(
                                $"Could not move temp output '{outputFile}' to '{originalOutputFile}'."
                            );
                    }
                    catch (Exception ex)
                    {
                        LogError($" Failed to move temp output to destination: {ex.Message}");
                        success = false;
                    }

                if (success)
                    return true;
            }

            if (token.IsCancellationRequested) return false;

            var errorTextFinal = errorBuffer.ToString().TrimEnd();

            try
            {
                var effectiveInput = asciiInputFile ?? originalInputFile;
                var inputExt = Path.GetExtension(effectiveInput);

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
                    LogWarning(
                        $"       Files found in input directory ({Path.GetDirectoryName(originalInputFile) ?? "?"}): {GetDirectoryDiagnostics(originalInputFile)}"
                    );

                if (errorLine.Contains("Unknown error", StringComparison.OrdinalIgnoreCase))
                    LogMessage(
                        "       'Unknown error' from chdman typically indicates a corrupt source file, an unsupported disc format, or an I/O issue. Try converting the file from a local drive."
                    );

                if (errorLine.Contains("Input/output error", StringComparison.OrdinalIgnoreCase))
                    LogMessage(
                        "       An input/output error while reading the source usually means a failing or disconnected drive, a file locked by antivirus or cloud sync, or a damaged disc image. Check the drive for errors and try converting from a local drive."
                    );
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
                    "       The bundled chdman.exe may be incompatible with this computer's CPU or was damaged/quarantined by antivirus software."
                );
                LogMessage(
                    "       Replace chdman.exe with a build that matches your CPU (e.g. an official MAME tools release) and add an antivirus exclusion for it."
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
                continue;

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
            -1073740791 => "; 0xC0000409, stack buffer overrun / fail fast",
            _ => string.Empty
        };
    }

    /// <summary>
    ///     Maps CHDSharp extraction exception messages to user-friendly text. Decompression failures
    ///     ("Failed to read hunk N", Chderrdecompressionerror) occur when a CHD is corrupt or uses the
    ///     A/V (laserdisc) codec variant that the built-in reader cannot decode; the message says so
    ///     instead of showing a cryptic codec error, and the extraction pipeline then retries with chdman.
    /// </summary>
    internal static string GetChdExtractionErrorMessage(string? message)
    {
        message ??= string.Empty;

        if (
            message.Contains("Chderrdecompressionerror", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Failed to read hunk", StringComparison.OrdinalIgnoreCase)
        )
            return message
                   + " The CHD file may be corrupt, or it may be an A/V (laserdisc) CHD, which the built-in reader cannot decode. Retrying with chdman...";

        return message;
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
                        LogMessage(
                            string.Equals(outputExt, FileExtensions.Gdi, StringComparison.Ordinal)
                                ? $" Extracted: {Path.GetFileName(outputPath)} and {Path.GetFileName(Path.ChangeExtension(outputPath, FileExtensions.Bin))} (chdman fallback writes CUE/BIN; the GDI descriptor requires the built-in reader)"
                                : $" Extracted: {Path.GetFileName(outputPath)} and {Path.GetFileName(Path.ChangeExtension(outputPath, FileExtensions.Bin))}"
                        );
                    else
                        LogMessage($" Extracted: {Path.GetFileName(outputPath)}");

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
        try
        {
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
                        return (
                            Success: false,
                            CuePaths: new List<string>(),
                            Error: $"Failed to open PBP file: {error} (code {(int)error})",
                            ErrorCode: error
                        );

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
                                return (
                                    Success: false,
                                    CuePaths: new List<string>(),
                                    Error:
                                    $"Failed to extract disc {t.Index} of {pbpFile.Discs.Count}: {extractError} (code {(int)extractError})",
                                    ErrorCode: extractError
                                );

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

    private void UpdateStatsDisplay()
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            TotalFilesValue.Text = $"{_totalFilesProcessed}";
            SuccessValue.Text = $"{_processedOkCount}";
            FailedValue.Text = $"{_failedCount}";
        });
    }

    private void UpdateProcessingTimeDisplay()
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
            ProcessingTimeValue.Text = $@"{_operationTimer.Elapsed:hh\:mm\:ss}"
        );
    }

    private void UpdateWriteSpeedDisplay(double speed)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Update the actual label
            SpeedValue.Text = $"{speed:F1} MB/s";

            if (speed > 0 && !StartConversionButton.IsEnabled) StatusBarMessage.Text = "Converting...";
        });
    }

    private void UpdateReadSpeedDisplay(double speed)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SpeedValue.Text = $"{speed:F1} MB/s";
            StatusBarMessage.Text = speed switch
            {
                > 0 when !StartExtractionButton.IsEnabled => "Extracting...",
                > 0 when !StartVerificationButton.IsEnabled => "Verifying...",
                _ => StatusBarMessage.Text
            };
        });
    }

    private void UpdateProgressDisplay(int completedCount, int tot, string name, string verb)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
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
            ProgressText.Visibility = Visibility.Visible;
            ProgressBar.Visibility = Visibility.Visible;
        });
    }

    private void ClearProgressDisplay()
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ProgressBar.Value = 0;
            ProgressBar.Visibility = Visibility.Collapsed;
            ProgressText.Text = "";
            ProgressText.Visibility = Visibility.Collapsed;
        });
    }

    private async Task DeleteOriginalGameFilesAsync(string inputFile, CancellationToken token)
    {
        try
        {
            var files = new List<string> { inputFile };
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

            foreach (var f in files.Distinct(StringComparer.Ordinal))
                await TryDeleteFileAsync(f, "game file", token);
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
        return ex is IOException { HResult: -2147024784 or -2147024783 };
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
                   ex.HResult == -2147024809
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

    private static bool IsIoError(string? errorOutput)
    {
        if (string.IsNullOrEmpty(errorOutput))
            return false;

        return errorOutput.Contains("Input/output error", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("read error", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("write error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPermissionError(string? errorOutput)
    {
        if (string.IsNullOrEmpty(errorOutput))
            return false;

        return errorOutput.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
               || errorOutput.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase);
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
                try
                {
                    totalInputSize += new FileInfo(file).Length;
                }
                catch
                {
                    /* skip inaccessible files */
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

    private static void KillChdmanProcesses()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName("chdman"))
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
                    // ignored
                }
        }
        catch
        {
            // ignored
        }
    }

    private async Task TryDeleteDirectoryAsync(string path, string desc, CancellationToken token)
    {
        for (var attempt = 0; attempt < MaxFileOperationRetries; attempt++)
            try
            {
                await Task.Run(() => Directory.Delete(path, true), token);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                LogMessage($"{desc} already deleted: {Path.GetFileName(path)}");
                return;
            }
            catch when (attempt < MaxFileOperationRetries - 1)
            {
                await Task.Delay(500 * (attempt + 1), token);
            }

        LogError($"Failed to delete {desc}: {path}");
    }

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
                return;

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

    private void SearchSubfoldersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RefreshFileListForActiveTab();
    }

    private void ForceCreateCdCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        ForceCreateDvdCheckBox.IsChecked = false;
    }

    private void ForceCreateDvdCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        ForceCreateCdCheckBox.IsChecked = false;
    }

    private void LogOperationSummary(string op)
    {
        var verb = _wasCancelled ? "canceled" : "completed";
        LogMessage(
            $"--- {op} {verb}. Total: {_totalFilesProcessed}, OK: {_processedOkCount}, Failed: {_failedCount}"
        );
        UpdateStatusBarMessage($"{op} {verb}" + (_failedCount > 0 ? " with errors" : ""));
        ShowMessageBox(
            $"{op} {verb}.\nTotal: {_totalFilesProcessed}\nOK: {_processedOkCount}\nFailed: {_failedCount}",
            "Complete",
            MessageBoxButton.OK,
            _failedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information
        );
    }

    private void ShowMessageBox(
        string msg,
        string title,
        MessageBoxButton btns,
        MessageBoxImage icon
    )
    {
        MessageBox.Show(this, msg, title, btns, icon);
    }

    private void ShowError(string msg)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
            ShowMessageBox(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error)
        );
    }

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

    private void ResetSpeedCounters()
    {
        // Reset performance counters to get fresh readings
        lock (_performanceCounterLock)
        {
            _writeBytesCounter?.NextValue();
            _readBytesCounter?.NextValue();
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // Ensure the window close process is initiated
        // The Window_Closing event will handle proper cleanup and shutdown
        Close();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

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

    private static void KillOrphanedProcesses()
    {
        try
        {
            var currentProcessId = Environment.ProcessId;
            var toolNames = new[] { "chdman", "7za", AppConfig.SevenZipExeName };

            foreach (var toolName in toolNames)
                try
                {
                    var processes = Process.GetProcessesByName(
                        Path.GetFileNameWithoutExtension(toolName)
                    );
                    foreach (var process in processes)
                        try
                        {
                            if (process.Id != currentProcessId)
                            {
                                process.Kill(true);
                                process.WaitForExit(3000);
                            }
                        }
                        catch
                        {
                            // Process already exited or access denied
                        }
                }
                catch
                {
                    // Process name not found or access denied
                }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    /// <summary>
    ///     What content inspection decided about an input: something to convert, or a reason to skip.
    /// </summary>
    /// <param name="PathToConvert">File to hand chdman, or null when skipping.</param>
    /// <param name="ForceDvd">True when the resolved file must be converted as a DVD image.</param>
    /// <param name="SkipReason">User-facing explanation, or null when there is something to convert.</param>
    private sealed record ResolvedInput(string? PathToConvert, bool ForceDvd, string? SkipReason)
    {
        internal static ResolvedInput Convert(string path, bool forceDvd)
        {
            return new ResolvedInput(path, forceDvd, null);
        }

        internal static ResolvedInput Skip(string reason)
        {
            return new ResolvedInput(null, false, reason);
        }
    }
}