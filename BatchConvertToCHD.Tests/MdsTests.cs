using System.Buffers.Binary;
using Alcohol120Sharp;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

/// <summary>
///     Covers Alcohol 120% .mds parsing and the preparation of a chdman-readable input. The synthetic
///     descriptors here reproduce the layouts and sector sizes seen in real rips.
/// </summary>
public class MdsTests : IDisposable
{
    // Offsets in the Alcohol descriptor, mirroring MdsParser.
    private const int SessionCountOffset = 0x14;
    private const int SessionBlockOffsetOffset = 0x50;
    private const int SessionBlockStart = 0x60;
    private const int TrackBlockStart = 0x100;
    private const int TrackBlockSize = 80;
    private readonly List<string> _log = [];
    private readonly string _tempDir;

    public MdsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"MdsTests_{Guid.NewGuid():N}");
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

    /// <summary>Builds a descriptor with one session and the supplied tracks.</summary>
    private string WriteMds(
        string name,
        params (byte Mode, byte Point, ushort SectorSize, uint StartLba)[] tracks
    )
    {
        var bytes = new byte[TrackBlockStart + TrackBlockSize * Math.Max(tracks.Length, 1)];
        "MEDIA DESCRIPTOR"u8.ToArray().CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(SessionCountOffset), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(SessionBlockOffsetOffset),
            SessionBlockStart
        );

        bytes[SessionBlockStart + 0x0A] = (byte)tracks.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(SessionBlockStart + 0x14),
            TrackBlockStart
        );

        for (var i = 0; i < tracks.Length; i++)
        {
            var offset = TrackBlockStart + i * TrackBlockSize;
            bytes[offset + 0x00] = tracks[i].Mode;
            bytes[offset + 0x04] = tracks[i].Point;
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(offset + 0x10),
                tracks[i].SectorSize
            );
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(offset + 0x24),
                tracks[i].StartLba
            );
        }

        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, bytes);

        return path;
    }

    private string WriteMdf(string name, int sectorSize, int sectors)
    {
        var path = Path.Combine(_tempDir, name);
        var data = new byte[sectorSize * sectors];

        // Fill each sector so the strip can be checked byte for byte: data bytes carry the sector
        // index, the subchannel tail carries 0xFF and must not survive.
        for (var sector = 0; sector < sectors; sector++)
        {
            var start = sector * sectorSize;
            for (var i = 0; i < sectorSize; i++)
                data[start + i] = i < MdsDisc.RawSectorSize ? (byte)(sector + 1) : (byte)0xFF;
        }

        File.WriteAllBytes(path, data);

        return path;
    }

    [Fact]
    public void SignatureIsRequired()
    {
        var notMds = Path.Combine(_tempDir, "random.mds");
        File.WriteAllText(notMds, "this is not a descriptor");

        Assert.False(MdsParser.IsMdsFile(notMds));
        Assert.Throws<InvalidDataException>(() => MdsParser.Parse(notMds));
    }

    [Fact]
    public void SingleMode2TrackIsParsed()
    {
        // Final Fantasy Tactics: one MODE2 track at LBA 0 with plain 2352-byte sectors.
        var mds = WriteMds("FINALFANTASYTACTICS.mds", (0xEC, 1, 2352, 0));
        WriteMdf("FINALFANTASYTACTICS.mdf", 2352, 2);

        Assert.True(MdsParser.IsMdsFile(mds));
        var disc = MdsParser.Parse(mds);

        var track = Assert.Single(disc.Tracks);
        Assert.Equal(1, track.Number);
        Assert.Equal(2352, track.SectorSize);
        Assert.Equal(0, track.StartLba);
        Assert.Equal(BinCueGenerator.Mode2, track.CueTrackType);
        Assert.False(track.IsAudio);
        Assert.True(disc.IsPlainRawCd);
        Assert.False(disc.NeedsSubchannelStrip);
        Assert.NotNull(disc.MdfPath);
    }

    [Fact]
    public void SubchannelSectorSizeIsRecognised()
    {
        // Marvel VS Capcom EX and friends: 2448-byte sectors that chdman refuses.
        var mds = WriteMds("MARVEL.mds", (0xEC, 1, 2448, 0));
        WriteMdf("MARVEL.mdf", 2448, 2);

        var disc = MdsParser.Parse(mds);

        Assert.Equal(2448, disc.SectorSize);
        Assert.True(disc.NeedsSubchannelStrip);
        Assert.False(disc.IsPlainRawCd);
        Assert.False(disc.IsDvdImage);
    }

    [Fact]
    public void MultiTrackWithAudioIsParsedInOrder()
    {
        // Thousand Arms: a data track then a CDDA track, both 2448.
        var mds = WriteMds("THOUSANDARMS2.mds", (0xEC, 1, 2448, 0), (0xA9, 2, 2448, 251028));
        WriteMdf("THOUSANDARMS2.mdf", 2448, 2);

        var disc = MdsParser.Parse(mds);

        Assert.Equal(2, disc.Tracks.Count);
        Assert.Equal(BinCueGenerator.Mode2, disc.Tracks[0].CueTrackType);
        Assert.Equal("AUDIO", disc.Tracks[1].CueTrackType);
        Assert.True(disc.Tracks[1].IsAudio);
        Assert.Equal(251028, disc.Tracks[1].StartLba);
        Assert.True(disc.AllTracksDescribable);
    }

    [Fact]
    public void CookedSectorSizeIsTreatedAsADvdImage()
    {
        // Xenosaga Episode II disc 2: 2048-byte sectors, so the .mdf is an ISO in all but name.
        var mds = WriteMds("Xenosaga II Disc 2.MDS", (0x02, 1, 2048, 0));
        WriteMdf("Xenosaga II Disc 2.mdf", 2048, 4);

        var disc = MdsParser.Parse(mds);

        Assert.True(disc.IsDvdImage);
    }

    [Fact]
    public void ImplausibleSessionCountIsRejected()
    {
        // A real corrupt descriptor reported 8233 sessions; walking that would read garbage.
        var mds = WriteMds("corrupt.mds", (0xEC, 1, 2352, 0));
        var bytes = File.ReadAllBytes(mds);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(SessionCountOffset), 8233);
        File.WriteAllBytes(mds, bytes);

        var ex = Assert.Throws<InvalidDataException>(() => MdsParser.Parse(mds));
        Assert.Contains("8233", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LeadInAndLeadOutEntriesAreSkipped()
    {
        // POINT values outside 1-99 describe lead-in/lead-out, not playable tracks.
        var mds = WriteMds(
            "leadin.mds",
            (0xEC, 0xA0, 2352, 0),
            (0xEC, 1, 2352, 0),
            (0xEC, 0xA2, 2352, 100)
        );

        var disc = MdsParser.Parse(mds);

        Assert.Single(disc.Tracks);
        Assert.Equal(1, disc.Tracks[0].Number);
    }

    [Fact]
    public void UnknownTrackModeIsNotDescribable()
    {
        var mds = WriteMds("weird.mds", (0x55, 1, 2352, 0));

        var disc = MdsParser.Parse(mds);

        Assert.Null(disc.Tracks[0].CueTrackType);
        Assert.False(disc.AllTracksDescribable);
    }

    [Fact]
    public async Task StripRemovesTheSubchannelTailAndKeepsTheData()
    {
        var mdf = WriteMdf("strip.mdf", 2448, 5);
        var stripped = Path.Combine(_tempDir, "stripped.bin");

        var failure = await MdsInputPreparer.StripSubchannelAsync(
            mdf,
            stripped,
            2448,
            CancellationToken.None
        );

        Assert.Null(failure);
        var bytes = await File.ReadAllBytesAsync(stripped);
        Assert.Equal(MdsDisc.RawSectorSize * 5, bytes.Length);

        // Every sector keeps its data bytes and loses the 0xFF subchannel tail.
        for (var sector = 0; sector < 5; sector++)
        {
            Assert.Equal((byte)(sector + 1), bytes[sector * MdsDisc.RawSectorSize]);
            Assert.Equal(
                (byte)(sector + 1),
                bytes[sector * MdsDisc.RawSectorSize + MdsDisc.RawSectorSize - 1]
            );
        }

        Assert.DoesNotContain((byte)0xFF, bytes);
    }

    [Fact]
    public async Task StripRejectsAnImageThatIsNotWholeSectors()
    {
        var mdf = WriteMdf("truncated.mdf", 2448, 3);
        await using (var fs = new FileStream(mdf, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(fs.Length - 100);
        }

        var failure = await MdsInputPreparer.StripSubchannelAsync(
            mdf,
            Path.Combine(_tempDir, "out.bin"),
            2448,
            CancellationToken.None
        );

        Assert.NotNull(failure);
        Assert.Contains("truncated", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparingA2448ImageProducesAStrippedImageAndACue()
    {
        var mds = WriteMds("MEGAMAN_X5.mds", (0xEC, 1, 2448, 0));
        WriteMdf("MEGAMAN_X5.mdf", 2448, 4);
        var workDir = Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(workDir);

        var disc = MdsParser.Parse(mds);
        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            _log.Add,
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.NotNull(result.CuePath);
        Assert.Null(result.DvdImagePath);

        var cue = await File.ReadAllTextAsync(result.CuePath!);

        // The stripped image carries a ".stripped" marker so it can never collide with a joined
        // split image, which is named after the descriptor.
        Assert.Contains("FILE \"MEGAMAN_X5.stripped.bin\" BINARY", cue, StringComparison.Ordinal);
        Assert.Contains("TRACK 01 MODE2/2352", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 00:00:00", cue, StringComparison.Ordinal);

        var strippedPath = Path.Combine(workDir, "MEGAMAN_X5.stripped.bin");
        Assert.True(File.Exists(strippedPath));
        Assert.Equal(MdsDisc.RawSectorSize * 4, new FileInfo(strippedPath).Length);
    }

    [Fact]
    public async Task PreparingA2352ImageWritesOnlyACueAndDoesNotCopyTheImage()
    {
        var mds = WriteMds("FFT.mds", (0xEC, 1, 2352, 0));
        WriteMdf("FFT.mdf", 2352, 4);
        var workDir = Path.Combine(_tempDir, "work2");
        Directory.CreateDirectory(workDir);

        var disc = MdsParser.Parse(mds);
        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            _log.Add,
            CancellationToken.None
        );

        Assert.True(result.Success);
        var cue = await File.ReadAllTextAsync(result.CuePath!);

        // Referenced relatively, not duplicated.
        Assert.Contains("FILE \"..\\FFT.mdf\" BINARY", cue, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(workDir, "FFT.mdf")));
        Assert.Single(Directory.GetFiles(workDir));
    }

    [Fact]
    public async Task PreparingMultiTrackWritesEveryTrackWithItsIndex()
    {
        var mds = WriteMds("THOUSANDARMS2.mds", (0xEC, 1, 2352, 0), (0xA9, 2, 2352, 251028));
        WriteMdf("THOUSANDARMS2.mdf", 2352, 4);
        var workDir = Path.Combine(_tempDir, "work3");
        Directory.CreateDirectory(workDir);

        var disc = MdsParser.Parse(mds);
        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            _log.Add,
            CancellationToken.None
        );

        var cue = await File.ReadAllTextAsync(result.CuePath!);
        Assert.Contains("TRACK 01 MODE2/2352", cue, StringComparison.Ordinal);
        Assert.Contains("TRACK 02 AUDIO", cue, StringComparison.Ordinal);

        // 251028 sectors = 55:47:03 at 75 frames per second.
        Assert.Contains("INDEX 01 55:47:03", cue, StringComparison.Ordinal);

        // One FILE line only: both tracks live in the same image.
        Assert.Equal(1, cue.Split("FILE ").Length - 1);
    }

    [Fact]
    public async Task PreparingA2048ImageAsksForADvdConversion()
    {
        var mds = WriteMds("Xeno.mds", (0x02, 1, 2048, 0));
        var mdf = WriteMdf("Xeno.mdf", 2048, 4);
        var workDir = Path.Combine(_tempDir, "work4");
        Directory.CreateDirectory(workDir);

        var disc = MdsParser.Parse(mds);
        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            _log.Add,
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.Null(result.CuePath);
        Assert.Equal(mdf, result.DvdImagePath);
        Assert.Empty(Directory.GetFiles(workDir));
    }

    [Fact]
    public async Task SplitAlcoholDataFileIsJoinedBeforeConversion()
    {
        // The Xenosaga layout: a .MDS describing 2048-byte sectors with the data split across
        // .I00 / .I01 and no .mdf at all.
        var mds = WriteMds("Xenosaga II Disc 2.MDS", (0x02, 1, 2048, 0));
        var partOne = Path.Combine(_tempDir, "Xenosaga II Disc 2.I00");
        var partTwo = Path.Combine(_tempDir, "Xenosaga II Disc 2.I01");
        File.WriteAllBytes(partOne, new byte[2048 * 3]);
        File.WriteAllBytes(partTwo, new byte[2048 * 2]);

        var disc = MdsParser.Parse(mds);

        // The first volume stands in for the missing .mdf.
        Assert.Equal(partOne, disc.MdfPath);

        var workDir = Path.Combine(_tempDir, "worksplit");
        Directory.CreateDirectory(workDir);
        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            _log.Add,
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.NotNull(result.DvdImagePath);

        // The joined image is what gets converted, and it holds every part.
        Assert.Equal(2048 * 5, new FileInfo(result.DvdImagePath!).Length);
        Assert.Contains(_log, m => m.Contains("2-part split image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingDataFileIsReportedNotThrown()
    {
        var mds = WriteMds("orphan.mds", (0xEC, 1, 2352, 0));
        var workDir = Path.Combine(_tempDir, "work5");
        Directory.CreateDirectory(workDir);

        var disc = MdsParser.Parse(mds);
        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            _log.Add,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            ".mdf data file was not found",
            result.FailureReason,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task UndescribableTrackModeIsReported()
    {
        var mds = WriteMds("odd.mds", (0x55, 1, 2352, 0));
        WriteMdf("odd.mdf", 2352, 2);
        var workDir = Path.Combine(_tempDir, "work6");
        Directory.CreateDirectory(workDir);

        var disc = MdsParser.Parse(mds);
        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            _log.Add,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains("cannot express in a cue", result.FailureReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(74, "00:00:74")]
    [InlineData(75, "00:01:00")]
    [InlineData(4500, "01:00:00")]
    [InlineData(251028, "55:47:03")]
    public void MsfFormattingMatchesTheCueConvention(long lba, string expected)
    {
        Assert.Equal(expected, MdsInputPreparer.FormatMsf(lba));
    }

    [Fact]
    public void MdfFileIsFoundByBaseNameEvenWhenSeveralExist()
    {
        var mds = WriteMds("Target.mds", (0xEC, 1, 2352, 0));
        var expected = WriteMdf("Target.mdf", 2352, 1);
        WriteMdf("Other.mdf", 2352, 1);

        var disc = MdsParser.Parse(mds);

        Assert.Equal(expected, disc.MdfPath);
    }

    [Fact]
    public void SoleMdfIsUsedWhenTheBaseNameDiffers()
    {
        var mds = WriteMds("Descriptor.mds", (0xEC, 1, 2352, 0));
        var expected = WriteMdf("Completely Different.mdf", 2352, 1);

        var disc = MdsParser.Parse(mds);

        Assert.Equal(expected, disc.MdfPath);
    }
}