using System.Diagnostics;
using System.Text;
using BatchConvertToCHD.Utilities;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BatchConvertToCHD.Tests;

public class CueWorkDirectoryTests : IDisposable
{
    private readonly string _tempDir;

    static CueWorkDirectoryTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public CueWorkDirectoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CueWorkDirectoryTests_{Guid.NewGuid():N}");
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

    private string CreateFile(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content, encoding ?? Encoding.UTF8);
        return path;
    }

    private static async Task<(CueWorkDirectoryResult Result, string? WorkDir)> PrepareAsync(
        string cuePath,
        IMp3Decoder? decoder = null
    )
    {
        var result = await CueWorkDirectory.PrepareAsync(
            cuePath,
            "TestPrefix_",
            decoder,
            null,
            CancellationToken.None
        );
        return (result, result.WorkDir);
    }

    [Fact]
    public async Task PrepareAsyncCanonicalAsciiCueNeedsNoWorkDir()
    {
        CreateFile("track1.bin", "dummy");
        // Written without a BOM — a canonical ASCII cue needs no work directory.
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            new UTF8Encoding(false)
        );

        var (result, workDir) = await PrepareAsync(cuePath);

        Assert.Null(result.WorkCuePath);
        Assert.Null(workDir);
        Assert.Empty(result.UnresolvedNames);
    }

    [Fact]
    public async Task PrepareAsyncKoreanCp949CueCreatesAsciiWorkDirWithAllFiles()
    {
        const string koreanName = "진설 사무라이 스피리츠 무사도열전.bin";
        var sourceBinPath = CreateFile(koreanName, "bin-content");
        var cuePath = CreateFile(
            "game.cue",
            $"FILE \"{koreanName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.GetEncoding(949)
        );

        var (result, workDir) = await PrepareAsync(cuePath);

        try
        {
            Assert.NotNull(workDir);
            Assert.NotNull(result.WorkCuePath);
            Assert.True(Directory.Exists(workDir));

            var files = Directory
                .GetFiles(workDir)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToList();
            Assert.Equal(["game.cue", "track01.bin"], files);

            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.Contains("FILE \"track01.bin\" BINARY", workCue, StringComparison.Ordinal);

            var copiedBin = Path.Combine(workDir, "track01.bin");
            Assert.Equal(
                await File.ReadAllTextAsync(sourceBinPath),
                await File.ReadAllTextAsync(copiedBin)
            );
        }
        finally
        {
            if (workDir is not null)
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    [Fact]
    public async Task PrepareAsyncZeroPaddingMismatchCreatesWorkDirWithResolvedName()
    {
        CreateFile("Game (Track 2).bin", "dummy");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"Game (Track 02).bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00"
        );

        var (result, workDir) = await PrepareAsync(cuePath);

        try
        {
            Assert.NotNull(workDir);
            Assert.NotNull(result.WorkCuePath);
            Assert.Equal(
                ["game.cue", "track01.bin"],
                Directory
                    .GetFiles(workDir)
                    .Select(Path.GetFileName)
                    .OrderBy(static f => f, StringComparer.Ordinal)
                    .ToList()
            );

            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.Contains("FILE \"track01.bin\" BINARY", workCue, StringComparison.Ordinal);
        }
        finally
        {
            if (workDir is not null)
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    [Fact]
    public async Task TryWriteInPlaceWorkCueAsyncWritesBomFreeRelativeCueWithoutCopyingBins()
    {
        // A UTF-8 BOM breaks chdman's cue parser ("couldn't find bin file []" — the BOM is
        // not skipped, so the first FILE directive is never parsed). The fix: prepare a
        // BOM-free canonical cue in a work directory that references each bin via a relative
        // path, WITHOUT copying the (potentially huge) bins.
        var binPath = CreateFile("track1.bin", "bin-content");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.UTF8
        ); // Encoding.UTF8 writes a BOM

        // Work directory on the same drive as the bin (inside the test temp dir) —
        // this is what makes the relative in-place path possible.
        var workDir = Path.Combine(_tempDir, "work");
        Directory.CreateDirectory(workDir);

        try
        {
            var workCuePath = await CueWorkDirectory.TryWriteInPlaceWorkCueAsync(
                cuePath,
                workDir,
                CancellationToken.None
            );
            Assert.NotNull(workCuePath);

            // No bins are copied into the work directory.
            Assert.Equal(
                ["game.cue"],
                Directory
                    .GetFiles(workDir)
                    .Select(Path.GetFileName)
                    .OrderBy(static f => f, StringComparer.Ordinal)
                    .ToList()
            );

            // The work cue must not start with a BOM.
            var workCueBytes = await File.ReadAllBytesAsync(workCuePath);
            Assert.False(workCueBytes is [0xEF, 0xBB, 0xBF, ..]);

            // The FILE line must reference the bin via a relative path that resolves to it.
            var workCue = await File.ReadAllTextAsync(workCuePath, Encoding.UTF8);
            var firstQuote = workCue.IndexOf('"');
            var lastQuote = workCue.LastIndexOf('"');
            Assert.True(
                firstQuote != -1 && lastQuote > firstQuote,
                $"no quoted FILE line found in work cue: {workCue}"
            );
            var referencedName = workCue[(firstQuote + 1)..lastQuote];
            Assert.False(
                Path.IsPathRooted(referencedName),
                "FILE line should reference the bin relatively, not absolutely"
            );
            var referenced = Path.GetFullPath(Path.Combine(workDir, referencedName));
            Assert.Equal(Path.GetFullPath(binPath), referenced);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, true);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    [Fact]
    public async Task TryWriteInPlaceWorkCueAsyncReturnsNullWhenReferenceUnresolved()
    {
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"missing.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.UTF8
        );

        // Missing bin: resolution fails, so the in-place fast path must decline.
        var workDir = Path.Combine(_tempDir, "work2");
        Directory.CreateDirectory(workDir);

        try
        {
            var workCuePath = await CueWorkDirectory.TryWriteInPlaceWorkCueAsync(
                cuePath,
                workDir,
                CancellationToken.None
            );
            Assert.Null(workCuePath);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, true);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    [Fact]
    public async Task PrepareAsyncUtf8BomCuePreparesBomFreeWorkCue()
    {
        // PrepareAsync with a BOM'd cue must produce a work cue (either in-place or copy-based,
        // depending on drive layout) that is BOM-free — that is what fixes chdman's
        // "couldn't find bin file []".
        CreateFile("track1.bin", "bin-content");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.UTF8
        );

        var (result, workDir) = await PrepareAsync(cuePath);

        try
        {
            Assert.NotNull(workDir);
            Assert.NotNull(result.WorkCuePath);
            Assert.Empty(result.UnresolvedNames);

            var workCueBytes = await File.ReadAllBytesAsync(result.WorkCuePath);
            Assert.False(
                workCueBytes is [0xEF, 0xBB, 0xBF, ..],
                "work cue must not start with a UTF-8 BOM"
            );
        }
        finally
        {
            if (workDir is not null)
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    [Fact]
    public async Task PrepareAsyncUtf8BomCueWithMp3TrackFallsBackToDecodePath()
    {
        // BOM + MP3 track: the MP3 must still be decoded into the work directory, so the
        // copy/decode path is used instead of the in-place fast path.
        CreateFile("track1.mp3", "mp3-content");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.mp3\" MP3\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00",
            Encoding.UTF8
        );
        var decoder = new FakeMp3Decoder();

        var (result, workDir) = await PrepareAsync(cuePath, decoder);

        try
        {
            Assert.NotNull(workDir);
            Assert.NotNull(result.WorkCuePath);
            Assert.Equal(
                ["game.cue", "track01.wav"],
                Directory
                    .GetFiles(workDir)
                    .Select(Path.GetFileName)
                    .OrderBy(static f => f, StringComparer.Ordinal)
                    .ToList()
            );

            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.Contains("FILE \"track01.wav\" WAVE", workCue, StringComparison.Ordinal);
        }
        finally
        {
            if (workDir is not null)
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    [Fact]
    public async Task PrepareAsyncUtf8BomCueWithNonAsciiNamesCopiesBins()
    {
        // BOM + non-ASCII referenced names: fall back to the copy-based path so chdman gets
        // a fully ASCII self-contained cue set.
        const string koreanName = "진설 사무라이 스피리츠 무사도열전.bin";
        var sourceBinPath = CreateFile(koreanName, "bin-content");
        var cuePath = CreateFile(
            "game.cue",
            $"FILE \"{koreanName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.UTF8
        );

        var (result, workDir) = await PrepareAsync(cuePath);

        try
        {
            Assert.NotNull(workDir);
            Assert.NotNull(result.WorkCuePath);
            Assert.Equal(
                ["game.cue", "track01.bin"],
                Directory
                    .GetFiles(workDir)
                    .Select(Path.GetFileName)
                    .OrderBy(static f => f, StringComparer.Ordinal)
                    .ToList()
            );

            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.Contains("FILE \"track01.bin\" BINARY", workCue, StringComparison.Ordinal);

            var copiedBin = Path.Combine(workDir, "track01.bin");
            Assert.Equal(
                await File.ReadAllTextAsync(sourceBinPath),
                await File.ReadAllTextAsync(copiedBin)
            );
        }
        finally
        {
            if (workDir is not null)
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    [Fact]
    public async Task PrepareAsyncUtf8BomCueWithZeroPaddingMismatchPreparesWorkCue()
    {
        // BOM + zero-padding mismatch ("(Track 02)" vs "(Track 2)"): the canonical rewrite must
        // keep the correction while remaining BOM-free, via either the in-place or the copy path.
        CreateFile("Game (Track 2).bin", "dummy");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"Game (Track 02).bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.UTF8
        );

        var (result, workDir) = await PrepareAsync(cuePath);

        try
        {
            Assert.NotNull(workDir);
            Assert.NotNull(result.WorkCuePath);
            Assert.Empty(result.UnresolvedNames);

            var workCueBytes = await File.ReadAllBytesAsync(result.WorkCuePath);
            Assert.False(
                workCueBytes is [0xEF, 0xBB, 0xBF, ..],
                "work cue must not start with a UTF-8 BOM"
            );

            // The zero-padding correction must be applied: the uncorrected name may never appear.
            // Depending on drive layout the file is either referenced in place under its corrected
            // on-disk name (fast path) or copied into the work dir under a trackNN name (copy path).
            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.DoesNotContain("Track 02", workCue, StringComparison.Ordinal);
            Assert.DoesNotContain("(Track 02", workCue, StringComparison.OrdinalIgnoreCase);

            var filesInWorkDir = Directory.GetFiles(workDir).Select(Path.GetFileName).ToList();
            if (filesInWorkDir.Contains("track01.bin", StringComparer.OrdinalIgnoreCase))
                Assert.Contains("FILE \"track01.bin\" BINARY", workCue, StringComparison.Ordinal);
            else
                Assert.Contains("Game (Track 2).bin", workCue, StringComparison.Ordinal);
        }
        finally
        {
            if (workDir is not null)
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    [Fact]
    public async Task PrepareAsyncMissingReferenceReturnsNoWorkDirAndReportsUnresolved()
    {
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"missing.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00"
        );

        var (result, workDir) = await PrepareAsync(cuePath);

        Assert.Null(result.WorkCuePath);
        Assert.Null(workDir);
        Assert.Contains("missing.bin", result.UnresolvedNames, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PrepareAsyncMp3TrackDecodesToWavInWorkDir()
    {
        var mp3Path = CreateFile("track1.mp3", "mp3-content");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.mp3\" MP3\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00"
        );
        var decoder = new FakeMp3Decoder();

        var (result, workDir) = await PrepareAsync(cuePath, decoder);

        try
        {
            Assert.NotNull(workDir);
            Assert.NotNull(result.WorkCuePath);
            Assert.Equal(
                ["game.cue", "track01.wav"],
                Directory
                    .GetFiles(workDir)
                    .Select(Path.GetFileName)
                    .OrderBy(static f => f, StringComparer.Ordinal)
                    .ToList()
            );

            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.Contains("FILE \"track01.wav\" WAVE", workCue, StringComparison.Ordinal);
            Assert.DoesNotContain("track1.mp3", workCue, StringComparison.Ordinal);

            var call = Assert.Single(decoder.Calls);
            Assert.Equal(mp3Path, call.Mp3Path);
            Assert.EndsWith("track01.wav", call.WavPath, StringComparison.Ordinal);
        }
        finally
        {
            if (workDir is not null)
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    [Fact]
    public async Task PrepareAsyncMp3TrackWithoutDecoderRunsDirectConversion()
    {
        CreateFile("track1.mp3", "mp3-content");
        // Written without a BOM — otherwise the BOM would trigger a work directory.
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.mp3\" MP3\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00",
            new UTF8Encoding(false)
        );

        var (result, workDir) = await PrepareAsync(cuePath);

        // Without a decoder the cue is canonical ASCII, so no work directory is prepared and
        // chdman's own "Unhandled track type MP3" error surfaces to the user.
        Assert.Null(result.WorkCuePath);
        Assert.Null(workDir);
        Assert.Empty(result.UnresolvedNames);
    }

    [Fact]
    public async Task PrepareAsyncKeepsWaveAndAiffTracksAsIs()
    {
        // WAVE/AIFF tracks are already supported by chdman — only MP3 gets decoded.
        CreateFile("Game (Track 2).bin", "dummy");
        CreateFile("track2.wav", "wav-data");
        CreateFile("track3.aiff", "aiff-data");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"Game (Track 02).bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"
            + "FILE \"track2.wav\" WAVE\r\n  TRACK 02 AUDIO\r\n    INDEX 01 00:00:00\r\n"
            + "FILE \"track3.aiff\" AIFF\r\n  TRACK 03 AUDIO\r\n    INDEX 01 00:00:00"
        );

        var (result, workDir) = await PrepareAsync(cuePath, new FakeMp3Decoder());

        try
        {
            Assert.NotNull(workDir);
            Assert.Equal(
                ["game.cue", "track01.bin", "track02.wav", "track03.aiff"],
                Directory
                    .GetFiles(workDir)
                    .Select(Path.GetFileName)
                    .OrderBy(static f => f, StringComparer.Ordinal)
                    .ToList()
            );

            var workCue = await File.ReadAllTextAsync(result.WorkCuePath!, Encoding.UTF8);
            Assert.Contains("FILE \"track01.bin\" BINARY", workCue, StringComparison.Ordinal);
            Assert.Contains("FILE \"track02.wav\" WAVE", workCue, StringComparison.Ordinal);
            Assert.Contains("FILE \"track03.aiff\" AIFF", workCue, StringComparison.Ordinal);
        }
        finally
        {
            if (workDir is not null)
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    [Fact]
    public async Task BomCueEndToEndWithRealChdman()
    {
        // End-to-end regression test for "couldn't find bin file []": a cue with a UTF-8 BOM
        // previously failed even though the bin exists, because chdman's parser does not skip
        // the BOM and therefore never sees the FILE directive. The fixed pipeline prepares a
        // BOM-free work cue referencing the bin relatively, which chdman converts.
        var chdmanPath = Path.Combine(AppContext.BaseDirectory, "chdman.exe");
        if (!File.Exists(chdmanPath)) return; // integration test — skipped when the bundled chdman is unavailable

        // Valid MODE1/2352 bin: 100 sectors with proper sync headers.
        var sector = new byte[2352];
        sector[0] = 0x00;
        for (var i = 1; i < 11; i++) sector[i] = 0xFF;

        sector[11] = 0x00;
        sector[12] = 0x01; // mode 1
        var binPath = Path.Combine(_tempDir, "track1.bin");
        await File.WriteAllBytesAsync(
            binPath,
            Enumerable.Repeat(sector, 100).SelectMany(static s => s).ToArray()
        );

        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 01 00:00:00",
            Encoding.UTF8
        ); // Encoding.UTF8 writes a BOM

        // 1) Reproduce the bug: chdman directly on the BOM'd cue fails with the empty-bin error.
        var (directExit, directOutput) = await RunChdmanAsync(
            chdmanPath,
            cuePath,
            Path.Combine(_tempDir, "direct.chd")
        );
        Assert.NotEqual(0, directExit);
        Assert.Contains(
            "couldn't find bin file []",
            directOutput,
            StringComparison.OrdinalIgnoreCase
        );

        // 2) Fixed pipeline: BOM-free work cue referencing the bin via a relative path.
        var workDir = Path.Combine(_tempDir, "work_e2e");
        Directory.CreateDirectory(workDir);
        try
        {
            var workCuePath = await CueWorkDirectory.TryWriteInPlaceWorkCueAsync(
                cuePath,
                workDir,
                CancellationToken.None
            );
            Assert.NotNull(workCuePath);

            var outputChd = Path.Combine(workDir, "game.chd");
            var (workExit, workOutput) = await RunChdmanAsync(chdmanPath, workCuePath, outputChd);
            Assert.True(workExit == 0, $"chdman failed on prepared cue: {workOutput}");
            Assert.True(File.Exists(outputChd), "expected a CHD output from the prepared cue");
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, true);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunChdmanAsync(
        string chdmanPath,
        string cuePath,
        string outputChd
    )
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = chdmanPath,
            Arguments = $"createcd -i \"{cuePath}\" -o \"{outputChd}\" -f",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout + stderr);
    }

    [Fact]
    public async Task Mp3ToWavDecoderDecodesCraftedSilenceMp3()
    {
        var mp3Path = Path.Combine(_tempDir, "silence.mp3");
        await WriteCraftedMp3Async(mp3Path);
        var wavPath = Path.Combine(_tempDir, "silence.wav");

        var decoder = new Mp3ToWavDecoder();
        await decoder.DecodeAsync(mp3Path, wavPath, null, CancellationToken.None);

        Assert.True(File.Exists(wavPath));
        Assert.True(
            new FileInfo(wavPath).Length > 44,
            "WAV file should have a RIFF header plus samples"
        );

        // chdman's cue WAVE tracks require exactly 44100 Hz stereo 16-bit PCM.
        await using var reader = new WaveFileReader(wavPath);
        Assert.Equal(44100, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
    }

    [Fact]
    public void NormalizeForChdmanResamplesAndConvertsMonoToStereo()
    {
        // Non-standard rips (mono or 22.05 kHz audio tracks) must be normalized to chdman's
        // hard requirements: exactly 44100 Hz stereo (16-bit happens at WAV write time).
        var source = new SignalGenerator(22050, 1)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = 440,
            Gain = 0.2
        };

        var normalized = Mp3ToWavDecoder.NormalizeForChdman(source);

        Assert.Equal(44100, normalized.WaveFormat.SampleRate);
        Assert.Equal(2, normalized.WaveFormat.Channels);

        // Actually pump samples through the WDL resampler + mono→stereo chain.
        // NAudio 3.x replaced the array-based ISampleProvider.Read with a Span overload.
        var buffer = new float[8192];
        var total = 0;
        while (total < 44100)
        {
            var read = normalized.Read(buffer.AsSpan(0, buffer.Length));
            if (read == 0)
                break;

            total += read;
        }

        Assert.True(total >= 44100, $"expected at least one second of samples, got {total}");
    }

    [Fact]
    public async Task Mono22050Mp3DecodesToChdmanCompatibleWav()
    {
        // Craft an MPEG-2 Layer III stream (80 kbps, 22.05 kHz, mono) filled with silence.
        // Header: 0xFF sync + 0xF3 (version bits = MPEG-2, layer bits = Layer III, no CRC)
        // + 0x90 (bitrate index 9 = 80 kbps in the MPEG-2 L3 table, samplerate index 00 =
        // 22050 Hz) + 0xC0 (mono). MPEG-2 L3 frame size = 72 * bitrate / samplerate
        // = 72 * 80000 / 22050 = 261.22 → 261 bytes with padding = 0 (MPEG-1 L3 would use
        // 144 * bitrate / samplerate). A wrong frame size loses sync after the first frame
        // and every decoder then yields zero samples.
        // Media Foundation's MP3 decoder on Windows does not support MPEG-2 streams, so this test
        // also exercises the built-in (ACM) fallback path.
        const int frameSize = 261;
        const int frameCount = 100;
        var frames = new byte[frameSize * frameCount];
        var header = new byte[] { 0xFF, 0xF3, 0x90, 0xC0 }; // MPEG-2 L3, 80kbps, 22050 Hz, mono
        for (var i = 0; i < frameCount; i++) Array.Copy(header, 0, frames, i * frameSize, header.Length);

        var mp3Path = Path.Combine(_tempDir, "mono22050.mp3");
        await File.WriteAllBytesAsync(mp3Path, frames);
        var wavPath = Path.Combine(_tempDir, "mono22050.wav");

        var decoder = new Mp3ToWavDecoder();
        await decoder.DecodeAsync(mp3Path, wavPath, null, CancellationToken.None);

        await using var reader = new WaveFileReader(wavPath);

        // The ACM decoder outputs 22050 Hz mono, so these format asserts are the proof that
        // NormalizeForChdman resampled to 44100 and upmixed to stereo — without it the WAV
        // would be rejected by chdman ("unsupported samplerate 22050 / only stereo is supported").
        Assert.Equal(44100, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.True(reader.Length > 0, "expected decoded samples");
    }

    [Fact]
    public async Task CueBinMp3EndToEndWithRealChdman()
    {
        // cue/bin/mp3: a data bin track plus MP3 audio tracks. The MP3 must be decoded to WAV
        // before chdman runs — chdman itself rejects MP3 tracks ("Unhandled track type MP3").
        var chdmanPath = Path.Combine(AppContext.BaseDirectory, "chdman.exe");
        if (!File.Exists(chdmanPath)) return; // integration test — skipped when the bundled chdman is unavailable

        var binPath = Path.Combine(_tempDir, "game.bin");
        await File.WriteAllBytesAsync(binPath, CreateMode1Bin(100));
        var mp3Path = Path.Combine(_tempDir, "track02.mp3");
        await WriteCraftedMp3Async(mp3Path);
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"game.bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 01 00:00:00\r\n"
            + "FILE \"track02.mp3\" MP3\r\n  TRACK 02 AUDIO\r\n    INDEX 01 00:00:00"
        );

        var result = await CueWorkDirectory.PrepareAsync(
            cuePath,
            "TestPrefix_",
            new Mp3ToWavDecoder(),
            null,
            CancellationToken.None
        );
        Assert.NotNull(result.WorkCuePath);
        Assert.Empty(result.UnresolvedNames);

        var workDir = result.WorkDir!;
        try
        {
            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.DoesNotContain("MP3", workCue, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("track01.bin", workCue, StringComparison.Ordinal);
            Assert.Contains("track02.wav", workCue, StringComparison.Ordinal);

            var outputChd = Path.Combine(workDir, "game.chd");
            var (exitCode, output) = await RunChdmanAsync(
                chdmanPath,
                result.WorkCuePath,
                outputChd
            );
            Assert.True(exitCode == 0, $"chdman failed on cue/bin/mp3 set: {output}");
            Assert.True(File.Exists(outputChd), "expected a CHD output");
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, true);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    [Fact]
    public async Task CueIsoMp3EndToEndWithRealChdman()
    {
        // cue/iso/mp3: an ISO data track (MODE1/2048) plus MP3 audio tracks.
        var chdmanPath = Path.Combine(AppContext.BaseDirectory, "chdman.exe");
        if (!File.Exists(chdmanPath)) return; // integration test — skipped when the bundled chdman is unavailable

        var isoPath = Path.Combine(_tempDir, "game.iso");
        await File.WriteAllBytesAsync(isoPath, CreateMode1Bin(100, 2048));
        var mp3Path = Path.Combine(_tempDir, "track02.mp3");
        await WriteCraftedMp3Async(mp3Path);
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"game.iso\" BINARY\r\n  TRACK 01 MODE1/2048\r\n    INDEX 01 00:00:00\r\n"
            + "FILE \"track02.mp3\" MP3\r\n  TRACK 02 AUDIO\r\n    INDEX 01 00:00:00"
        );

        var result = await CueWorkDirectory.PrepareAsync(
            cuePath,
            "TestPrefix_",
            new Mp3ToWavDecoder(),
            null,
            CancellationToken.None
        );
        Assert.NotNull(result.WorkCuePath);
        Assert.Empty(result.UnresolvedNames);

        var workDir = result.WorkDir!;
        try
        {
            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.Contains("track01.iso", workCue, StringComparison.Ordinal);
            Assert.Contains("track02.wav", workCue, StringComparison.Ordinal);

            var outputChd = Path.Combine(workDir, "game.chd");
            var (exitCode, output) = await RunChdmanAsync(
                chdmanPath,
                result.WorkCuePath,
                outputChd
            );
            Assert.True(exitCode == 0, $"chdman failed on cue/iso/mp3 set: {output}");
            Assert.True(File.Exists(outputChd), "expected a CHD output");
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, true);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private static async Task WriteCraftedMp3Async(string path)
    {
        // Craft a stream of MPEG-1 Layer III frames (128 kbps, 44.1 kHz, stereo) filled with silence.
        const int frameSize = 417;
        const int frameCount = 100;
        var frames = new byte[frameSize * frameCount];
        var header = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        for (var i = 0; i < frameCount; i++) Array.Copy(header, 0, frames, i * frameSize, header.Length);

        await File.WriteAllBytesAsync(path, frames);
    }

    private static byte[] CreateMode1Bin(int sectorCount, int sectorSize = 2352)
    {
        var sector = new byte[sectorSize];
        sector[0] = 0x00;
        for (var i = 1; i < 11; i++) sector[i] = 0xFF;

        sector[11] = 0x00;
        sector[12] = 0x01; // mode 1
        return Enumerable.Repeat(sector, sectorCount).SelectMany(static s => s).ToArray();
    }

    private sealed class FakeMp3Decoder : IMp3Decoder
    {
        public List<(string Mp3Path, string WavPath)> Calls { get; } = [];

        public Task DecodeAsync(
            string mp3Path,
            string wavPath,
            Action<string>? onLog,
            CancellationToken token
        )
        {
            Calls.Add((mp3Path, wavPath));
            File.WriteAllText(wavPath, "wav-content");
            return Task.CompletedTask;
        }
    }
}