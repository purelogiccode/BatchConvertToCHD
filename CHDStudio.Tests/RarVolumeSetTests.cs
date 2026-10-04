namespace CHDStudio.Tests;

public class RarVolumeSetTests : IDisposable
{
    private readonly string _tempDir;

    public RarVolumeSetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RarVolumeSetTests_{Guid.NewGuid():N}");
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

    private string WriteFile(string fileName, int size = 4)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Theory]
    [InlineData("game.part01.rar", "game", 1)]
    [InlineData("game.part1.rar", "game", 1)]
    [InlineData("hyk-D2-2.part22.rar", "hyk-D2-2", 22)]
    [InlineData("Game.PART7.RAR", "Game", 7)]
    [InlineData("My Game (Disc 1).part003.rar", "My Game (Disc 1)", 3)]
    public void TryGetPartInfoRecognisesPartNames(
        string fileName,
        string expectedBaseName,
        int expectedPartNumber
    )
    {
        var recognised = Utilities.RarVolumeSet.TryGetPartInfo(
            Path.Combine(_tempDir, fileName),
            out var setBaseName,
            out var partNumber
        );

        Assert.True(recognised);
        Assert.Equal(expectedBaseName, setBaseName);
        Assert.Equal(expectedPartNumber, partNumber);
    }

    [Theory]
    [InlineData("game.rar")]
    [InlineData("game.part.rar")]
    [InlineData("game.part01.7z")]
    [InlineData("game.part01.rar.txt")]
    [InlineData("part01.rar")]
    public void TryGetPartInfoRejectsOtherNames(string fileName)
    {
        Assert.False(
            Utilities.RarVolumeSet.TryGetPartInfo(
                Path.Combine(_tempDir, fileName),
                out var setBaseName,
                out var partNumber
            )
        );
        Assert.Empty(setBaseName);
        Assert.Equal(0, partNumber);
    }

    [Theory]
    [InlineData("game.part02.rar", true)]
    [InlineData("game.part01.rar", false)]
    [InlineData("game.rar", false)]
    public void IsLaterPartClassifiesVolumeNames(string fileName, bool expected)
    {
        Assert.Equal(expected, Utilities.RarVolumeSet.IsLaterPart(Path.Combine(_tempDir, fileName)));
    }

    [Fact]
    public void FindFirstVolumeReturnsTheInputForASingleArchive()
    {
        var single = WriteFile("game.rar");

        Assert.Equal(single, Utilities.RarVolumeSet.FindFirstVolume(single));
    }

    [Fact]
    public void FindFirstVolumeRedirectsALaterPartToTheFirstVolume()
    {
        var first = WriteFile("set.part01.rar");
        WriteFile("set.part02.rar");
        var third = WriteFile("set.part03.rar");

        Assert.Equal(first, Utilities.RarVolumeSet.FindFirstVolume(third));
    }

    [Fact]
    public void FindFirstVolumeIsCaseInsensitive()
    {
        var first = WriteFile("Game.Part01.RAR");
        var third = WriteFile("game.part03.rar");

        Assert.Equal(first, Utilities.RarVolumeSet.FindFirstVolume(third));
    }

    [Fact]
    public void FindFirstVolumeReturnsNullWhenTheFirstVolumeIsMissing()
    {
        WriteFile("set.part02.rar");
        var third = WriteFile("set.part03.rar");

        Assert.Null(Utilities.RarVolumeSet.FindFirstVolume(third));
    }

    [Fact]
    public void FindFirstVolumeIgnoresOtherSetsInTheSameFolder()
    {
        var first = WriteFile("game (USA).part01.rar");
        WriteFile("game (Japan).part01.rar");
        var target = WriteFile("game (USA).part12.rar");

        Assert.Equal(first, Utilities.RarVolumeSet.FindFirstVolume(target));
    }

    [Fact]
    public void GetVolumePathsOrdersPartVolumesNumerically()
    {
        var first = WriteFile("set.part01.rar");
        var second = WriteFile("set.part02.rar");
        var tenth = WriteFile("set.part10.rar");

        Assert.Equal([first, second, tenth], Utilities.RarVolumeSet.GetVolumePaths(tenth));
    }

    [Fact]
    public void GetVolumePathsStopsNumberedVolumesAtTheFirstGap()
    {
        var first = WriteFile("disc.001");
        var second = WriteFile("disc.002");
        WriteFile("disc.010");

        Assert.Equal([first, second], Utilities.RarVolumeSet.GetVolumePaths(first));
    }

    [Fact]
    public void GetVolumePathsIncludesOldStyleContinuations()
    {
        var first = WriteFile("set.rar");
        var second = WriteFile("set.r00");
        var third = WriteFile("set.r01");

        Assert.Equal([first, second, third], Utilities.RarVolumeSet.GetVolumePaths(first));
    }

    [Fact]
    public void GetVolumePathsReturnsTheFileAloneWhenThereAreNoSiblings()
    {
        var single = WriteFile("game.rar");

        Assert.Equal([single], Utilities.RarVolumeSet.GetVolumePaths(single));
    }

    [Fact]
    public void GetTotalBytesSumsEveryVolume()
    {
        WriteFile("set.part01.rar", 10);
        WriteFile("set.part02.rar", 20);
        var third = WriteFile("set.part03.rar", 30);

        Assert.Equal(60, Utilities.RarVolumeSet.GetTotalBytes(third));
    }

    [Fact]
    public void GetFirstVolumeNameMirrorsTheObservedPadding()
    {
        Assert.Equal(
            "hyk-D2-2.part01.rar",
            Utilities.RarVolumeSet.GetFirstVolumeName(Path.Combine(_tempDir, "hyk-D2-2.part22.rar"))
        );
        Assert.Equal(
            "game.part1.rar",
            Utilities.RarVolumeSet.GetFirstVolumeName(Path.Combine(_tempDir, "game.part2.rar"))
        );
    }
}