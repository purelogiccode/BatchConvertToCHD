using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class InputFileFilterTests : IDisposable
{
    private readonly List<string> _log = [];
    private readonly string _tempDir;

    public InputFileFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"InputFileFilterTests_{Guid.NewGuid():N}");
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

    private string CreateFile(string relativeName, string content = "")
    {
        var path = Path.Combine(_tempDir, relativeName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, content);

        return path;
    }

    [Fact]
    public async Task CloneCdImgIsSuppressedByItsCcd()
    {
        // The exact case that destroyed finished conversions: the .ccd converts to Game.chd, then
        // the sibling .img is processed as its own input, fails, and the old code deleted Game.chd.
        var ccd = CreateFile("Game.ccd", "[CloneCD]\r\nVersion=3\r\n");
        var img = CreateFile("Game.img");

        var remaining = await InputFileFilter.RemoveCompanionDataFilesAsync(
            [ccd, img],
            _log.Add,
            CancellationToken.None
        );

        Assert.Equal([ccd], remaining);
        Assert.Contains(
            _log,
            m =>
                m.Contains("Game.img", StringComparison.Ordinal)
                && m.Contains("same base name", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task BinIsSuppressedByCueWithMatchingBaseName()
    {
        var cue = CreateFile("Game.cue", "FILE \"Game.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n");
        var bin = CreateFile("Game.bin");

        var remaining = await InputFileFilter.RemoveCompanionDataFilesAsync(
            [cue, bin],
            _log.Add,
            CancellationToken.None
        );

        Assert.Equal([cue], remaining);
    }

    [Fact]
    public async Task SplitTrackBinsAreSuppressedByReferenceNotBaseName()
    {
        // Redump-style split sets: the cue base name never matches the track bins, so suppression
        // has to come from reading the descriptor.
        var cue = CreateFile(
            "Game.cue",
            "FILE \"Game (Track 01).bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\nFILE \"Game (Track 02).bin\" BINARY\r\n  TRACK 02 AUDIO\r\n"
        );
        var track1 = CreateFile("Game (Track 01).bin");
        var track2 = CreateFile("Game (Track 02).bin");

        var remaining = await InputFileFilter.RemoveCompanionDataFilesAsync(
            [cue, track1, track2],
            _log.Add,
            CancellationToken.None
        );

        Assert.Equal([cue], remaining);
        Assert.All(
            _log,
            m => Assert.Contains("referenced by Game.cue", m, StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task UnrelatedImageInTheSameFolderIsKept()
    {
        // Suppression must be narrow: an image the descriptor does not cover stays in the batch.
        var cue = CreateFile("Game.cue", "FILE \"Game.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n");
        var bin = CreateFile("Game.bin");
        var other = CreateFile("Bonus Disc.iso");

        var remaining = await InputFileFilter.RemoveCompanionDataFilesAsync(
            [cue, bin, other],
            _log.Add,
            CancellationToken.None
        );

        Assert.Equal([cue, other], remaining);
    }

    [Fact]
    public async Task DescriptorInADifferentFolderDoesNotSuppress()
    {
        // Only same-directory pairs are companions; a like-named cue elsewhere is a different disc.
        var cue = CreateFile(Path.Combine("discA", "Game.cue"), "FILE \"Game.bin\" BINARY\r\n");
        var img = CreateFile(Path.Combine("discB", "Game.img"));

        var remaining = await InputFileFilter.RemoveCompanionDataFilesAsync(
            [cue, img],
            _log.Add,
            CancellationToken.None
        );

        Assert.Equal([cue, img], remaining);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task ImageWithNoDescriptorIsKept()
    {
        var img = CreateFile("Standalone.img");
        var iso = CreateFile("Another.iso");

        var remaining = await InputFileFilter.RemoveCompanionDataFilesAsync(
            [img, iso],
            _log.Add,
            CancellationToken.None
        );

        Assert.Equal([img, iso], remaining);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task ExtensionMatchingIsCaseInsensitive()
    {
        var ccd = CreateFile("SAIYUKI.CCD", "[CloneCD]\r\n");
        var img = CreateFile("SAIYUKI.IMG");

        var remaining = await InputFileFilter.RemoveCompanionDataFilesAsync(
            [ccd, img],
            _log.Add,
            CancellationToken.None
        );

        Assert.Equal([ccd], remaining);
    }

    [Fact]
    public async Task InputOrderIsPreserved()
    {
        var first = CreateFile("A.iso");
        var cue = CreateFile("B.cue", "FILE \"B.bin\" BINARY\r\n");
        var bin = CreateFile("B.bin");
        var last = CreateFile("C.iso");

        var remaining = await InputFileFilter.RemoveCompanionDataFilesAsync(
            [first, cue, bin, last],
            _log.Add,
            CancellationToken.None
        );

        Assert.Equal([first, cue, last], remaining);
    }

    [Fact]
    public async Task SuppressionRecordsTheCoveringDescriptor()
    {
        var ccd = CreateFile("Vagrant.ccd", "[CloneCD]\r\n");
        var img = CreateFile("Vagrant.img");

        var suppressions = await InputFileFilter.FindCompanionSuppressionsAsync(
            [ccd, img],
            CancellationToken.None
        );

        var suppression = Assert.Single(suppressions);
        Assert.Equal(img, suppression.DataFile);
        Assert.Equal(ccd, suppression.Descriptor);
        Assert.True(suppression.MatchedByName);
        Assert.Contains("Vagrant.ccd", suppression.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsSuppressedWhenOnlyDescriptorsAreSelected()
    {
        var cue = CreateFile("Game.cue", "FILE \"Game.bin\" BINARY\r\n");
        CreateFile("Game.bin");

        var remaining = await InputFileFilter.RemoveCompanionDataFilesAsync(
            [cue],
            _log.Add,
            CancellationToken.None
        );

        Assert.Equal([cue], remaining);
        Assert.Empty(_log);
    }

    [Fact]
    public void CollidingOutputNamesAreReported()
    {
        // Game.cue and Game.zip both resolve to Game.chd.
        string[] inputs = [@"C:\in\Game.cue", @"C:\in\Game.zip", @"C:\in\Other.iso"];

        var collisions = InputFileFilter.FindOutputCollisions(
            inputs,
            f => Path.Combine("C:\\out", Path.GetFileNameWithoutExtension(f) + ".chd")
        );

        var collision = Assert.Single(collisions);
        Assert.Equal(2, collision.Count());
        Assert.Contains(@"C:\in\Game.cue", collision, StringComparer.Ordinal);
        Assert.Contains(@"C:\in\Game.zip", collision, StringComparer.Ordinal);
    }

    [Fact]
    public void DistinctOutputNamesReportNoCollision()
    {
        string[] inputs = [@"C:\in\A.cue", @"C:\in\B.cue"];

        var collisions = InputFileFilter.FindOutputCollisions(
            inputs,
            f => Path.Combine("C:\\out", Path.GetFileNameWithoutExtension(f) + ".chd")
        );

        Assert.Empty(collisions);
    }

    [Fact]
    public void OutputCollisionComparisonIgnoresCase()
    {
        string[] inputs = [@"C:\in\game.cue", @"C:\in\GAME.zip"];

        var collisions = InputFileFilter.FindOutputCollisions(
            inputs,
            f => Path.Combine("C:\\out", Path.GetFileNameWithoutExtension(f) + ".chd")
        );

        Assert.Single(collisions);
    }

    [Fact]
    public void CollidingArchiveIsSkippedInFavorOfTheOriginalImage()
    {
        // The NRG report: Game.7z and Game.iso both convert to Game.chd. The archive
        // extraction is redundant when the same image sits beside it, so the .iso wins.
        string[] inputs = [@"C:\in\Game.7z", @"C:\in\Game.iso"];

        var (kept, skipped) = InputFileFilter.ResolveOutputCollisions(
            inputs,
            f => Path.Combine("C:\\out", Path.GetFileNameWithoutExtension(f) + ".chd")
        );

        Assert.Equal([@"C:\in\Game.iso"], kept);
        var duplicate = Assert.Single(skipped);
        Assert.Equal(@"C:\in\Game.7z", duplicate.SkippedFile);
        Assert.Equal(@"C:\in\Game.iso", duplicate.KeptFile);
        Assert.Equal(@"C:\out\Game.chd", duplicate.OutputPath);
    }

    [Fact]
    public void CollidingArchiveIsSkippedRegardlessOfInputOrder()
    {
        string[] inputs = [@"C:\in\Game.iso", @"C:\in\Game.7z"];

        var (kept, skipped) = InputFileFilter.ResolveOutputCollisions(
            inputs,
            f => Path.Combine("C:\\out", Path.GetFileNameWithoutExtension(f) + ".chd")
        );

        Assert.Equal([@"C:\in\Game.iso"], kept);
        Assert.Single(skipped);
    }

    [Fact]
    public void CollisionBetweenTwoArchivesKeepsTheFirst()
    {
        string[] inputs = [@"C:\in\Game.zip", @"C:\in\Game.rar"];

        var (kept, skipped) = InputFileFilter.ResolveOutputCollisions(
            inputs,
            f => Path.Combine("C:\\out", Path.GetFileNameWithoutExtension(f) + ".chd")
        );

        Assert.Equal([@"C:\in\Game.zip"], kept);
        Assert.Equal(@"C:\in\Game.rar", Assert.Single(skipped).SkippedFile);
    }

    [Fact]
    public void NonCollidingInputsPassThroughUnchanged()
    {
        string[] inputs = [@"C:\in\A.iso", @"C:\in\B.cue", @"C:\in\C.bin"];

        var (kept, skipped) = InputFileFilter.ResolveOutputCollisions(
            inputs,
            f => Path.Combine("C:\\out", Path.GetFileNameWithoutExtension(f) + ".chd")
        );

        Assert.Equal(inputs, kept);
        Assert.Empty(skipped);
    }

    [Fact]
    public void ThreeWayCollisionKeepsOneAndSkipsTwo()
    {
        string[] inputs = [@"C:\in\Game.7z", @"C:\in\Game.iso", @"C:\in\GAME.ZIP"];

        var (kept, skipped) = InputFileFilter.ResolveOutputCollisions(
            inputs,
            f => Path.Combine("C:\\out", Path.GetFileNameWithoutExtension(f) + ".chd")
        );

        Assert.Equal([@"C:\in\Game.iso"], kept);
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, s => Assert.Equal(@"C:\in\Game.iso", s.KeptFile));
    }

    // --- RemoveRarVolumeParts ---

    [Fact]
    public void RemoveRarVolumePartsKeepsOnlyTheFirstVolume()
    {
        var first = CreateFile("game.part01.rar");
        var second = CreateFile("game.part02.rar");
        var tenth = CreateFile("game.part10.rar");

        var remaining = InputFileFilter.RemoveRarVolumeParts([first, second, tenth], _log.Add);

        Assert.Equal([first], remaining);
        Assert.Contains(_log, m => m.Contains("game.part02.rar", StringComparison.Ordinal));
        Assert.Contains(_log, m => m.Contains("game.part10.rar", StringComparison.Ordinal));
        Assert.Contains(_log, m => m.Contains("game.part01.rar", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveRarVolumePartsKeepsTheLowestPartWhenTheFirstIsMissing()
    {
        var fifth = CreateFile("game.part05.rar");
        var sixth = CreateFile("game.part06.rar");

        var remaining = InputFileFilter.RemoveRarVolumeParts([fifth, sixth], _log.Add);

        Assert.Equal([fifth], remaining);
        Assert.Single(_log);
    }

    [Fact]
    public void RemoveRarVolumePartsLeavesLonePartsAndPlainArchivesAlone()
    {
        var loneFirst = CreateFile("game.part01.rar");
        var loneLater = CreateFile("other.part07.rar");
        var plain = CreateFile("plain.rar");

        var remaining = InputFileFilter.RemoveRarVolumeParts(
            [loneFirst, loneLater, plain],
            _log.Add
        );

        Assert.Equal([loneFirst, loneLater, plain], remaining);
        Assert.Empty(_log);
    }

    [Fact]
    public void RemoveRarVolumePartsKeepsSetsInDifferentFoldersSeparate()
    {
        var aFirst = CreateFile("A.part01.rar");
        var aSecond = CreateFile("A.part02.rar");
        var bFirst = CreateFile(Path.Combine("sub", "B.part01.rar"));
        var bSecond = CreateFile(Path.Combine("sub", "B.part02.rar"));

        var remaining = InputFileFilter.RemoveRarVolumeParts(
            [aFirst, aSecond, bFirst, bSecond],
            _log.Add
        );

        Assert.Equal([aFirst, bFirst], remaining);
        Assert.Equal(2, _log.Count);
    }

    [Fact]
    public void RemoveRarVolumePartsPrefersTheFirstVolumeSpellingWithAContinuation()
    {
        // Both names parse as part 1; only the padded one has its next volume on disk, so that is
        // the one that has to be kept, whatever order the batch lists them in.
        var unpadded = CreateFile("game.part1.rar");
        var padded = CreateFile("game.part01.rar");
        CreateFile("game.part02.rar");

        var remaining = InputFileFilter.RemoveRarVolumeParts([unpadded, padded], _log.Add);

        Assert.Equal([padded], remaining);
        Assert.Single(_log);
    }
}