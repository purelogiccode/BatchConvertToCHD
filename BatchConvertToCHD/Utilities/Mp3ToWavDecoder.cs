using System.Diagnostics;
#if WINDOWS
using System.Runtime.Versioning;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
#endif
using BatchConvertToCHD.Interfaces;

namespace BatchConvertToCHD.Utilities;

/// <summary>
///     MP3 → WAV decoder. chdman cannot read MP3 audio tracks in cue sheets ("Unhandled track type
///     MP3"), and its WAVE track support requires exactly 44100 Hz, stereo, 16-bit PCM — so MP3
///     tracks are decoded to a chdman-compatible WAV before conversion. On Windows decoding is
///     backed by Windows Media Foundation (NAudio.MediaFoundationReader) with a fallback to
///     NAudio's Mp3FileReader (ACM codec) for systems without Media Foundation (e.g. Windows N
///     editions). On Linux and macOS an ffmpeg found on PATH performs the decode instead.
/// </summary>
internal sealed class Mp3ToWavDecoder : IMp3Decoder
{
#if WINDOWS
    private static readonly Lock MediaFoundationLock = new();
#endif

    /// <inheritdoc />
    /// <param name="mp3Path">Path of the MP3 file to decode.</param>
    /// <param name="wavPath">Destination path for the decoded 44100 Hz stereo 16-bit PCM WAV file.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    public Task DecodeAsync(
        string mp3Path,
        string wavPath,
        Action<string>? onLog,
        CancellationToken token
    )
    {
        return Task.Run(
            () =>
            {
                token.ThrowIfCancellationRequested();
                onLog?.Invoke(
                    $"MP3: Decoding {Path.GetFileName(mp3Path)} to WAV (required for chdman)..."
                );

                Exception? primaryError;

#if WINDOWS
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        DecodeWithMediaFoundation(mp3Path, wavPath);

                        // Some inputs (notably crafted MPEG-2 Layer III files under recent NAudio/Media
                        // Foundation combinations) OPEN fine yet yield no samples at all. An empty audio
                        // payload means this path did not really succeed, so fall through to the built-in
                        // decoder instead of writing a header-only WAV.
                        if (WavHasAudioData(wavPath))
                        {
                            token.ThrowIfCancellationRequested();
                            return;
                        }

                        primaryError = new InvalidDataException(
                            "Media Foundation produced no audio samples for this file."
                        );
                        onLog?.Invoke(
                            "MP3: Media Foundation decoding yielded no audio; falling back to the built-in MP3 decoder..."
                        );
                    }
                    catch (Exception mfEx)
                    {
                        token.ThrowIfCancellationRequested();

                        primaryError = mfEx;

                        // Media Foundation is unavailable (Windows N / Server Core) or the codec is
                        // missing — fall back to NAudio's Mp3FileReader (ACM codec).
                        onLog?.Invoke(
                            $"MP3: Media Foundation decoding failed ({mfEx.Message}); falling back to the built-in MP3 decoder..."
                        );
                    }
                }
                else
#endif
                {
                    // Media Foundation is a Windows-only decoder; on Linux and macOS decoding is
                    // delegated to ffmpeg, which ships with every mainstream distribution.
                    primaryError = new PlatformNotSupportedException(
                        "Media Foundation is only available on Windows."
                    );
                    onLog?.Invoke(
                        "MP3: decoding with ffmpeg (Media Foundation is Windows-only)..."
                    );
                }

                try
                {
#if WINDOWS
                    if (OperatingSystem.IsWindows())
                    {
                        DecodeWithBuiltInDecoder(mp3Path, wavPath);
                    }
                    else
#endif
                    {
                        DecodeWithFfmpeg(mp3Path, wavPath);
                    }

                    if (WavHasAudioData(wavPath))
                    {
                        token.ThrowIfCancellationRequested();
                        return;
                    }

                    throw new InvalidDataException(
                        "the fallback decoder also produced no audio samples - the file may be empty or use an unsupported format."
                    );
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception builtInEx)
                {
                    token.ThrowIfCancellationRequested();

                    // Chain the original Media Foundation error so the root cause (missing codec vs
                    // corrupt MP3) stays diagnosable.
                    throw new InvalidDataException(
                        $"Failed to decode MP3 '{Path.GetFileName(mp3Path)}' with Media Foundation ({primaryError.Message}) and the built-in decoder ({builtInEx.Message}).",
                        primaryError ?? builtInEx
                    );
                }
            },
            token
        );
    }

    /// <summary>
    ///     True when the WAV at <paramref name="wavPath" /> carries an actual audio payload rather
    ///     than just a format header.
    /// </summary>
    private static bool WavHasAudioData(string wavPath)
    {
#if WINDOWS
        try
        {
            using var reader = new WaveFileReader(wavPath);
            return reader.Length > 0;
        }
        catch
        {
            return false;
        }
#else
        try
        {
            // A bare 44-byte PCM header means no audio was decoded.
            var info = new FileInfo(wavPath);
            return info.Exists && info.Length > 44;
        }
        catch
        {
            return false;
        }
#endif
    }

