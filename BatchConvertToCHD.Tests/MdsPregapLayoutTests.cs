using MDSSharp;

namespace BatchConvertToCHD.Tests;

/// <summary>
///     Per-track pregap layout of MDS v2 images: the footer's <c>track_data_length</c> records
///     whether a track's pregap is stored, and libMirage validates it as the extra block's data
///     length plus the pregap when stored. The preparer must materialize only the pregaps the data
///     file is missing, because an MDS v2 image can keep one track's pregap and omit another's.
/// </summary>
public class MdsPregapLayoutTests : IDisposable
{
    private const int SectorSize = 2352;

    private readonly string _tempDir;

    public MdsPregapLayoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"MdsPregapLayoutTests_{Guid.NewGuid():N}");
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
            /* best effort */
        }

        GC.SuppressFinalize(this);
    }

    private string WriteMdf(string name, int sectors)
    {
        var path = Path.Combine(_tempDir, name);
        var data = new byte[sectors * SectorSize];
        for (var sector = 0; sector < sectors; sector++)
        {
            data[sector * SectorSize] = (byte)(sector + 1);
        }

        File.WriteAllBytes(path, data);
        return path;
    }

    private MdsDisc BuildDisc(
        string name,
        string mdfPath,
        params MdsTrack[] tracks
    )
    {
        var mdsPath = Path.Combine(_tempDir, name + ".mds");
        File.WriteAllBytes(mdsPath, []);
        return new MdsDisc(1, tracks, mdsPath, mdfPath)
        {
            MediumType = MdsMedium.Cd,
            DataFilePaths = [mdfPath]
        };
    }

    private static MdsTrack Track(
        int number,
        long startLba,
        long pregap,
        long length,
        long stored
    )
    {
        return new MdsTrack(number, 0xA9, SectorSize, startLba)
        {
            PregapSectors = pregap,
            LengthSectors = length,
            StoredDataSectors = stored
        };
    }

    private string WorkDir()
    {
        var path = Path.Combine(_tempDir, "work_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task MixedPregapStorageIsRebuiltPerTrack()
    {
        // Track 1: 10 data sectors, no pregap. Track 2: 10-sector pregap stored in the file plus
        // 10 data sectors (footer stores 20). Track 3: 10-sector pregap missing (footer stores 10).
        var mdfPath = WriteMdf("Mixed.mdf", 40);
        var disc = BuildDisc(
            "Mixed",
            mdfPath,
            Track(1, 0, 0, 10, 10),
            Track(2, 20, 10, 10, 20),
            Track(3, 40, 10, 10, 10)
        );
        var workDir = WorkDir();

        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            null,
            CancellationToken.None
        );

        Assert.True(result.Success, result.FailureReason);
        var cue = await File.ReadAllTextAsync(result.CuePath!);
        Assert.Contains("INDEX 01 00:00:00", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 00 00:00:10", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 00:00:20", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 00 00:00:30", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 00:00:40", cue, StringComparison.Ordinal);

        var padded = Path.Combine(workDir, "Mixed.pregap.bin");
        Assert.True(File.Exists(padded));
        var bytes = await File.ReadAllBytesAsync(padded);
        Assert.Equal(50 * SectorSize, bytes.Length);

        // The source's first 30 sectors are copied as-is (track 2's stored pregap included), then
        // ten zero-filled pregap sectors, then track 3's data shifted to LBA 40.
        Assert.Equal(1, bytes[0]);
        Assert.Equal(11, bytes[10 * SectorSize]);
        Assert.Equal(0, bytes[30 * SectorSize]);
        Assert.Equal(31, bytes[40 * SectorSize]);
    }

    [Fact]
    public async Task PregapsStoredForEveryTrackGetIndex00WithoutARebuild()
    {
        var mdfPath = WriteMdf("Stored.mdf", 40);
        var disc = BuildDisc(
            "Stored",
            mdfPath,
            Track(1, 0, 0, 10, 10),
            Track(2, 20, 10, 10, 20),
            Track(3, 40, 10, 10, 20)
        );
        var workDir = WorkDir();

        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            null,
            CancellationToken.None
        );

        Assert.True(result.Success, result.FailureReason);
        var cue = await File.ReadAllTextAsync(result.CuePath!);
        Assert.Contains("INDEX 00 00:00:10", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 00:00:20", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 00 00:00:30", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 00:00:40", cue, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(workDir, "Stored.pregap.bin")));
    }

    [Fact]
    public async Task FirstTrackPregapBeforeLbaZeroIsNotMaterialized()
    {
        // The first track's 150-sector pregap precedes LBA 0 and cannot be expressed; the data
        // file holds only the track's data and the cue starts at LBA 0, like libMirage's NULL
        // pregap for track 1.
        var mdfPath = WriteMdf("First.mdf", 177);
        var disc = BuildDisc("First", mdfPath, Track(1, 0, 150, 177, 177));
        var workDir = WorkDir();

        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            null,
            CancellationToken.None
        );

        Assert.True(result.Success, result.FailureReason);
        var cue = await File.ReadAllTextAsync(result.CuePath!);
        Assert.Contains("INDEX 01 00:00:00", cue, StringComparison.Ordinal);
        Assert.DoesNotContain("INDEX 00", cue, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(workDir, "First.pregap.bin")));
    }
}
