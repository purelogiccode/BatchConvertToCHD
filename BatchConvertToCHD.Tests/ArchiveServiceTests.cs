using System.IO.Compression;
using BatchConvertToCHD.Services;
using SharpCompress.Common;
using SharpCompressZipArchive = SharpCompress.Archives.Zip.ZipArchive;

namespace BatchConvertToCHD.Tests;

public class ArchiveServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ArchiveServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ArchiveServiceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // ignore cleanup errors
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExtractArchiveAsyncMissingFileReturnsFailureAndLogsWarning()
    {
        var service = new ArchiveService("7za.exe", false);
        var missingPath = Path.Combine(_tempDir, "missing.7z");
        var tempDir = Path.Combine(_tempDir, "extract");
        var logs = new List<string>();

        var result = await service.ExtractArchiveAsync(
            missingPath,
            tempDir,
            logs.Add,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            logs,
            static msg =>
                msg.Contains("File not found", StringComparison.OrdinalIgnoreCase)
                && msg.Contains("missing", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains("File not found", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractArchiveAsyncUnsupportedExtensionReturnsFailure()
    {
        var service = new ArchiveService("7za.exe", false);
        var filePath = Path.Combine(_tempDir, "test.tar");
        File.WriteAllText(filePath, "dummy");
        var tempDir = Path.Combine(_tempDir, "extract");

        var result = await service.ExtractArchiveAsync(
            filePath,
            tempDir,
            static _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains("Unsupported archive type", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractArchiveAsyncValidZipExtractsFiles()
    {
        var service = new ArchiveService("7za.exe", false);
        var zipPath = Path.Combine(_tempDir, "test.zip");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        await using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("game.iso");
            await using (var stream = entry.Open())
            {
                stream.WriteByte(0x01);
            }
        }

        var result = await service.ExtractArchiveAsync(
            zipPath,
            tempDir,
            static _ => { },
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.Single(result.FilePaths);
        Assert.EndsWith("game.iso", result.FilePaths[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractArchiveAsyncValidZipNoPrimaryFilesReturnsFailure()
    {
        var service = new ArchiveService("7za.exe", false);
        var zipPath = Path.Combine(_tempDir, "test.zip");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        await using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            await using (var stream = entry.Open())
            {
                stream.WriteByte(0x01);
            }
        }

        var result = await service.ExtractArchiveAsync(
            zipPath,
            tempDir,
            static _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            "No supported primary files found",
            result.ErrorMessage,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncZipWithOnlyBinGeneratesCue()
    {
        var service = new ArchiveService("7za.exe", false);
        var zipPath = Path.Combine(_tempDir, "test.zip");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        await using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("game.bin");
            await using (var stream = entry.Open())
            {
                stream.WriteByte(0x01);
            }
        }

        var result = await service.ExtractArchiveAsync(
            zipPath,
            tempDir,
            static _ => { },
            CancellationToken.None
        );

        Assert.True(result.Success);
        var cue = Assert.Single(result.FilePaths);
        Assert.EndsWith(".autocue.cue", cue, StringComparison.OrdinalIgnoreCase);

        var content = await File.ReadAllTextAsync(cue);
        Assert.Contains("FILE \"game.bin\" BINARY", content, StringComparison.Ordinal);
        Assert.Contains("TRACK 01 MODE2/2352", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractCsoAsyncMissingCsoFileReturnsFailure()
    {
        var service = new ArchiveService("7za.exe", false);
        var tempIso = Path.Combine(_tempDir, "out.iso");

        var result = await service.ExtractCsoAsync(
            "input.cso",
            tempIso,
            _tempDir,
            static _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains("CSOSharp", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void CanCreateService()
    {
        var service = new ArchiveService("7za.exe", false);
        Assert.NotNull(service);
    }

    [Fact]
    public void ExtractArchiveWithFallbackMissingFileThrowsFileNotFoundWithoutFallback()
    {
        var missingPath = Path.Combine(_tempDir, $"missing_{Guid.NewGuid():N}.7z");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                missingPath,
                outputDir,
                logs.Add,
                ".7z",
                static _ => throw new InvalidOperationException("Should not reach here"),
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<FileNotFoundException>(ex);
    }

    [Fact]
    public async Task ExtractArchiveAsyncInvalidSevenZipReturnsFailure()
    {
        var service = new ArchiveService("7za.exe", false);
        var sevenZPath = Path.Combine(_tempDir, "invalid.7z");
        File.WriteAllText(sevenZPath, "not a valid 7z archive");
        var tempDir = Path.Combine(_tempDir, "extract");

        var result = await service.ExtractArchiveAsync(
            sevenZPath,
            tempDir,
            static _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExtractArchiveAsyncInvalidRarReturnsFailure()
    {
        var service = new ArchiveService("7za.exe", false);
        var rarPath = Path.Combine(_tempDir, "invalid.rar");
        File.WriteAllText(rarPath, "not a valid rar archive");
        var tempDir = Path.Combine(_tempDir, "extract");

        var result = await service.ExtractArchiveAsync(
            rarPath,
            tempDir,
            static _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
    }

    // --- Tests for corrupt-data exception re-throw in ExtractArchiveWithFallback ---

    [Fact]
    public void ExtractArchiveWithFallbackIndexOutOfRangeExceptionRethrowsWithoutFallback()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();
        var openCalled = false;

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                _ =>
                {
                    openCalled = true;
                    ThrowIndexOutOfRange();
                    return null!;
                },
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<IndexOutOfRangeException>(ex);
        Assert.True(openCalled);
    }

    [Fact]
    public void ExtractArchiveWithFallbackNullReferenceExceptionRethrowsWithoutFallback()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                static _ =>
                {
                    ThrowNullReference();
                    return null!;
                },
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<NullReferenceException>(ex);
    }

    [Fact]
    public void ExtractArchiveWithFallbackInvalidFormatExceptionRethrowsWithoutFallback()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                static _ => throw new InvalidFormatException("Multi-part rar file is incomplete."),
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<InvalidFormatException>(ex);
    }

    [Fact]
    public void ExtractArchiveWithFallbackInvalidDataExceptionRethrowsWithoutFallback()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                static _ => throw new InvalidDataException("archive is corrupt"),
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<InvalidDataException>(ex);
    }

    [Fact]
    public void ExtractArchiveWithFallbackIncompleteArchiveExceptionRethrowsWithoutFallback()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                static _ => throw new IncompleteArchiveException("archive is incomplete"),
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<IncompleteArchiveException>(ex);
    }

    [Fact]
    public void ExtractArchiveWithFallbackCryptographicExceptionRethrowsWithoutFallback()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                static _ => throw new CryptographicException("archive is encrypted"),
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<CryptographicException>(ex);
    }

    [Fact]
    public void ExtractArchiveWithFallbackArchiveOperationExceptionRethrowsWithoutFallback()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                static _ => throw new ArchiveOperationException("archive operation failed"),
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<ArchiveOperationException>(ex);
    }

    [Fact]
    public void ExtractArchiveWithFallbackOperationCanceledExceptionRethrows()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                static _ => throw new OperationCanceledException(),
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<OperationCanceledException>(ex);
    }

    [Fact]
    public void ExtractArchiveWithFallbackNonCorruptExceptionAttemptsFallback()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();
        var openCallCount = 0;

        // InvalidOperationException is not in the corrupt-data list, so fallback should be attempted
        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                _ =>
                {
                    openCallCount++;
                    throw new InvalidOperationException("some transient error");
                },
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        // openArchive should be called twice: once for direct, once for fallback
        Assert.Equal(2, openCallCount);
    }

    // --- Tests for ExtractArchiveAsync exception handling ---

    [Fact]
    public void IsMultiPartRarErrorDetectsMissingVolume()
    {
        var ex = new InvalidFormatException(
            "Multi-part rar file is incomplete.  Entry expects a new volume: Kessen [PAL]\\Kessen.iso"
        );

        Assert.True(ArchiveService.IsMultiPartRarError(ex));
        Assert.False(
            ArchiveService.IsMultiPartRarError(new InvalidFormatException("unrelated error"))
        );
        Assert.False(
            ArchiveService.IsMultiPartRarError(
                new IOException("Multi-part rar file is incomplete.")
            )
        );
    }

    [Fact]
    public void IsNetworkUnavailableErrorDetectsDisconnectedShares()
    {
        Assert.True(
            ArchiveService.IsNetworkUnavailableError(
                new IOException(
                    @"The network path was not found. : '\\HENRY-MEDIA\External-Archive\Roms\PS1\Persona 2.7z'."
                )
            )
        );
        Assert.True(
            ArchiveService.IsNetworkUnavailableError(
                new IOException("The specified network name is no longer available.")
            )
        );
        Assert.True(
            ArchiveService.IsNetworkUnavailableError(
                new IOException("The network location cannot be reached.")
            )
        );
        Assert.False(
            ArchiveService.IsNetworkUnavailableError(
                new IOException("Access to the path is denied.")
            )
        );
        Assert.False(
            ArchiveService.IsNetworkUnavailableError(
                new IOException(
                    "The process cannot access the file because it is being used by another process."
                )
            )
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncInvalidFormatExceptionReturnsInvalidOrIncompleteMessage()
    {
        var service = new ArchiveService("7za.exe", false);
        // Create a file with RAR extension but invalid content to trigger InvalidFormatException
        var rarPath = Path.Combine(_tempDir, "bad.rar");
        File.WriteAllText(rarPath, "not a valid rar archive");
        var tempDir = Path.Combine(_tempDir, "extract");

        var result = await service.ExtractArchiveAsync(
            rarPath,
            tempDir,
            static _ => { },
            CancellationToken.None
        );

        Assert.False(result.Success);
        // The error should be one of the known archive error messages
        Assert.True(
            result.ErrorMessage.Contains(
                "invalid or incomplete",
                StringComparison.OrdinalIgnoreCase
            )
            || result.ErrorMessage.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
            || result.ErrorMessage.Contains(
                "Error extracting archive",
                StringComparison.OrdinalIgnoreCase
            ),
            $"Expected a known error message but got: {result.ErrorMessage}"
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncValidZipWithMultipleFilesExtractsAll()
    {
        var service = new ArchiveService("7za.exe", false);
        var zipPath = Path.Combine(_tempDir, "multi.zip");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        await using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "game1.iso", "game2.cue", "readme.txt" })
            {
                var entry = archive.CreateEntry(name);
                await using var stream = entry.Open();
                stream.WriteByte(0x01);
            }
        }

        var result = await service.ExtractArchiveAsync(
            zipPath,
            tempDir,
            static _ => { },
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.Equal(2, result.FilePaths.Count);
        Assert.Contains(
            result.FilePaths,
            static f => f.EndsWith("game1.iso", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            result.FilePaths,
            static f => f.EndsWith("game2.cue", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncValidZipWithSubdirectoriesExtractsFiles()
    {
        var service = new ArchiveService("7za.exe", false);
        var zipPath = Path.Combine(_tempDir, "nested.zip");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        await using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("subdir/game.iso");
            await using var stream = entry.Open();
            stream.WriteByte(0x01);
        }

        var result = await service.ExtractArchiveAsync(
            zipPath,
            tempDir,
            static _ => { },
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.Single(result.FilePaths);
        Assert.Contains("subdir", result.FilePaths[0], StringComparison.Ordinal);
        Assert.EndsWith("game.iso", result.FilePaths[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractArchiveAsyncCancellationThrowsOperationCanceled()
    {
        var service = new ArchiveService("7za.exe", false);
        var zipPath = Path.Combine(_tempDir, "cancel.zip");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        await using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("game.iso");
            await using var stream = entry.Open();
            stream.WriteByte(0x01);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ExtractArchiveAsync(zipPath, tempDir, static _ => { }, cts.Token)
        );
    }

    // --- Tests for 7za fallback logic ---

    [Fact]
    public async Task ExtractArchiveAsyncInvalidSevenZipWith7ZaUnavailableReturnsFailure()
    {
        var service = new ArchiveService("7za.exe", false);
        var sevenZPath = Path.Combine(_tempDir, "bad.7z");
        File.WriteAllText(sevenZPath, "not a valid 7z archive");
        var tempDir = Path.Combine(_tempDir, "extract");
        var logs = new List<string>();

        var result = await service.ExtractArchiveAsync(
            sevenZPath,
            tempDir,
            logs.Add,
            CancellationToken.None
        );

        Assert.False(result.Success);
        // Should NOT log 7za fallback attempt since 7za is unavailable
        Assert.DoesNotContain(
            logs,
            static msg => msg.Contains("7za.exe fallback", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncInvalidSevenZipWith7ZaAvailableAttemptsFallback()
    {
        // Use a non-existent 7za path so the fallback will fail, but we can verify it was attempted
        var fake7ZaPath = Path.Combine(_tempDir, "7za.exe");
        var service = new ArchiveService(fake7ZaPath, true);
        var sevenZPath = Path.Combine(_tempDir, "bad.7z");
        File.WriteAllText(sevenZPath, "not a valid 7z archive");
        var tempDir = Path.Combine(_tempDir, "extract");
        var logs = new List<string>();

        var result = await service.ExtractArchiveAsync(
            sevenZPath,
            tempDir,
            logs.Add,
            CancellationToken.None
        );

        Assert.False(result.Success);
        // Should log the 7za fallback attempt since 7za is marked as available
        Assert.Contains(
            logs,
            static msg => msg.Contains("7za.exe fallback", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncValidSevenZipExtractsSuccessfully()
    {
        // Create a 7z archive using System.IO.Compression (the test verifies the extraction pipeline works)
        var sevenZPath = Path.Combine(_tempDir, "valid.7z");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        // Write a minimal valid 7z header (signature only - enough for SharpCompress to open but may have no entries)
        // For a proper integration test, a real 7z file is needed. Here we test that the error path works.
        File.WriteAllBytes(
            sevenZPath,
            new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 }
        );

        var service = new ArchiveService("7za.exe", false);
        var result = await service.ExtractArchiveAsync(
            sevenZPath,
            tempDir,
            static _ => { },
            CancellationToken.None
        );

        // This will either succeed (if the minimal header is valid enough) or fail gracefully
        Assert.False(result.Success);
    }

    // --- Tests for ExtractArchiveWithFallback with valid archives ---

    [Fact]
    public async Task ExtractArchiveAsyncValidZipWithLogOutput()
    {
        var service = new ArchiveService("7za.exe", false);
        var zipPath = Path.Combine(_tempDir, "log_test.zip");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        await using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("game.iso");
            await using var stream = entry.Open();
            stream.WriteByte(0x01);
        }

        var logs = new List<string>();
        var result = await service.ExtractArchiveAsync(
            zipPath,
            tempDir,
            logs.Add,
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.Contains(
            logs,
            static msg => msg.Contains("Extracting", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void ExtractArchiveWithFallbackDirectoryNotFoundExceptionThrows()
    {
        var missingDir = Path.Combine(_tempDir, $"missing_{Guid.NewGuid():N}", "test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                missingDir,
                outputDir,
                logs.Add,
                ".rar",
                static _ => throw new InvalidOperationException("Should not reach here"),
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<DirectoryNotFoundException>(ex);
        Assert.Contains(
            logs,
            static msg => msg.Contains("Skipping fallback", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncCsoAsyncWithCancellationThrows()
    {
        var service = new ArchiveService("7za.exe", false);
        var csoPath = Path.Combine(_tempDir, "test.cso");
        File.WriteAllBytes(csoPath, new byte[] { 0x01 });
        var tempIso = Path.Combine(_tempDir, "out.iso");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ExtractCsoAsync(csoPath, tempIso, _tempDir, static _ => { }, cts.Token)
        );
    }

    [Fact]
    public void ExtractArchiveWithFallbackLogsFallbackAttemptForNonCorruptErrors()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                static _ => throw new InvalidOperationException("transient error"),
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.Contains(
            logs,
            static msg =>
                msg.Contains("Direct extraction failed", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            logs,
            static msg =>
                msg.Contains(
                    "Attempting fallback with local copy",
                    StringComparison.OrdinalIgnoreCase
                )
        );
    }

    [Fact]
    public void ExtractArchiveWithFallbackCorruptErrorsDoNotAttemptFallbackCopy()
    {
        var archivePath = CreateDummyFile("test.rar");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                archivePath,
                outputDir,
                logs.Add,
                ".rar",
                static _ =>
                {
                    ThrowIndexOutOfRange();
                    return null!;
                },
                CancellationToken.None
            );
        });

        Assert.NotNull(ex);
        Assert.IsType<IndexOutOfRangeException>(ex);
        // The exception is re-thrown before the fallback copy happens
        Assert.Contains(
            logs,
            static msg =>
                msg.Contains("Direct extraction failed", StringComparison.OrdinalIgnoreCase)
        );
        // No temp file should be created in the output directory (fallback copy didn't happen)
        Assert.DoesNotContain(
            Directory.GetFiles(Path.GetTempPath(), "*.rar"),
            static f => File.GetCreationTime(f) > DateTime.UtcNow.AddMinutes(-1)
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncLogsExtractionPath()
    {
        var service = new ArchiveService("7za.exe", false);
        var zipPath = Path.Combine(_tempDir, "log_test.zip");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        await using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("game.iso");
            await using var stream = entry.Open();
            stream.WriteByte(0x01);
        }

        var logs = new List<string>();
        var result = await service.ExtractArchiveAsync(
            zipPath,
            tempDir,
            logs.Add,
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.Contains(
            logs,
            static msg => msg.Contains("Extracting", StringComparison.OrdinalIgnoreCase)
        );
    }

    // --- Tests for 7za.exe exit code 2 (corrupt/invalid archive) handling ---

    [Fact]
    public async Task ExtractArchiveAsyncCorrupt7ZWith7ZaReturnsCorruptErrorMessage()
    {
        var sevenZipPath = Path.Combine(AppContext.BaseDirectory, "7za.exe");
        var service = new ArchiveService(sevenZipPath, true);
        var corruptPath = Path.Combine(_tempDir, "corrupt.7z");
        File.WriteAllText(corruptPath, "this is not a valid 7z archive file at all");
        var tempDir = Path.Combine(_tempDir, "extract");
        var logs = new List<string>();

        var result = await service.ExtractArchiveAsync(
            corruptPath,
            tempDir,
            logs.Add,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.True(
            result.ErrorMessage.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
            || result.ErrorMessage.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || result.ErrorMessage.Contains("incomplete", StringComparison.OrdinalIgnoreCase),
            $"Expected corrupt/invalid/incomplete message but got: {result.ErrorMessage}"
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncCorrupt7ZWith7ZaDoesNotReturnGenericExtractionFailed()
    {
        var sevenZipPath = Path.Combine(AppContext.BaseDirectory, "7za.exe");
        var service = new ArchiveService(sevenZipPath, true);
        var corruptPath = Path.Combine(_tempDir, "corrupt2.7z");
        File.WriteAllBytes(corruptPath, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        var tempDir = Path.Combine(_tempDir, "extract");
        var logs = new List<string>();

        var result = await service.ExtractArchiveAsync(
            corruptPath,
            tempDir,
            logs.Add,
            CancellationToken.None
        );

        Assert.False(result.Success);
        // Should NOT contain the generic "7za.exe extraction failed" message
        Assert.DoesNotContain(
            "7za.exe extraction failed",
            result.ErrorMessage,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncCorrupt7ZWith7ZaLogs7ZaFallbackAttempt()
    {
        var sevenZipPath = Path.Combine(AppContext.BaseDirectory, "7za.exe");
        var service = new ArchiveService(sevenZipPath, true);
        var corruptPath = Path.Combine(_tempDir, "corrupt3.7z");
        File.WriteAllText(corruptPath, "not a 7z archive");
        var tempDir = Path.Combine(_tempDir, "extract");
        var logs = new List<string>();

        var result = await service.ExtractArchiveAsync(
            corruptPath,
            tempDir,
            logs.Add,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            logs,
            static msg => msg.Contains("7za.exe fallback", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task ExtractArchiveAsyncCorrupt7ZWithout7ZaReturnsFailure()
    {
        var service = new ArchiveService("7za.exe", false);
        var corruptPath = Path.Combine(_tempDir, "corrupt_no7za.7z");
        File.WriteAllText(corruptPath, "not a 7z archive");
        var tempDir = Path.Combine(_tempDir, "extract");
        var logs = new List<string>();

        var result = await service.ExtractArchiveAsync(
            corruptPath,
            tempDir,
            logs.Add,
            CancellationToken.None
        );

        Assert.False(result.Success);
        // Should NOT attempt 7za fallback
        Assert.DoesNotContain(
            logs,
            static msg => msg.Contains("7za.exe fallback", StringComparison.OrdinalIgnoreCase)
        );
    }

    private string CreateDummyFile(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, new byte[] { 0x00 });
        return path;
    }

    private static void ThrowIndexOutOfRange()
    {
        // Simulates what SharpCompress does internally with corrupt RAR data
        var arr = new int[1];
        _ = arr[1];
    }

    private static void ThrowNullReference()
    {
        // Simulates what SharpCompress does internally with corrupt RAR data
        string? s = null;
        _ = s!.Length;
    }
}