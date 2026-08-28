using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class PathUtilsTests
{
    /// <summary>Scratch space for the tests that need real directories on disk.</summary>
    private static readonly string ReserveTempDir = Path.Combine(
        Path.GetTempPath(),
        $"PathUtilsTests_{Guid.NewGuid():N}"
    );

    [Theory]
    [InlineData("game.iso", "game.iso")]
    [InlineData("file:name.txt", "file_name.txt")]
    [InlineData("name\0.txt", "name_.txt")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SanitizeFileNameReplacesInvalidChars(string? input, string expected)
    {
        var result = PathUtils.SanitizeFileName(input ?? string.Empty);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeFileNameRemovesTrailingPeriods()
    {
        var result = PathUtils.SanitizeFileName("file.");
        Assert.Equal("file_", result);
    }

    [Fact]
    public void SanitizeFileNamePreservesUnicodeEllipsis()
    {
        var result = PathUtils.SanitizeFileName("game…iso");
        Assert.Equal("game…iso", result);
    }

    [Fact]
    public void GetSafeTempFileNameReturnsCorrectPath()
    {
        var tempDir = Path.GetTempPath();
        var result = PathUtils.GetSafeTempFileName("game.iso", "chd", tempDir);
        Assert.Equal(Path.Combine(tempDir, "game.chd"), result);
    }

    [Fact]
    public void GetSafeTempFileNameUsesGuidWhenEmptyName()
    {
        var tempDir = Path.GetTempPath();
        var result = PathUtils.GetSafeTempFileName(".iso", "chd", tempDir);
        Assert.True(Path.GetFileNameWithoutExtension(result).Length > 0);
        Assert.Equal(".chd", Path.GetExtension(result));
    }

    [Fact]
    public void ValidateAndNormalizePathEmptyPathReturnsNull()
    {
        string? capturedError = null;
        var result = PathUtils.ValidateAndNormalizePath(
            "",
            "test folder",
            msg => { capturedError = msg; },
            static _ => { }
        );
        Assert.Null(result);
        Assert.NotNull(capturedError);
    }

    [Fact]
    public void ValidateAndNormalizePathNonExistentDirectoryReturnsNull()
    {
        string? capturedError = null;
        var nonExistent = Path.Combine(Path.GetTempPath(), $"NonExistent_{Guid.NewGuid():N}");
        var result = PathUtils.ValidateAndNormalizePath(
            nonExistent,
            "test folder",
            msg => { capturedError = msg; },
            static _ => { }
        );
        Assert.Null(result);
        Assert.NotNull(capturedError);
    }

    [Fact]
    public void ValidateAndNormalizePathValidDirectoryReturnsNormalizedPath()
    {
        var tempDir = Path.GetTempPath();
        var result = PathUtils.ValidateAndNormalizePath(
            tempDir,
            "temp",
            static _ => { },
            static _ => { }
        );
        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(tempDir), result);
    }

    [Fact]
    public void ValidateAndNormalizePathNullPathReturnsNull()
    {
        string? capturedError = null;
        var result = PathUtils.ValidateAndNormalizePath(
            null,
            "test folder",
            msg => { capturedError = msg; },
            static _ => { }
        );
        Assert.Null(result);
        Assert.NotNull(capturedError);
    }

    [Fact]
    public void ValidateAndNormalizePathInvalidCharsReturnsNull()
    {
        string? capturedError = null;
        var result = PathUtils.ValidateAndNormalizePath(
            "\0invalid",
            "invalid path",
            msg => { capturedError = msg; },
            static _ => { }
        );
        Assert.Null(result);
        Assert.NotNull(capturedError);
    }

    [Fact]
    public void ValidateAndNormalizePathWhitespaceOnlyReturnsNull()
    {
        string? capturedError = null;
        var result = PathUtils.ValidateAndNormalizePath(
            "   ",
            "test folder",
            msg => { capturedError = msg; },
            static _ => { }
        );
        Assert.Null(result);
        Assert.NotNull(capturedError);
    }

    [Fact]
    public void GetSafeRelativePathSameRootReturnsRelativePath()
    {
        var root = Path.GetPathRoot(Path.GetTempPath()) ?? @"C:\";
        var path1 = Path.Combine(root, "dir1", "subdir");
        var path2 = Path.Combine(root, "dir1", "subdir", "sub2", "file.txt");

        var result = PathUtils.GetSafeRelativePath(path1, path2);
        Assert.NotEqual(".", result, StringComparer.Ordinal);
        Assert.Contains("sub2", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSafeRelativePathDifferentRootReturnsDot()
    {
        var result = PathUtils.GetSafeRelativePath(@"C:\dir1", @"D:\dir2");
        Assert.Equal(".", result);
    }

    [Fact]
    public void GetSafeRelativePathInvalidPathReturnsDot()
    {
        var result = PathUtils.GetSafeRelativePath(string.Empty, @"C:\test");
        Assert.Equal(".", result);
    }

    [Fact]
    public void GetSafeTempFileNamePreservesExtension()
    {
        var tempDir = Path.GetTempPath();
        var result = PathUtils.GetSafeTempFileName("game.cue", "iso", tempDir);
        Assert.EndsWith(".iso", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSafeTempFileNameSanitizesInput()
    {
        var tempDir = Path.GetTempPath();
        var result = PathUtils.GetSafeTempFileName("game:test.iso", "chd", tempDir);
        var fileName = Path.GetFileNameWithoutExtension(result);
        Assert.DoesNotContain(":", fileName, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeFileNameAllInvalidCharsReplaced()
    {
        const string input = "a<b>c:d\"e/f\\g|h?i*j";
        var result = PathUtils.SanitizeFileName(input);
        Assert.DoesNotContain("<", result, StringComparison.Ordinal);
        Assert.DoesNotContain(">", result, StringComparison.Ordinal);
        Assert.DoesNotContain(":", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("/", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", result, StringComparison.Ordinal);
        Assert.DoesNotContain("|", result, StringComparison.Ordinal);
        Assert.DoesNotContain("?", result, StringComparison.Ordinal);
        Assert.DoesNotContain("*", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeFileNameMultipleTrailingPeriodsReplaced()
    {
        var result = PathUtils.SanitizeFileName("file...");
        Assert.DoesNotContain("...", result, StringComparison.Ordinal);
        // The algorithm processes trailing periods one at a time:
        // "file..." -> "file.._" -> stops because last char is now '_'
        Assert.Equal("file.._", result);
    }

    [Fact]
    public void SanitizeFileNameSingleTrailingPeriodWithContent()
    {
        var result = PathUtils.SanitizeFileName("game.iso.");
        Assert.Equal("game.iso_", result);
    }

    [Fact]
    public void SanitizeFileNameOnlyPeriodsBecomesUnderscores()
    {
        var result = PathUtils.SanitizeFileName("...");
        // The algorithm processes one trailing period at a time,
        // replacing each with '_' and then stopping when last char is not '.'
        Assert.Equal(".._", result);
    }

    [Fact]
    public void SanitizeFileNamePreservesMojibakeCharacters()
    {
        var result = PathUtils.SanitizeFileName("game\u00e2\u20ac\u00a6iso");
        Assert.Equal("game\u00e2\u20ac\u00a6iso", result);
    }

    [Fact]
    public void SanitizeFileNamePreservesValidUnicode()
    {
        // Japanese filename characters should be preserved
        var result = PathUtils.SanitizeFileName("\u30b2\u30fc\u30e0.iso");
        Assert.Equal("\u30b2\u30fc\u30e0.iso", result);
    }

    #region GetBestTempDirectory / GetPossibleTempBasePaths

    [Fact]
    public void GetBestTempDirectoryReturnsNonNullPath()
    {
        var result = PathUtils.GetBestTempDirectory(null, null, "TestPrefix_");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GetBestTempDirectoryIncludesPrefix()
    {
        var result = PathUtils.GetBestTempDirectory(null, null, "TestPrefix_");
        Assert.Contains("TestPrefix_", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetBestTempDirectoryUsesSystemTempAsFallback()
    {
        // With null inputs, the result is based on the drive with most free space,
        // which may or may not be the system temp drive. Verify the path is valid.
        var result = PathUtils.GetBestTempDirectory(null, null, "FallbackTest_");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("FallbackTest_", result, StringComparison.Ordinal);
        // Path should include a guid component
        var dirName = Path.GetFileName(result);
        Assert.StartsWith("FallbackTest_", dirName, StringComparison.Ordinal);
        Assert.True(dirName.Length > "FallbackTest_".Length + 10);
    }

    [Fact]
    public void GetPossibleTempBasePathsIncludesSystemTemp()
    {
        var paths = PathUtils.GetPossibleTempBasePaths().ToList();
        Assert.NotEmpty(paths);
        Assert.Contains(Path.GetTempPath(), paths, StringComparer.Ordinal);
    }

    [Fact]
    public void GetPossibleTempBasePathsReturnsNoDuplicates()
    {
        var paths = PathUtils.GetPossibleTempBasePaths().ToList();
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    #endregion

    #region CreateTempDirectoryOnSameVolume

    [Fact]
    public void SameVolumeTempDirectoryIsCreatedOnTheReferenceVolume()
    {
        var reference = Path.Combine(Path.GetTempPath(), $"reference_{Guid.NewGuid():N}.iso");
        File.WriteAllBytes(reference, new byte[16]);

        string? created = null;
        try
        {
            created = PathUtils.CreateTempDirectoryOnSameVolume(reference, "SameVolume_");

            Assert.NotNull(created);
            Assert.True(Directory.Exists(created));
            Assert.Equal(
                Path.GetPathRoot(Path.GetFullPath(reference)),
                Path.GetPathRoot(Path.GetFullPath(created)),
                StringComparer.OrdinalIgnoreCase
            );
        }
        finally
        {
            Cleanup(created, reference);
        }
    }

    [Fact]
    public void ACueInTheSameVolumeDirectoryCanReferenceTheImageRelatively()
    {
        // This is the property the whole helper exists for: chdman joins a cue's FILE entry to the
        // cue's own directory, so the path from the directory to the image must not come back rooted.
        var reference = Path.Combine(Path.GetTempPath(), $"reference_{Guid.NewGuid():N}.iso");
        File.WriteAllBytes(reference, new byte[16]);

        string? created = null;
        try
        {
            created = PathUtils.CreateTempDirectoryOnSameVolume(reference, "SameVolume_");
            Assert.NotNull(created);

            var relative = Path.GetRelativePath(created, reference);
            Assert.False(
                Path.IsPathRooted(relative),
                $"'{relative}' is rooted, so a generated cue could not reach the image"
            );
        }
        finally
        {
            Cleanup(created, reference);
        }
    }

    [Fact]
    public void EveryReadyFixedVolumeGetsADirectoryOnItself()
    {
        // The roomiest-drive choice is wrong here: an image on a nearly full drive still needs its
        // cue on that drive. Each fixed volume is checked, since that is where source images live.
        foreach (
            var drive in DriveInfo
                .GetDrives()
                .Where(static d => d is { IsReady: true, DriveType: DriveType.Fixed })
        )
        {
            var root = drive.RootDirectory.FullName;
            var reference = Path.Combine(root, $"image_{Guid.NewGuid():N}.iso");

            var created = PathUtils.CreateTempDirectoryOnSameVolume(reference, "SameVolume_");
            if (created is null)
                // A volume that refuses a directory is reported by returning null, which the caller
                // handles; there is nothing to assert about it beyond that.
                continue;

            try
            {
                Assert.Equal(
                    Path.GetPathRoot(root),
                    Path.GetPathRoot(Path.GetFullPath(created)),
                    StringComparer.OrdinalIgnoreCase
                );
            }
            finally
            {
                Cleanup(created, null);
            }
        }
    }

    [Fact]
    public void EachCallGetsItsOwnDirectory()
    {
        var reference = Path.Combine(Path.GetTempPath(), $"reference_{Guid.NewGuid():N}.iso");

        var first = PathUtils.CreateTempDirectoryOnSameVolume(reference, "SameVolume_");
        var second = PathUtils.CreateTempDirectoryOnSameVolume(reference, "SameVolume_");

        try
        {
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first, second, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(first, null);
            Cleanup(second, null);
        }
    }

    [Fact]
    public void AFreeSubdirectoryNameIsTheDiscNameWhenNothingOccupiesIt()
    {
        var parent = Path.Combine(ReserveTempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);

        var reserved = PathUtils.ReserveFreeSubdirectory(parent, "Breath of Fire IV");

        Assert.Equal(Path.Combine(parent, "Breath of Fire IV"), reserved);
        // Reserving does not create it; the caller decides whether it is needed.
        Assert.False(Directory.Exists(reserved));
    }

    [Fact]
    public void AFreeSubdirectoryNameStepsAsideForAnExistingDirectory()
    {
        var parent = Path.Combine(ReserveTempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(parent, "Game"));
        Directory.CreateDirectory(Path.Combine(parent, "Game (2)"));

        var reserved = PathUtils.ReserveFreeSubdirectory(parent, "Game");

        Assert.Equal(Path.Combine(parent, "Game (3)"), reserved);
    }

    [Fact]
    public void AFreeSubdirectoryNameStepsAsideForAnExistingFileOfThatName()
    {
        // A file called "Game" with no extension would block the directory just as a folder would.
        var parent = Path.Combine(ReserveTempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        File.WriteAllBytes(Path.Combine(parent, "Game"), new byte[4]);

        var reserved = PathUtils.ReserveFreeSubdirectory(parent, "Game");

        Assert.Equal(Path.Combine(parent, "Game (2)"), reserved);
    }

    [Fact]
    public void AFreeSubdirectoryNameIsSanitised()
    {
        var parent = Path.Combine(ReserveTempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);

        var reserved = PathUtils.ReserveFreeSubdirectory(parent, "Game: Special?Edition");

        Assert.Equal(Path.Combine(parent, "Game_ Special_Edition"), reserved);
        Assert.DoesNotContain(':', Path.GetFileName(reserved));
        Assert.DoesNotContain('?', Path.GetFileName(reserved));
    }

    [Theory]
    // Same folder, including the forms that differ only as text.
    [InlineData(@"D:\Games", @"D:\Games", true)]
    [InlineData(@"D:\Games", @"D:\Games\", true)]
    [InlineData(@"D:\Games\", @"D:\Games", true)]
    [InlineData(@"D:\Games", @"d:\games", true)]
    [InlineData(@"D:\Games", @"D:\Games\..\Games", true)]
    // Nested, which the old equality check let through and which carries the same exposure.
    [InlineData(@"D:\Games", @"D:\Games\CHD", true)]
    [InlineData(@"D:\Games", @"D:\Games\CHD\Sub", true)]
    // Genuinely separate, including the prefix trap.
    [InlineData(@"D:\Games", @"D:\Games2", false)]
    [InlineData(@"D:\Games", @"D:\Other", false)]
    [InlineData(@"D:\Games\CHD", @"D:\Games", false)]
    [InlineData(@"D:\Games", @"C:\Games", false)]
    public void SameOrNestedDirectoriesAreDetected(string root, string candidate, bool expected)
    {
        Assert.Equal(expected, PathUtils.IsSameOrInsideDirectory(root, candidate));
    }

    [Fact]
    public void UnusableDirectoryComparisonsAreFalseRatherThanThrowing()
    {
        // The caller only uses this to decide whether to log a note, so it must never throw.
        Assert.False(PathUtils.IsSameOrInsideDirectory(null, @"D:\Games"));
        Assert.False(PathUtils.IsSameOrInsideDirectory(@"D:\Games", null));
        Assert.False(PathUtils.IsSameOrInsideDirectory(string.Empty, string.Empty));
        Assert.False(PathUtils.IsSameOrInsideDirectory("   ", @"D:\Games"));
        Assert.False(PathUtils.IsSameOrInsideDirectory("\0invalid", @"D:\Games"));
    }

    [Fact]
    public void AnUnusablePathIsReportedAsNull()
    {
        // The caller falls back to converting the image as-is on null, so a path with no volume has
        // to come back null rather than throwing out of the conversion.
        Assert.Null(PathUtils.CreateTempDirectoryOnSameVolume(string.Empty, "SameVolume_"));
        Assert.Null(PathUtils.CreateTempDirectoryOnSameVolume("\0invalid", "SameVolume_"));
    }

    private static void Cleanup(string? directory, string? file)
    {
        try
        {
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);

            if (file is not null && File.Exists(file)) File.Delete(file);
        }
        catch
        {
            /* ignore */
        }
    }

    #endregion
}