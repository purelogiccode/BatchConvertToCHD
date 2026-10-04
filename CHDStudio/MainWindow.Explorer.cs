using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CHDStudio.Models;
using CHDStudio.Services;
using CHDStudio.Utilities;
using VideoGameFileSystemParser.Models;

namespace CHDStudio;

/// <summary>
///     Explorer tab: opens a CHD image, parses its file system with the selected
///     VideoGameFileSystemParser console parser, and lets the user browse and extract its contents.
/// </summary>
internal partial class MainWindow
{
    private const string ExplorerTempFolderName = "Explorer";

    private readonly CancellationTokenSource _explorerCts = new();
    private readonly Lock _explorerLock = new();

    // Serializes explorer use with explorer retirement: the container is only disposed once any
    // in-flight background read or extraction using it has finished.
    private readonly SemaphoreSlim _explorerUseLock = new(1, 1);

    // Serializes whole open/replace operations so two overlapping opens cannot leak the
    // ChdExplorerService that loses the race.
    private readonly SemaphoreSlim _explorerOpenLock = new(1, 1);

    /// <summary>Set when the window has closed and no new explorer may be assigned.</summary>
    private bool _explorerDisposed;

    /// <summary>Directory currently shown in the Explorer grid (root is <c>"/"</c>).</summary>
    private string _explorerCurrentPath = "/";

    /// <summary>Set once the tab controls exist and the parser list is bound.</summary>
    private bool _explorerInitialized;

    /// <summary>Whether the log column is currently collapsed for the Explorer tab.</summary>
    private bool _explorerLayoutActive;

    /// <summary>Log column width saved before the Explorer tab collapsed it.</summary>
    private GridLength _explorerSavedLogWidth = new(1, GridUnitType.Star);

    /// <summary>Splitter column width saved before the Explorer tab collapsed it.</summary>
    private GridLength _explorerSavedSplitterWidth = new(10);

    /// <summary>Display name of the parser the open image was parsed with, for status messages.</summary>
    private string _explorerParserName = string.Empty;

    /// <summary>The explorer for the image currently open, or <see langword="null" /> when none is.</summary>
    private ChdExplorerService? _explorerService;

    /// <summary>
    ///     Binds the parser drop-down and wires window lifetime events. Called from the main
    ///     constructor once every named control exists.
    /// </summary>
    private void InitializeExplorerTab()
    {
        // Three registry rows map to GenericIsoRaw2352; keep one row per enum value (the last
        // occurrence carries the generic "ISO RAW 2352" name).
        ExplorerParserComboBox.ItemsSource = ConsoleTypeRegistry.All
            .GroupBy(static entry => entry.Type)
            .Select(static group => group.Last())
            .ToList();

        // PlayStation (Auto) is the best default: it parses ISO 9660 on both CD and DVD CHDs,
        // where the plain ISO 9660 parser needs a CD track table that DVD images do not have.
        ExplorerParserComboBox.SelectedItem =
            ConsoleTypeRegistry.All.FirstOrDefault(static entry => entry.Type == ConsoleType.PlayStation
            );

        Closed += (_, _) => DisposeExplorer();
        _explorerInitialized = true;
        UpdateExplorerUiState();
    }

    /// <summary>
    ///     Collapses the log column (and its splitter) while the Explorer tab is active so the
    ///     browser uses the full window width, and restores the saved widths afterwards.
    /// </summary>
    /// <param name="explorerActive">Whether the Explorer tab is the selected tab.</param>
    private void SetExplorerLayout(bool explorerActive)
    {
        if (explorerActive == _explorerLayoutActive) return;

        var columns = ContentColumnsGrid.ColumnDefinitions;
        if (explorerActive)
        {
            _explorerSavedSplitterWidth = columns[1].Width;
            _explorerSavedLogWidth = columns[2].Width;
            columns[1].Width = new GridLength(0);
            columns[2].Width = new GridLength(0);
            LogSplitter.IsVisible = false;

            // A zero-width column alone still lets the unclipped log control draw over the tab at
            // the window edge, so the panel itself is hidden while the Explorer tab is active.
            LogPanelBorder.IsVisible = false;
        }
        else
        {
            columns[1].Width = _explorerSavedSplitterWidth;
            columns[2].Width = _explorerSavedLogWidth;
            LogSplitter.IsVisible = true;
            LogPanelBorder.IsVisible = true;
        }

        _explorerLayoutActive = explorerActive;
    }