#if WINDOWS
    /// <summary>
    ///     Decodes with Windows Media Foundation. Serialized because NAudio's startup flag is not
    ///     thread-safe.
    /// </summary>
    /// <param name="mp3Path">Path of the MP3 file to decode.</param>
    /// <param name="wavPath">Destination path for the decoded WAV file.</param>
    [SupportedOSPlatform("windows")]
    private static void DecodeWithMediaFoundation(string mp3Path, string wavPath)
    {
        // Media Foundation Startup/Shutdown flips a static flag in NAudio without locking,
        // so concurrent decodes (parallel conversions) must be serialized.
        lock (MediaFoundationLock)
        {
            MediaFoundationApi.Startup();
            try
            {
                using var reader = new MediaFoundationReader(mp3Path);
                WriteChdmanCompatibleWav(reader.ToSampleProvider(), wavPath);
            }
            finally
            {
                MediaFoundationApi.Shutdown();
            }
        }
    }

    /// <summary>
    ///     Decodes with NAudio's ACM-backed MP3 reader; the fallback when Media Foundation is not
    ///     available (Windows N editions).
    /// </summary>
    /// <param name="mp3Path">Path of the MP3 file to decode.</param>
    /// <param name="wavPath">Destination path for the decoded WAV file.</param>
    [SupportedOSPlatform("windows")]
    private static void DecodeWithBuiltInDecoder(string mp3Path, string wavPath)
    {
        using var reader = new Mp3FileReader(mp3Path);
        WriteChdmanCompatibleWav(reader.ToSampleProvider(), wavPath);
    }
#endif

    /// <summary>
    ///     Decodes the MP3 with an external ffmpeg executable found on PATH. Used on Linux and
    ///     macOS, where Media Foundation and NAudio's ACM-backed MP3 reader are unavailable.
    /// </summary>
    /// <param name="mp3Path">Path of the MP3 file to decode.</param>
    /// <param name="wavPath">Destination path for the decoded 44100 Hz stereo 16-bit PCM WAV file.</param>
    private static void DecodeWithFfmpeg(string mp3Path, string wavPath)
    {
        var ffmpegPath =
            FindExecutableOnPath("ffmpeg")
            ?? throw new InvalidOperationException(
                "ffmpeg was not found on PATH; install it to decode MP3 tracks on this platform."
            );

        using var process = Process.Start(
            new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-y",
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-i",
                    mp3Path,
                    "-ar",
                    "44100",
                    "-ac",
                    "2",
                    "-sample_fmt",
                    "s16",
                    wavPath
                }
            }
        ) ?? throw new InvalidOperationException("Failed to start ffmpeg.");

        using (process)
        {
            var errorOutput = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"ffmpeg failed with exit code {process.ExitCode}: {errorOutput}"
                );
            }
        }
    }

    /// <summary>
    ///     Returns the full path of the first executable named <paramref name="executableName" />
    ///     found in the <c>PATH</c> environment variable, or null when it is not present.
    /// </summary>
    /// <param name="executableName">The executable file name to locate (without extension on Windows).</param>
    private static string? FindExecutableOnPath(string executableName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable)) return null;

        foreach (
            var directory in pathVariable.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, executableName);
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries
                continue;
            }

            if (File.Exists(candidate)) return candidate;

            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
                return candidate + ".exe";
        }

        return null;
    }

#if WINDOWS
    /// <summary>
    ///     Normalizes any PCM sample stream into what chdman's cue WAVE tracks require:
    ///     exactly 44100 Hz, stereo, 16-bit (the 16-bit conversion happens at write time via
    ///     <see cref="WaveFileWriter.CreateWaveFile16" />).
    /// </summary>
    /// <remarks>
    ///     The WDL resampler buffers ~100 ms of audio at the higher of the input/output rates, so
    ///     it stays comfortably within memory for all common MP3 sample rates (8–48 kHz).
    /// </remarks>
    internal static ISampleProvider NormalizeForChdman(ISampleProvider source)
    {
        var sample = source;
        if (sample.WaveFormat.SampleRate != 44100) sample = new WdlResamplingSampleProvider(sample, 44100);

        if (sample.WaveFormat.Channels == 1) sample = new MonoToStereoSampleProvider(sample);

        return sample;
    }

    /// <summary>
    ///     Writes the sample provider as the 44100 Hz stereo 16-bit PCM WAV chdman requires.
    /// </summary>
    /// <param name="source">The decoded sample provider.</param>
    /// <param name="wavPath">Destination path for the WAV file.</param>
    private static void WriteChdmanCompatibleWav(ISampleProvider source, string wavPath)
    {
        // Force 16-bit PCM output — some Media Foundation codecs produce IEEE float,
        // which chdman cannot consume in cue WAVE tracks.
        WaveFileWriter.CreateWaveFile16(wavPath, NormalizeForChdman(source));
    }
#endif
}