using System.IO;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// MP3 → WAV decoder. chdman cannot read MP3 audio tracks in cue sheets ("Unhandled track type
/// MP3"), and its WAVE track support requires exactly 44100 Hz, stereo, 16-bit PCM — so MP3
/// tracks are decoded to a chdman-compatible WAV before conversion. Decoding is backed by
/// Windows Media Foundation (NAudio.MediaFoundationReader) with a fallback to NAudio's
/// Mp3FileReader (ACM codec) for systems without Media Foundation (e.g. Windows N editions).
/// </summary>
internal sealed class Mp3ToWavDecoder : IMp3Decoder
{
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
                    if (token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(token);
                    }

                    primaryError = mfEx;

                    // Media Foundation is unavailable (Windows N / Server Core) or the codec is
                    // missing — fall back to NAudio's Mp3FileReader (ACM codec).
                    onLog?.Invoke(
                        $"MP3: Media Foundation decoding failed ({mfEx.Message}); falling back to the built-in MP3 decoder..."
                    );
                }

                try
                {
                    DecodeWithBuiltInDecoder(mp3Path, wavPath);
                    if (WavHasAudioData(wavPath))
                    {
                        token.ThrowIfCancellationRequested();
                        return;
                    }

                    throw new InvalidDataException(
                        "the built-in decoder also produced no audio samples - the file may be empty or use an unsupported format."
                    );
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception builtInEx)
                {
                    if (token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(token);
                    }

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
    /// True when the WAV at <paramref name="wavPath"/> carries an actual audio payload rather
    /// than just a format header.
    /// </summary>
    private static bool WavHasAudioData(string wavPath)
    {
        try
        {
            using var reader = new WaveFileReader(wavPath);
            return reader.Length > 0;
        }
        catch
        {
            return false;
        }
    }

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

    private static void DecodeWithBuiltInDecoder(string mp3Path, string wavPath)
    {
        using var reader = new Mp3FileReader(mp3Path);
        WriteChdmanCompatibleWav(reader.ToSampleProvider(), wavPath);
    }

    /// <summary>
    /// Normalizes any PCM sample stream into what chdman's cue WAVE tracks require:
    /// exactly 44100 Hz, stereo, 16-bit (the 16-bit conversion happens at write time via
    /// <see cref="WaveFileWriter.CreateWaveFile16"/>).
    /// </summary>
    /// <remarks>
    /// The WDL resampler buffers ~100 ms of audio at the higher of the input/output rates, so
    /// it stays comfortably within memory for all common MP3 sample rates (8–48 kHz).
    /// </remarks>
    internal static ISampleProvider NormalizeForChdman(ISampleProvider source)
    {
        var sample = source;
        if (sample.WaveFormat.SampleRate != 44100)
        {
            sample = new WdlResamplingSampleProvider(sample, 44100);
        }

        if (sample.WaveFormat.Channels == 1)
        {
            sample = new MonoToStereoSampleProvider(sample);
        }

        return sample;
    }

    private static void WriteChdmanCompatibleWav(ISampleProvider source, string wavPath)
    {
        // Force 16-bit PCM output — some Media Foundation codecs produce IEEE float,
        // which chdman cannot consume in cue WAVE tracks.
        WaveFileWriter.CreateWaveFile16(wavPath, NormalizeForChdman(source));
    }

    private static readonly Lock MediaFoundationLock = new();
}