    /// <summary>
    ///     Writes the Explorer tab instructions to the activity log when the tab is selected.
    /// </summary>
    private void DisplayExplorerInstructionsInLog()
    {
        LogMessage($"Welcome to {AppConfig.ApplicationName}. (Explorer Mode)");
        LogMessage("Open a CHD image, then choose the file system parser matching its console/system.");
        LogMessage("Double-click a folder to browse it; double-click a file to extract and open it.");
        LogMessage("--- Ready to explore ---");
    }

    /// <summary>
    ///     Opens a CHD file picker and loads the selected image into the Explorer tab.
    /// </summary>
    // ReSharper disable once UnusedMember.Local
    private async void BrowseExplorerChdButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storageProvider = GetTopLevel(this)?.StorageProvider;
            if (storageProvider is null) return;

            var files = await storageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Select a CHD image to explore",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("CHD image") { Patterns = new[] { "*.chd" } },
                        FilePickerFileTypes.All
                    ]
                }
            );

            if (files.Count == 0) return;

            var chdPath = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(chdPath))
            {
                ExplorerStatusTextBlock.Text = "The selected file is not available on the local file system.";
                return;
            }

            ExplorerChdPathTextBox.Text = chdPath;
            await OpenExplorerChdAsync(chdPath);
        }
        catch (Exception ex)
        {
            LogError($"Explorer: failed to open the CHD file picker: {ex.Message}", ex);
            ShowError($"Failed to open the CHD file picker: {ex.Message}");
        }
    }

    /// <summary>
    ///     Re-parses the open image when the user picks a different file system parser.
    /// </summary>
    // ReSharper disable once UnusedMember.Local
    private async void ExplorerParserComboBox_SelectionChangedAsync(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (!_explorerInitialized) return;

            var chdPath = ExplorerChdPathTextBox.Text;
            if (string.IsNullOrEmpty(chdPath)) return;

            await OpenExplorerChdAsync(chdPath);
        }
        catch (Exception ex)
        {
            LogError($"Explorer: failed to re-parse the open image: {ex.Message}", ex);
            ExplorerStatusTextBlock.Text = $"Failed to re-parse the open image: {ex.Message}";
        }
    }

    /// <summary>
    ///     Navigates one directory level up in the open image.
    /// </summary>
    // ReSharper disable once UnusedMember.Local
    private async void ExplorerUpButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (string.Equals(_explorerCurrentPath, "/", StringComparison.Ordinal)) return;

            await LoadExplorerDirectoryAsync(GetExplorerParentPath(_explorerCurrentPath));
        }
        catch (Exception ex)
        {
            LogError($"Explorer: failed to navigate up: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Enters a directory or extracts and opens a file on double-click.
    /// </summary>
    // ReSharper disable once UnusedMember.Local
    private async void ExplorerEntriesDataGrid_DoubleTappedAsync(object? sender, TappedEventArgs e)
    {
        try
        {
            if (ExplorerEntriesDataGrid.SelectedItem is not ChdExplorerItem item) return;

            if (item.IsDirectory)
            {
                await LoadExplorerDirectoryAsync(item.Entry.FullPath);
            }
            else
            {
                await ExtractAndOpenExplorerFileAsync(item);
            }
        }
        catch (Exception ex)
        {
            LogError($"Explorer: double-click action failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Keeps the Extract button state in sync with the grid selection.
    /// </summary>
    // ReSharper disable once UnusedMember.Local
    private void ExplorerEntriesDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ExplorerExtractButton.IsEnabled =
            _explorerService is not null && ExplorerEntriesDataGrid.SelectedItem is ChdExplorerItem;
    }

    /// <summary>
    ///     Extracts the selected entry (file or directory) to a folder chosen by the user.
    /// </summary>
    // ReSharper disable once UnusedMember.Local
    private async void ExplorerExtractButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ExplorerEntriesDataGrid.SelectedItem is not ChdExplorerItem item) return;

            var destination = await SelectFolderAsync($"Select the folder to extract '{item.Name}' to");
            if (string.IsNullOrEmpty(destination)) return;

            // Never replace an existing file or folder of the same name: extract into a numbered
            // subfolder instead, the same isolation the Extraction tab applies. Otherwise a disc's
            // GAME.BIN could silently overwrite the original image it was converted from.
            var targetPath = Path.Combine(destination, item.Name);
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                destination = PathUtils.ReserveFreeSubdirectory(destination, item.Name);
                Directory.CreateDirectory(destination);
                LogMessage(
                    $"Explorer: '{item.Name}' already exists in the chosen folder; extracting into '{Path.GetFileName(destination)}' instead."
                );
            }

            SetExplorerBusy(true, $"Extracting {item.Name}...");
            try
            {
                var extractedPath = await ExtractExplorerEntryAsync(item, destination, _explorerCts.Token);
                if (extractedPath is null) return;

                LogMessage($"Explorer: extracted '{item.Name}' to '{extractedPath}'.");
                ExplorerStatusTextBlock.Text = $"Extracted: {extractedPath}";
            }
            finally
            {
                SetExplorerBusy(false, null);
            }
        }
        catch (OperationCanceledException)
        {
            // Window is closing; nothing to report.
        }
        catch (Exception ex)
        {
            LogError($"Explorer: extraction failed: {ex.Message}", ex);
            ShowError($"Failed to extract the selected item: {ex.Message}");
        }
    }

    /// <summary>
    ///     Opens (or re-opens) a CHD image with the parser currently selected in the drop-down.
    ///     Open operations are serialized so overlapping calls cannot leak the service that loses
    ///     the race.
    /// </summary>
    /// <param name="chdPath">Path of the CHD image to open.</param>
    private async Task OpenExplorerChdAsync(string chdPath)
    {
        await _explorerOpenLock.WaitAsync();
        try
        {
            await OpenExplorerChdCoreAsync(chdPath);
        }
        finally
        {
            _explorerOpenLock.Release();
        }
    }

    /// <summary>
    ///     Opens a CHD image and installs the resulting explorer, retiring any previous one.
    /// </summary>
    /// <param name="chdPath">Path of the CHD image to open.</param>
    private async Task OpenExplorerChdCoreAsync(string chdPath)
    {
        var parser = ExplorerParserComboBox.SelectedItem as ConsoleTypeInfo;
        var consoleType = parser?.Type ?? ConsoleType.GenericIso9660;
        var parserName = parser?.DisplayName ?? consoleType.ToString();

        await RetireExplorerAsync();

        ExplorerInfoTextBox.Text = string.Empty;

        SetExplorerBusy(true, $"Opening '{Path.GetFileName(chdPath)}' as {parserName}...");
        LogMessage($"Explorer: opening '{chdPath}' with the {parserName} parser...");

        ChdExplorerService? service = null;
        string? error = null;
        try
        {
            var token = _explorerCts.Token;
            (service, error) = await Task.Run(
                () =>
                {
                    var opened = ChdExplorerService.TryOpen(chdPath, consoleType, out var openError);
                    return (opened, openError);
                },
                token
            );
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            SetExplorerBusy(false, null);
        }

        if (service is null)
        {
            lock (_explorerLock)
            {
                _explorerService = null;
            }

            ExplorerEntriesDataGrid.ItemsSource = null;
            _explorerCurrentPath = "/";
            ExplorerInfoTextBox.Text = string.Empty;
            UpdateExplorerUiState();

            var message =
                $"Could not parse '{Path.GetFileName(chdPath)}' as {parserName} ({error}). Try another file system parser.";
            ExplorerStatusTextBlock.Text = message;
            LogError($"Explorer: {message}");
            ShowError(message);
            return;
        }

        var disposeNewService = false;
        lock (_explorerLock)
        {
            if (_explorerDisposed)
            {
                disposeNewService = true;
            }
            else
            {
                _explorerService = service;
            }
        }

        if (disposeNewService)
        {
            service.Dispose();
            return;
        }

        _explorerParserName = parserName;
        ExplorerInfoTextBox.Text = service.InfoReport;
        LogMessage(
            $"Explorer: parsed as {parserName} - volume '{service.VolumeName}' ({ChdExplorerItem.FormatSize(service.VolumeSize)})."
        );
        await LoadExplorerDirectoryAsync("/");
    }

    /// <summary>
    ///     Loads a directory of the open image into the grid.
    /// </summary>
    /// <param name="internalPath">Directory path inside the image.</param>
    private async Task LoadExplorerDirectoryAsync(string internalPath)
    {
        CancellationToken token;
        try
        {
            token = _explorerCts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        SetExplorerBusy(true, $"Loading {internalPath}...");
        try
        {
            var entries = await UseExplorerAsync(service => service.ListDirectory(internalPath), token);
            if (entries is null) return;

            var items = entries
                .Select(ChdExplorerItem.FromEntry)
                .OrderByDescending(static item => item.IsDirectory)
                .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ExplorerEntriesDataGrid.ItemsSource = items;
            _explorerCurrentPath = internalPath;
            UpdateExplorerUiState();
            ExplorerStatusTextBlock.Text =
                $"{items.Count} item(s) - parsed as {_explorerParserName} - volume '{_explorerService?.VolumeName}'";
        }
        catch (OperationCanceledException)
        {
            // Window is closing or the image was replaced; nothing to report.
        }
        catch (Exception ex)
        {
            LogError($"Explorer: failed to list '{internalPath}': {ex.Message}", ex);
            ExplorerStatusTextBlock.Text = $"Failed to list '{internalPath}': {ex.Message}";
        }
        finally
        {
            SetExplorerBusy(false, null);
        }
    }

    /// <summary>
    ///     Extracts a file entry to a temp folder and opens it with the OS default application.
    /// </summary>
    /// <param name="item">The file row to open.</param>
    private async Task ExtractAndOpenExplorerFileAsync(ChdExplorerItem item)
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            AppConfig.ApplicationName,
            ExplorerTempFolderName,
            Guid.NewGuid().ToString("N")
        );

        var extracted = false;
        Process? openedProcess = null;
        try
        {
            SetExplorerBusy(true, $"Extracting {item.Name}...");
            var extractedPath = await ExtractExplorerEntryAsync(item, tempDirectory, _explorerCts.Token);
            if (extractedPath is null) return;

            extracted = true;
            var pathToOpen = extractedPath;
            await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    openedProcess = Process.Start(
                        new ProcessStartInfo(pathToOpen) { UseShellExecute = true }
                    );
                }
            );

            LogMessage($"Explorer: extracted '{item.Name}' and opened it with the default application.");
            ExplorerStatusTextBlock.Text = $"Opened: {item.Name}";
        }
        catch (OperationCanceledException)
        {
            // Window is closing; nothing to report.
        }
        catch (Exception ex)
        {
            LogError($"Explorer: failed to extract/open '{item.Name}': {ex.Message}", ex);
            ShowError($"Failed to extract and open '{item.Name}': {ex.Message}");
        }
        finally
        {
            SetExplorerBusy(false, null);
            ScheduleExplorerTempCleanup(tempDirectory, openedProcess, extracted);
        }
    }

    /// <summary>
    ///     Extracts an entry to a destination directory while holding the explorer lease.
    /// </summary>
    /// <param name="item">The entry to extract.</param>
    /// <param name="destinationDirectory">Local directory that receives the entry.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The extracted path, or <see langword="null" /> when the explorer was retired.</returns>
    private async Task<string?> ExtractExplorerEntryAsync(
        ChdExplorerItem item,
        string destinationDirectory,
        CancellationToken token
    )
    {
        Directory.CreateDirectory(destinationDirectory);
        var progress = new Progress<double>(value => ExplorerProgressBar.Value = value * 100);
        return await UseExplorerAsync(
            service => service.ExtractEntry(item.Entry, destinationDirectory, progress, token),
            token
        );
    }

    /// <summary>
    ///     Runs an explorer operation on the thread pool while holding the explorer lease, so the
    ///     container cannot be disposed mid-read. Returns <see langword="null" /> when no image is open.
    /// </summary>
    /// <typeparam name="T">Result type of the operation.</typeparam>
    /// <param name="action">The operation to run.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The operation result, or the default value when no explorer is open.</returns>
    private async Task<T?> UseExplorerAsync<T>(Func<ChdExplorerService, T> action, CancellationToken token)
    {
        await _explorerUseLock.WaitAsync(token);
        try
        {
            ChdExplorerService? service;
            lock (_explorerLock)
            {
                service = _explorerService;
            }

            if (service is null) return default;

            return await Task.Run(() => action(service), token);
        }
        finally
        {
            _explorerUseLock.Release();
        }
    }

    /// <summary>
    ///     Disposes the explorer currently open, waiting for any in-flight operation to finish.
    /// </summary>
    private async Task RetireExplorerAsync()
    {
        ChdExplorerService? previous;
        lock (_explorerLock)
        {
            previous = _explorerService;
            _explorerService = null;
        }

        if (previous is null) return;

        await _explorerUseLock.WaitAsync();
        try
        {
            previous.Dispose();
        }
        catch (Exception ex)
        {
            LogError($"Explorer: failed to close the previous image: {ex.Message}", ex);
        }
        finally
        {
            _explorerUseLock.Release();
        }
    }

    /// <summary>
    ///     Disposes the explorer open when the window closes, waiting for any in-flight operation to
    ///     finish, then releases the explorer synchronization objects.
    /// </summary>
    private void DisposeExplorer()
    {
        ChdExplorerService? service;
        lock (_explorerLock)
        {
            _explorerDisposed = true;
            service = _explorerService;
            _explorerService = null;
        }

        _explorerCts.Cancel();

        _ = Task.Run(async () =>
            {
                try
                {
                    if (service is not null)
                    {
                        await _explorerUseLock.WaitAsync();
                        try
                        {
                            service.Dispose();
                        }
                        catch
                        {
                            /* process is shutting down */
                        }
                        finally
                        {
                            _explorerUseLock.Release();
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed.
                }
                finally
                {
                    _explorerCts.Dispose();

                    // _explorerUseLock is deliberately not disposed: RetireExplorerAsync may be
                    // waiting on it, and disposing it there would throw and leak the previous
                    // service. One semaphore at shutdown is harmless.
                }
            }
        );
    }

    /// <summary>
    ///     Updates the navigation bar and action button state for the current path and selection.
    /// </summary>
    private void UpdateExplorerUiState()
    {
        ExplorerPathTextBlock.Text = $"Current Path: {_explorerCurrentPath}";
        ExplorerUpButton.IsEnabled =
            !string.Equals(_explorerCurrentPath, "/", StringComparison.Ordinal) && _explorerService is not null;
        ExplorerExtractButton.IsEnabled =
            _explorerService is not null && ExplorerEntriesDataGrid.SelectedItem is ChdExplorerItem;
    }

    /// <summary>
    ///     Toggles the Explorer tab's busy state while an open/load/extract operation runs.
    /// </summary>
    /// <param name="busy">Whether an operation is running.</param>
    /// <param name="status">Optional status message to show.</param>
    private void SetExplorerBusy(bool busy, string? status)
    {
        ExplorerProgressBar.IsVisible = busy;
        if (busy)
        {
            ExplorerProgressBar.Value = 0;
        }

        BrowseExplorerChdButton.IsEnabled = !busy;
        ExplorerParserComboBox.IsEnabled = !busy;
        ExplorerEntriesDataGrid.IsEnabled = !busy;
        ExplorerUpButton.IsEnabled =
            !busy
            && !string.Equals(_explorerCurrentPath, "/", StringComparison.Ordinal)
            && _explorerService is not null;
        ExplorerExtractButton.IsEnabled =
            !busy && _explorerService is not null && ExplorerEntriesDataGrid.SelectedItem is ChdExplorerItem;

        if (!string.IsNullOrEmpty(status))
        {
            ExplorerStatusTextBlock.Text = status;
        }
    }

    /// <summary>
    ///     Returns the parent of an image-internal path, clamped at the root.
    /// </summary>
    /// <param name="internalPath">The current image-internal path.</param>
    /// <returns>The parent path, or <c>"/"</c> at the root.</returns>
    private static string GetExplorerParentPath(string internalPath)
    {
        var trimmed = internalPath.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return "/";

        var separator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return separator <= 0 ? "/" : trimmed[..separator];
    }

    /// <summary>
    ///     Deletes a temporary extraction folder once the application that opened it has exited, or
    ///     after a short delay when no process was started. Best effort.
    /// </summary>
    /// <param name="directory">The folder to delete.</param>
    /// <param name="openedProcess">The process the extracted file was opened with, or null.</param>
    /// <param name="extracted">Whether an extraction succeeded, so the fallback delay is longer.</param>
    private static void ScheduleExplorerTempCleanup(
        string directory,
        Process? openedProcess,
        bool extracted
    )
    {
        _ = Task.Run(async () =>
            {
                try
                {
                    var viewerRan = false;
                    if (openedProcess is not null)
                    {
                        // The external application may read the file lazily. Deleting it while it
                        // is still open fails on Windows and removes it under the app on Unix, so
                        // wait for the viewer to exit instead of using a fixed delay.
                        var startedAt = DateTime.UtcNow;
                        using (openedProcess)
                        {
                            await openedProcess.WaitForExitAsync();
                        }

                        // A shell launcher (xdg-open, open, a DDE handler) exits as soon as it has
                        // handed the file to the real viewer, which may still be starting. A quick
                        // exit therefore gets the fixed grace delay below rather than deleting the
                        // file out from under the viewer.
                        viewerRan = DateTime.UtcNow - startedAt >= TimeSpan.FromSeconds(3);
                    }

                    if (!viewerRan)
                    {
                        await Task.Delay(
                            extracted ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5)
                        );
                    }

                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }
                catch
                {
                    /* best effort */
                }
            }
        );
    }
}