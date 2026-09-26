using System.Text;
using MDSSharp;

namespace BatchConvertToCHD.Tests;

/// <summary>
///     MDS v2 (Daemon Tools) descriptor and track-data support. The .mds/.mdf/.mdx fixtures in
///     Fixtures/MdsV2 are the test images from the MIT-licensed mdsx project
///     (https://github.com/Marisa-Chan/mdsx): test.mds has plain track data, test_compress.mds and
///     test_compress.mdx store it compressed (the latter as a single-file MDX container), and
///     test_pazz.mds encrypts it with the password "pazz". Every decoded variant must reproduce the
///     plain test.mdf byte-for-byte, which pins the decryption pipeline (RIPEMD-160 PBKDF2,
///     AES-256 CBC/LRW, GF(2^128) tweaks, zlib) to the real format.
/// </summary>
public class MdsV2Tests : IDisposable
{
    private const string ExpectedImageSha256 =
        "F49667CC27D051908AF7203696206CC00E822898D656D83DFF7C7932C894F0E0";

    private readonly string _tempDir;

    public MdsV2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"MdsV2Tests_{Guid.NewGuid():N}");
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
            // best effort
        }
    }

    private static string FixturePath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "MdsV2", name);
    }

    private string StageFixture(string name)
    {
        var target = Path.Combine(_tempDir, name);
        File.Copy(FixturePath(name), target, true);
        return target;
    }

    private void StageDataFile(string name)
    {
        File.Copy(FixturePath(name), Path.Combine(_tempDir, name), true);
    }

    private string WorkDir(string name)
    {
        var path = Path.Combine(_tempDir, "work_" + name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task AssertDecodedImageMatchesAsync(string mdsPath, string workDir, string? password)
    {
        var disc = MdsParser.Parse(mdsPath);
        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            null,
            CancellationToken.None,
            password
        );

        Assert.True(result.Success, result.FailureReason);

        var decodedPath = Path.Combine(
            workDir,
            Path.GetFileNameWithoutExtension(mdsPath) + ".decoded.bin"
        );
        Assert.True(File.Exists(decodedPath), $"decoded file not found at {decodedPath}");

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(decodedPath))
        );
        Assert.Equal(ExpectedImageSha256, hash);
    }

    [Theory]
    [InlineData("", "9c1185a5c5e9fc54612808977ee8f548b2258d31")]
    [InlineData("a", "0bdc9d2d256b3ee9daae347be6f4dc835a467ffe")]
    [InlineData("abc", "8eb208f7e05d987a9b044a8e98c6b087f15a0bfc")]
    [InlineData("message digest", "5d0689ef49d2fae572b881b123a85ffa21595f36")]
    [InlineData(
        "abcdefghijklmnopqrstuvwxyz",
        "f71c27109c692c1b56bbdceb5b9d2865b3708dbc"
    )]
    public void Ripemd160MatchesKnownVectors(string message, string expected)
    {
        var digest = Ripemd160.Hash(Encoding.ASCII.GetBytes(message));

        Assert.Equal(expected, Convert.ToHexString(digest).ToLowerInvariant());
    }

    [Fact]
    public void DecryptsAndParsesV2Descriptor()
    {
        var mdsPath = StageFixture("test.mds");
        // The fixture declares "*.mdf"; a correctly sized placeholder is all the parser needs.
        File.WriteAllBytes(Path.Combine(_tempDir, "test.mdf"), new byte[177 * 2048]);

        var disc = MdsParser.Parse(mdsPath);

        Assert.Equal(1, disc.SessionCount);
        Assert.Equal(MdsMedium.Cd, disc.MediumType);
        Assert.False(disc.HasEncryptedTrackData);
        Assert.False(disc.HasCompressedTrackData);
        Assert.False(disc.IsMdxContainer);
        Assert.Single(disc.Tracks);

        var track = disc.Tracks[0];
        Assert.Equal(1, track.Number);
        Assert.Equal("MODE1/2048", track.CueTrackType);
        Assert.Equal(2048, track.SectorSize);
        Assert.Equal(150, track.PregapSectors);
        Assert.Equal(177, track.LengthSectors);
        Assert.NotNull(disc.MdfPath);
        Assert.EndsWith("test.mdf", disc.MdfPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V2EncryptedTrackDataIsDetected()
    {
        var disc = MdsParser.Parse(StageFixture("test_pass.mds"));

        Assert.True(disc.HasEncryptedTrackData);
        Assert.False(disc.HasCompressedTrackData);
        Assert.Single(disc.Tracks);
    }

    [Fact]
    public void V2CompressedTrackDataIsDetected()
    {
        var disc = MdsParser.Parse(StageFixture("test_compress.mds"));

        Assert.True(disc.HasCompressedTrackData);
        Assert.False(disc.HasEncryptedTrackData);
        Assert.Single(disc.Tracks);
    }

    [Fact]
    public async Task PrepareAsyncWritesCueForPlainV2Image()
    {
        var mdsPath = StageFixture("test.mds");
        File.WriteAllBytes(Path.Combine(_tempDir, "test.mdf"), new byte[177 * 2048]);
        var disc = MdsParser.Parse(mdsPath);
        var workDir = WorkDir("plain");

        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            workDir,
            null,
            CancellationToken.None
        );

        Assert.True(result.Success, result.FailureReason);
        Assert.NotNull(result.CuePath);
        var cue = await File.ReadAllTextAsync(result.CuePath);
        Assert.Contains("TRACK 01 MODE1/2048", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 00:00:00", cue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsyncDecodesCompressedImage()
    {
        var mdsPath = StageFixture("test_compress.mds");
        StageDataFile("test_compress.mdf");

        await AssertDecodedImageMatchesAsync(mdsPath, WorkDir("compress"), password: null);
    }

    [Fact]
    public async Task PrepareAsyncDecodesCompressedMdxContainer()
    {
        var mdxPath = StageFixture("test_compress.mdx");
        var disc = MdsParser.Parse(mdxPath);
        Assert.True(disc.IsMdxContainer);

        await AssertDecodedImageMatchesAsync(mdxPath, WorkDir("mdx"), password: null);
    }

    [Fact]
    public async Task PrepareAsyncDecodesPasswordEncryptedImage()
    {
        var mdsPath = StageFixture("test_pazz.mds");
        StageDataFile("test_pazz.mdf");

        await AssertDecodedImageMatchesAsync(mdsPath, WorkDir("pazz"), password: "pazz");
    }

    [Fact]
    public async Task PrepareAsyncEncryptedImageWithoutPasswordFailsWithGuidance()
    {
        var mdsPath = StageFixture("test_pazz.mds");
        StageDataFile("test_pazz.mdf");
        var disc = MdsParser.Parse(mdsPath);

        var result = await MdsInputPreparer.PrepareAsync(
            disc,
            WorkDir("pazz-none"),
            null,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains("password", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MdxSingleFileContainerIsDetected()
    {
        var mdxPath = StageFixture("test_compress.mdx");

        var disc = MdsParser.Parse(mdxPath);

        Assert.True(disc.IsMdxContainer);
        Assert.Single(disc.DataFilePaths);
        Assert.Equal(mdxPath, disc.DataFilePaths[0]);
    }
}
