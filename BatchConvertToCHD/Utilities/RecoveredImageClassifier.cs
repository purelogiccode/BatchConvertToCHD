using System.IO;
using MDSSharp;

namespace BatchConvertToCHD.Utilities;

/// <summary>
///     Works out how an image recovered into a temp directory - joined from parts, decoded from ECM,
///     decompressed from ISZ or extracted from an archive - should be handed to chdman. Raw 2352-byte
///     CD sectors are sniffed by their header; everything else is decided from the file's size:
///     2048-byte sectors convert as a DVD image, 2336 and 2324 are the Mode 2 layouts a cue can
///     describe, and 2448/2368 rips need their subchannel tail stripped before chdman will read them.
/// </summary>
internal static class RecoveredImageClassifier
{
    /// <summary>Bytes per Mode 2 (XA) sector stored without its sync and header.</summary>
    internal const int Mode2XaSectorSize = 2336;

    /// <summary>Bytes per Mode 2 Form 1 sector as some rippers store it.</summary>
    internal const int Mode2Form1SectorSize = 2324;

    /// <summary>What chdman should be handed for a recovered image.</summary>
    /// <param name="CuePath">Cue to convert as a CD, or null.</param>
    /// <param name="DvdImagePath">Image to convert as a DVD, or null.</param>
    /// <param name="SkipReason">Why nothing could be prepared, or null on success.</param>
    internal sealed record Result(string? CuePath, string? DvdImagePath, string? SkipReason)
    {
        /// <summary>True when a path to convert was produced.</summary>
        internal bool Success => SkipReason is null;

        /// <summary>A result handing chdman the cue for a CD conversion.</summary>
        /// <param name="cuePath">Path of the cue to convert.</param>
        internal static Result Cue(string cuePath)
        {
            return new Result(cuePath, null, null);
        }

        /// <summary>A result handing chdman an image for a DVD conversion.</summary>
        /// <param name="imagePath">Path of the image to convert.</param>
        internal static Result Dvd(string imagePath)
        {
            return new Result(null, imagePath, null);
        }

        /// <summary>A result reporting that the image cannot be converted.</summary>
        /// <param name="reason">User-facing reason the image is skipped.</param>
        internal static Result Skip(string reason)
        {
            return new Result(null, null, reason);
        }
    }

