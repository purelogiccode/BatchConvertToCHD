using CHDStudio.Services;

namespace CHDStudio.Tests;

public class LegacyCleanupServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"LegacyCleanupServiceTests_{Guid.NewGuid():N}"
    );

    public LegacyCleanupServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    private string CreateFolder(string name)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "file.txt"), "content");
        return path;
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "content");
        return path;
    }

    [Fact]
    public void CleanupDeletesLegacyLogsFolder()
    {
        var path = CreateFolder("logs");

        LegacyCleanupService.Cleanup(_tempDir);

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void CleanupDeletesLegacyResourcesFolder()
    {
        var path = CreateFolder("Resources");

        LegacyCleanupService.Cleanup(_tempDir);

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void CleanupPreservesScreenshotFolder()
    {
        var path = CreateFolder("Screenshot");

        LegacyCleanupService.Cleanup(_tempDir);

        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(path, "file.txt")));
    }

    [Fact]
    public void CleanupPreservesUnrelatedFolders()
    {
        var path = CreateFolder("data");

        LegacyCleanupService.Cleanup(_tempDir);

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void CleanupDeletesLegacyMaxcsoExecutable()
    {
        var path = CreateFile("maxcso.exe");

        LegacyCleanupService.Cleanup(_tempDir);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CleanupDeletesLegacyPsxPackagerExecutable()
    {
        var path = CreateFile("psxpackager.exe");

        LegacyCleanupService.Cleanup(_tempDir);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CleanupPreservesUnrelatedFiles()
    {
        var path = CreateFile("CHDStudio.exe");

        LegacyCleanupService.Cleanup(_tempDir);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void CleanupDoesNotThrowWhenDirectoryDoesNotExist()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");

        var exception = Record.Exception(() => LegacyCleanupService.Cleanup(missing));

        Assert.Null(exception);
    }

    [Fact]
    public void CleanupDoesNotThrowOnEmptyDirectory()
    {
        var exception = Record.Exception(() => LegacyCleanupService.Cleanup(_tempDir));

        Assert.Null(exception);
    }

    [Fact]
    public void RunInBackgroundDoesNotThrow()
    {
        var exception = Record.Exception(LegacyCleanupService.RunInBackground);

        Assert.Null(exception);
    }
}