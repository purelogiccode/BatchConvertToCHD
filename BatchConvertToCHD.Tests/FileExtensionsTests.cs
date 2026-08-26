using System.Reflection;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class FileExtensionsTests
{
    #region Individual constants

    [Theory]
    [InlineData(".cue", nameof(FileExtensions.Cue))]
    [InlineData(".iso", nameof(FileExtensions.Iso))]
    [InlineData(".img", nameof(FileExtensions.Img))]
    [InlineData(".gdi", nameof(FileExtensions.Gdi))]
    [InlineData(".toc", nameof(FileExtensions.Toc))]
    [InlineData(".raw", nameof(FileExtensions.Raw))]
    [InlineData(".ccd", nameof(FileExtensions.Ccd))]
    [InlineData(".sub", nameof(FileExtensions.Sub))]
    [InlineData(".zip", nameof(FileExtensions.Zip))]
    [InlineData(".7z", nameof(FileExtensions.SevenZip))]
    [InlineData(".rar", nameof(FileExtensions.Rar))]
    [InlineData(".cso", nameof(FileExtensions.Cso))]
    [InlineData(".pbp", nameof(FileExtensions.Pbp))]
    [InlineData(".isz", nameof(FileExtensions.Isz))]
    [InlineData(".chd", nameof(FileExtensions.Chd))]
    public void ConstantHasCorrectValue(string expected, string constantName)
    {
        var actual =
            typeof(FileExtensions)
                .GetField(
                    constantName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                )
                ?.GetValue(null) as string;

        Assert.Equal(expected, actual);
    }

    #endregion

    #region ArchiveExtensions

    [Fact]
    public void ArchiveExtensionsArrayHasExpectedEntries()
    {
        var expected = new[] { ".zip", ".7z", ".rar" };
        Assert.Equal(expected, FileExtensions.ArchiveExtensions);
    }

    [Fact]
    public void ArchiveExtensionsSetMatchesArray()
    {
        Assert.Equal(
            FileExtensions.ArchiveExtensions.Length,
            FileExtensions.ArchiveExtensionsSet.Count
        );
        foreach (var ext in FileExtensions.ArchiveExtensions)
            Assert.Contains(ext, FileExtensions.ArchiveExtensionsSet);
    }

    [Theory]
    [InlineData(".zip", true)]
    [InlineData(".ZIP", true)]
    [InlineData(".7z", true)]
    [InlineData(".7Z", true)]
    [InlineData(".rar", true)]
    [InlineData(".RAR", true)]
    [InlineData(".iso", false)]
    [InlineData(".cue", false)]
    [InlineData(".chd", false)]
    [InlineData("", false)]
    public void ArchiveExtensionsSetContainsExpectedValues(string ext, bool expected)
    {
        Assert.Equal(expected, FileExtensions.ArchiveExtensionsSet.Contains(ext));
    }

    #endregion

    #region PrimaryTargetExtensions

    [Fact]
    public void PrimaryTargetExtensionsArrayHasExpectedEntries()
    {
        var expected = new[]
        {
            ".cue",
            ".iso",
            ".img",
            ".gdi",
            ".toc",
            ".raw",
            ".ccd",
            ".mds",
            ".isz",
        };
        Assert.Equal(expected, FileExtensions.PrimaryTargetExtensions);
    }

    [Fact]
    public void PrimaryTargetExtensionsSetMatchesArray()
    {
        Assert.Equal(
            FileExtensions.PrimaryTargetExtensions.Length,
            FileExtensions.PrimaryTargetExtensionsSet.Count
        );
        foreach (var ext in FileExtensions.PrimaryTargetExtensions)
            Assert.Contains(ext, FileExtensions.PrimaryTargetExtensionsSet);
    }

    [Theory]
    [InlineData(".cue", true)]
    [InlineData(".CUE", true)]
    [InlineData(".iso", true)]
    [InlineData(".img", true)]
    [InlineData(".gdi", true)]
    [InlineData(".toc", true)]
    [InlineData(".TOC", true)]
    [InlineData(".raw", true)]
    [InlineData(".RAW", true)]
    [InlineData(".ccd", true)]
    [InlineData(".CCD", true)]
    // An archived ISZ is decompressed by the conversion loop, so it counts as a primary target.
    [InlineData(".isz", true)]
    [InlineData(".ISZ", true)]
    [InlineData(".zip", false)]
    [InlineData(".chd", false)]
    [InlineData(".cso", false)]
    public void PrimaryTargetExtensionsSetContainsExpectedValues(string ext, bool expected)
    {
        Assert.Equal(expected, FileExtensions.PrimaryTargetExtensionsSet.Contains(ext));
    }

    #endregion

    #region AllSupportedInputExtensionsForConversion

    [Fact]
    public void AllSupportedInputExtensionsForConversionArrayHasExpectedEntries()
    {
        var expected = new[]
        {
            ".cue",
            ".iso",
            ".img",
            ".gdi",
            ".toc",
            ".raw",
            ".ccd",
            ".bin",
            ".mds",
            ".ecm",
            ".isz",
            ".001",
            ".i00",
            ".zip",
            ".7z",
            ".rar",
            ".cso",
            ".pbp",
        };
        Assert.Equal(expected, FileExtensions.AllSupportedInputExtensionsForConversion);
    }

    [Fact]
    public void AllSupportedInputExtensionsForConversionSetMatchesArray()
    {
        Assert.Equal(
            FileExtensions.AllSupportedInputExtensionsForConversion.Length,
            FileExtensions.AllSupportedInputExtensionsForConversionSet.Count
        );
        foreach (var ext in FileExtensions.AllSupportedInputExtensionsForConversion)
            Assert.Contains(ext, FileExtensions.AllSupportedInputExtensionsForConversionSet);
    }

    [Theory]
    [InlineData(".cue", true)]
    [InlineData(".iso", true)]
    [InlineData(".ISO", true)]
    [InlineData(".img", true)]
    [InlineData(".gdi", true)]
    [InlineData(".toc", true)]
    [InlineData(".raw", true)]
    [InlineData(".zip", true)]
    [InlineData(".7z", true)]
    [InlineData(".7Z", true)]
    [InlineData(".rar", true)]
    [InlineData(".cso", true)]
    [InlineData(".CSO", true)]
    [InlineData(".pbp", true)]
    [InlineData(".ccd", true)]
    [InlineData(".CCD", true)]
    // A bare .bin with no cue is a convertible disc: a cue is generated for it at conversion time,
    // and when a sibling descriptor covers it InputFileFilter drops it from the batch.
    [InlineData(".bin", true)]
    [InlineData(".BIN", true)]
    // .mds is the Alcohol descriptor and drives the conversion; the .mdf it points at is data only.
    [InlineData(".mds", true)]
    [InlineData(".MDS", true)]
    [InlineData(".mdf", false)]
    // ECM is decoded by an external tool when one is installed, so it is worth listing.
    [InlineData(".ecm", true)]
    // ISZ is decompressed in-process, and the extension is also used for plain images that were
    // simply renamed, so it has to be scanned for either case to be reached at all.
    [InlineData(".isz", true)]
    [InlineData(".ISZ", true)]
    // Only the first volume of a split set is an input; later parts are found from it.
    [InlineData(".001", true)]
    [InlineData(".i00", true)]
    [InlineData(".I00", true)]
    [InlineData(".002", false)]
    [InlineData(".i01", false)]
    [InlineData(".chd", false)]
    [InlineData(".sub", false)]
    [InlineData("", false)]
    public void AllSupportedInputExtensionsForConversionSetContainsExpectedValues(
        string ext,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            FileExtensions.AllSupportedInputExtensionsForConversionSet.Contains(ext)
        );
    }

    #endregion

    #region Cross-consistency

    [Fact]
    public void ArchiveExtensionsAreSubsetOfAllSupported()
    {
        foreach (var ext in FileExtensions.ArchiveExtensions)
            Assert.Contains(
                ext,
                FileExtensions.AllSupportedInputExtensionsForConversion,
                StringComparer.Ordinal
            );
    }

    [Fact]
    public void PrimaryTargetExtensionsAreSubsetOfAllSupported()
    {
        foreach (var ext in FileExtensions.PrimaryTargetExtensions)
            Assert.Contains(
                ext,
                FileExtensions.AllSupportedInputExtensionsForConversion,
                StringComparer.Ordinal
            );
    }

    [Fact]
    public void NoDuplicateExtensionsAcrossCategories()
    {
        var all = FileExtensions.AllSupportedInputExtensionsForConversion;
        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    #endregion
}