    /// <summary>
    ///     Classifies <paramref name="imagePath" /> and prepares whatever chdman needs.
    /// </summary>
    /// <param name="imagePath">The recovered image.</param>
    /// <param name="workDir">Directory holding it, where any cue or stripped image is written.</param>
    /// <param name="description">How to refer to the image in log messages.</param>
    /// <param name="misalignedReason">Skip reason when the size fits no known sector layout.</param>
    /// <param name="onLog">Log callback.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task<Result> ClassifyAsync(
        string imagePath,
        string workDir,
        string description,
        string misalignedReason,
        Action<string> onLog,
        CancellationToken token
    )
    {
        var trackMode = RawCdImageDetector.DetectTrackMode(imagePath);
        if (trackMode is not null)
        {
            onLog($" {description} holds raw CD sectors ({trackMode}); generating a cue for it.");

            return await WriteCueAsync(imagePath, workDir, trackMode, description, token)
                .ConfigureAwait(false);
        }

        long fileSize;
        try
        {
            fileSize = new FileInfo(imagePath).Length;
        }
        catch (Exception)
        {
            return Result.Skip(misalignedReason);
        }

        if (fileSize <= 0) return Result.Skip(misalignedReason);

        // 2048 first, so the long-standing treatment of .iso-style images is unchanged. Sizes can fit
        // several layouts at once (a 2336-byte image is 2048-aligned every 64 sectors, for example);
        // this order is the one the app has always used and is kept deliberately.
        if (fileSize % MdsDisc.CookedSectorSize == 0)
        {
            onLog(
                $" {description} holds {MdsDisc.CookedSectorSize}-byte sectors; converting it as a DVD image."
            );

            return Result.Dvd(imagePath);
        }

        if (fileSize % Mode2XaSectorSize == 0)
        {
            onLog(
                $" {description} holds {Mode2XaSectorSize}-byte Mode 2 sectors ({BinCueGenerator.Mode2Xa}); generating a cue for it."
            );

            return await WriteCueAsync(
                    imagePath,
                    workDir,
                    BinCueGenerator.Mode2Xa,
                    description,
                    token
                )
                .ConfigureAwait(false);
        }

        if (fileSize % Mode2Form1SectorSize == 0)
        {
            onLog(
                $" {description} holds {Mode2Form1SectorSize}-byte Mode 2 Form 1 sectors ({BinCueGenerator.Mode2Form1}); generating a cue for it."
            );

            return await WriteCueAsync(
                    imagePath,
                    workDir,
                    BinCueGenerator.Mode2Form1,
                    description,
                    token
                )
                .ConfigureAwait(false);
        }

        if (fileSize % MdsDisc.RawPlusSubchannelSize == 0)
        {
            return await StripAndWriteCueAsync(
                    imagePath,
                    workDir,
                    description,
                    MdsDisc.RawPlusSubchannelSize,
                    misalignedReason,
                    onLog,
                    token
                )
                .ConfigureAwait(false);
        }

        if (fileSize % MdsDisc.RawPlusShortSubchannelSize == 0)
        {
            return await StripAndWriteCueAsync(
                    imagePath,
                    workDir,
                    description,
                    MdsDisc.RawPlusShortSubchannelSize,
                    misalignedReason,
                    onLog,
                    token
                )
                .ConfigureAwait(false);
        }

        return Result.Skip(misalignedReason);
    }

    /// <summary>
    ///     Writes a single-track cue referencing <paramref name="imagePath" /> and returns it, or a
    ///     skip result when the image cannot be referenced from the work directory.
    /// </summary>
    /// <param name="imagePath">The image the cue describes.</param>
    /// <param name="workDir">Directory the cue is written into.</param>
    /// <param name="trackMode">Cue track mode to declare, e.g. "MODE2/2336".</param>
    /// <param name="description">How to refer to the image in skip messages.</param>
    /// <param name="token">Cancellation token.</param>
    private static async Task<Result> WriteCueAsync(
        string imagePath,
        string workDir,
        string trackMode,
        string description,
        CancellationToken token
    )
    {
        var cuePath = await RawCdImageDetector
            .TryWriteCueAsync(imagePath, trackMode, workDir, token)
            .ConfigureAwait(false);

        return cuePath is not null
            ? Result.Cue(cuePath)
            : Result.Skip($"could not write a cue for the {description.ToLowerInvariant()}.");
    }

    /// <summary>
    ///     Strips the subchannel tail from a 2448/2368-byte image, sniffs the resulting raw CD layout
    ///     and returns a cue for it.
    /// </summary>
    /// <param name="imagePath">The image carrying subchannel data.</param>
    /// <param name="workDir">Directory holding it, where the stripped image and cue are written.</param>
    /// <param name="description">How to refer to the image in log messages.</param>
    /// <param name="sectorSize">Bytes per sector in the source image.</param>
    /// <param name="misalignedReason">Skip reason when the stripped image has no readable layout.</param>
    /// <param name="onLog">Log callback.</param>
    /// <param name="token">Cancellation token.</param>
    private static async Task<Result> StripAndWriteCueAsync(
        string imagePath,
        string workDir,
        string description,
        int sectorSize,
        string misalignedReason,
        Action<string> onLog,
        CancellationToken token
    )
    {
        var strippedPath = Path.Combine(
            workDir,
            Path.GetFileNameWithoutExtension(imagePath) + ".stripped" + FileExtensions.Bin
        );
        onLog(
            $" {description} stores {sectorSize}-byte sectors with subchannel data; stripping the tail so chdman can read it."
        );

        var failure = await MdsInputPreparer
            .StripSubchannelAsync(imagePath, strippedPath, sectorSize, token)
            .ConfigureAwait(false);
        if (failure is not null) return Result.Skip(failure);

        var trackMode = RawCdImageDetector.DetectTrackMode(strippedPath);
        if (trackMode is null) return Result.Skip(misalignedReason);

        onLog($" Stripped image holds raw CD sectors ({trackMode}); generating a cue for it.");

        return await WriteCueAsync(strippedPath, workDir, trackMode, "Stripped image", token)
            .ConfigureAwait(false);
    }
}
