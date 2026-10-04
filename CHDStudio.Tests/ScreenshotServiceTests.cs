using CHDStudio.Services;

namespace CHDStudio.Tests;

public class ScreenshotServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"ScreenshotServiceTests_{Guid.NewGuid():N}"
    );

    public ScreenshotServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    private string PreferredDirectory => Path.Combine(
        _tempRoot,
        "appdata",
        AppConfig.ApplicationName,
        ScreenshotService.FolderName
    );

    private string FallbackDirectory => Path.Combine(_tempRoot, "app", ScreenshotService.FolderName);

    [Fact]
    public void FolderNameIsScreenshots()
    {
        Assert.Equal("screenshots", ScreenshotService.FolderName);
    }

    [Fact]
    public void GetPreferredDirectoryUsesApplicationDataRootAndApplicationName()
    {
        var result = ScreenshotService.GetPreferredDirectory(_tempRoot);

        Assert.Equal(
            Path.Combine(_tempRoot, AppConfig.ApplicationName, "screenshots"),
            result
        );
    }

    [Fact]
    public void GetPreferredDirectoryUsesLocalApplicationData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConfig.ApplicationName,
            "screenshots"
        );

        Assert.Equal(expected, ScreenshotService.GetPreferredDirectory());
    }

    [Fact]
    public void GetFallbackDirectoryCombinesBaseDirectoryAndFolderName()
    {
        var result = ScreenshotService.GetFallbackDirectory(@"C:\Apps\CHDStudio");

        Assert.Equal(Path.Combine(@"C:\Apps\CHDStudio", "screenshots"), result);
    }

    [Fact]
    public void BuildFileNameProducesTimestampedPng()
    {
        var timestamp = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Local);

        var result = ScreenshotService.BuildFileName(timestamp);

        Assert.Equal("screenshot_2026-01-02_03-04-05-678.png", result);
    }

    [Fact]
    public void SaveScreenshotWritesToPreferredDirectoryWhenWritable()
    {
        var savedPath = ScreenshotService.SaveScreenshot(
            path => File.WriteAllText(path, "png"),
            PreferredDirectory,
            FallbackDirectory
        );

        Assert.NotNull(savedPath);
        Assert.True(File.Exists(savedPath));
        Assert.StartsWith(PreferredDirectory, savedPath, StringComparison.Ordinal);
        Assert.EndsWith(".png", savedPath, StringComparison.Ordinal);
        Assert.False(Directory.Exists(FallbackDirectory));
    }

    [Fact]
    public void SaveScreenshotCreatesMissingPreferredDirectory()
    {
        Assert.False(Directory.Exists(PreferredDirectory));

        var savedPath = ScreenshotService.SaveScreenshot(
            path => File.WriteAllText(path, "png"),
            PreferredDirectory,
            FallbackDirectory
        );

        Assert.NotNull(savedPath);
        Assert.True(Directory.Exists(PreferredDirectory));
    }

    [Fact]
    public void SaveScreenshotFileNameContainsTimestamp()
    {
        var savedPath = ScreenshotService.SaveScreenshot(
            path => File.WriteAllText(path, "png"),
            PreferredDirectory,
            FallbackDirectory
        );

        Assert.NotNull(savedPath);
        var fileName = Path.GetFileName(savedPath);
        Assert.StartsWith("screenshot_", fileName, StringComparison.Ordinal);
        Assert.Matches(@"^screenshot_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-\d{3}\.png$", fileName);
    }

    [Fact]
    public void SaveScreenshotFallsBackWhenPreferredSaveThrows()
    {
        var savedPath = ScreenshotService.SaveScreenshot(
            path =>
            {
                if (path.StartsWith(PreferredDirectory, StringComparison.Ordinal))
                    throw new IOException("Access denied");

                File.WriteAllText(path, "png");
            },
            PreferredDirectory,
            FallbackDirectory
        );

        Assert.NotNull(savedPath);
        Assert.True(File.Exists(savedPath));
        Assert.StartsWith(FallbackDirectory, savedPath, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveScreenshotFallsBackWhenPreferredDirectoryCreationFails()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferredDirectory)!);
        File.WriteAllText(PreferredDirectory, "this file blocks the directory");

        var savedPath = ScreenshotService.SaveScreenshot(
            path => File.WriteAllText(path, "png"),
            PreferredDirectory,
            FallbackDirectory
        );

        Assert.NotNull(savedPath);
        Assert.True(File.Exists(savedPath));
        Assert.StartsWith(FallbackDirectory, savedPath, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveScreenshotCreatesFallbackDirectoryWhenMissing()
    {
        Assert.False(Directory.Exists(FallbackDirectory));

        var savedPath = ScreenshotService.SaveScreenshot(
            path =>
            {
                if (path.StartsWith(PreferredDirectory, StringComparison.Ordinal))
                    throw new IOException("Access denied");

                File.WriteAllText(path, "png");
            },
            PreferredDirectory,
            FallbackDirectory
        );

        Assert.NotNull(savedPath);
        Assert.True(Directory.Exists(FallbackDirectory));
    }

    [Fact]
    public void SaveScreenshotReturnsNullWhenBothSavesFail()
    {
        var savedPath = ScreenshotService.SaveScreenshot(
            _ => throw new IOException("Access denied"),
            PreferredDirectory,
            FallbackDirectory
        );

        Assert.Null(savedPath);
    }

    [Fact]
    public void SaveScreenshotInvokesSaveDelegateExactlyOnceWhenPreferredWorks()
    {
        var calls = 0;

        ScreenshotService.SaveScreenshot(
            _ => calls++,
            PreferredDirectory,
            FallbackDirectory
        );

        Assert.Equal(1, calls);
    }

    [Fact]
    public void SaveScreenshotInvokesSaveDelegateTwiceWhenPreferredFails()
    {
        var calls = 0;

        ScreenshotService.SaveScreenshot(
            path =>
            {
                calls++;
                if (path.StartsWith(PreferredDirectory, StringComparison.Ordinal))
                    throw new IOException("Access denied");
            },
            PreferredDirectory,
            FallbackDirectory
        );

        Assert.Equal(2, calls);
    }
}