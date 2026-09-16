using System.Globalization;
using System.Text;

namespace MDSSharp;

/// <summary>
///     Turns an Alcohol .mds/.mdf pair into something chdman can actually convert.
///     The medium type and track table decide the shape:
///     - DVD media: the data file is a cooked image, converted directly with no cue.
///     - CD media with 2048-byte sectors: a cooked CD, described by a MODE1/2048 cue.
///     - CD media with 2352-byte sectors: a cue is all that is missing, and the data file is referenced where it lies.
///     - CD media with 2448/2368-byte sectors (raw data plus subchannel): chdman will not read these,
///       so the subchannel tail is stripped into a new image and a cue is written for that.
///     When the descriptor records pregaps that the data file does not contain, the pregap sectors are
///     rebuilt as zeros so a cue can express INDEX 00 without shifting the tracks that follow.
///     Descriptors that name several data files have those files joined first.
/// </summary>
public static class MdsInputPreparer
{
    /// <summary>Sectors repacked per read. A write per sector is far too slow at disc scale.</summary>
    private const int StripChunkSectors = 2048;

    private const string BinExtension = ".bin";
    private const string CueExtension = ".cue";
    private const string PregapBinExtension = ".pregap.bin";

    /// <summary>
    ///     Prepares <paramref name="disc" /> for conversion, writing any generated files into
    ///     <paramref name="workDir" />.
    /// </summary>
    /// <param name="disc">The parsed Alcohol image.</param>
    /// <param name="workDir">Existing directory for generated files.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<Result> PrepareAsync(
        MdsDisc disc,
        string workDir,
        Action<string>? onLog,
        CancellationToken token
    )
    {
        if (disc.Tracks.Count == 0)
            return Result.Failed("the descriptor contains no readable tracks.");

        var dataFiles = disc.DataFilePaths.Count > 0
            ? disc.DataFilePaths
            : disc.MdfPath is not null
                ? [disc.MdfPath]
                : [];

        if (dataFiles.Count == 0 || dataFiles.Any(static f => !File.Exists(f)))
            return Result.Failed("the .mdf data file was not found next to the .mds descriptor");

        string dataFilePath;
        try
        {
            dataFilePath = await JoinDataFilesAsync(disc, workDir, dataFiles, onLog, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failed($"the data files could not be joined: {ex.Message}");
        }

        if (disc.IsDvdImage)
        {
            onLog?.Invoke(
                $" {Path.GetFileName(disc.MdsPath)} declares DVD media; converting the data file directly."
            );
            return Result.Dvd(dataFilePath);
        }

        if (!disc.AllTracksDescribable)
        {
            var unknown = disc
                .Tracks.Where(static t => t.CueTrackType is null)
                .Select(static t => t.Description);
            return Result.Failed(
                $"the descriptor uses track modes this build cannot express in a cue ({string.Join(", ", unknown)})"
            );
        }

        if (disc.IsCookedCd)
        {
            onLog?.Invoke(
                $" {Path.GetFileName(disc.MdsPath)} describes {MdsDisc.CookedSectorSize}-byte CD sectors; writing a MODE1/2048 cue."
            );
            var cookedReference = await ReferenceOrCopyAsync(dataFilePath, workDir, onLog, token)
                .ConfigureAwait(false);
            var cookedCue = await WriteCueAsync(disc, workDir, cookedReference, token)
                .ConfigureAwait(false);
            return Result.Cue(cookedCue);
        }

        if (
            disc.SectorSize
            is not (
                MdsDisc.RawSectorSize
                or MdsDisc.RawPlusSubchannelSize
                or MdsDisc.RawPlusShortSubchannelSize
                or MdsDisc.Mode2XaSectorSize
            )
        )
        {
            return Result.Failed(
                $"the descriptor reports {disc.SectorSize} bytes per sector, which is neither a raw CD ({MdsDisc.RawSectorSize}), a subchannel-bearing CD ({MdsDisc.RawPlusSubchannelSize}), a Mode 2 cooked CD ({MdsDisc.Mode2XaSectorSize}) nor a DVD image ({MdsDisc.CookedSectorSize})"
            );
        }

        var pregapPlan = ClassifyPregapPlan(disc, dataFilePath);

        if (pregapPlan == PregapPlan.Padding)
        {
            var paddedPath = Path.Combine(
                workDir,
                Path.GetFileNameWithoutExtension(disc.MdsPath) + PregapBinExtension
            );
            var pregapSectors = disc.Tracks.Sum(static t => t.PregapSectors);
            var strip = disc.NeedsSubchannelStrip;
            onLog?.Invoke(
                $" {Path.GetFileName(disc.MdsPath)} records {pregapSectors:N0} pregap sectors the data file does not contain; rebuilding the image with zero-filled pregaps{(strip ? " and stripped subchannel data" : string.Empty)}."
            );

            var padded = await WritePregapPaddedImageAsync(
                    dataFilePath,
                    paddedPath,
                    disc,
                    strip,
                    token
                )
                .ConfigureAwait(false);
            if (padded is not null) return Result.Failed(padded);

            var paddedCue = await WriteCueAsync(
                    disc,
                    workDir,
                    Path.GetFileName(paddedPath),
                    token,
                    pregapsInFile: true
                )
                .ConfigureAwait(false);
            return Result.Cue(paddedCue);
        }

        var pregapsInFile = pregapPlan == PregapPlan.PregapsInFile;
        if (pregapsInFile)
        {
            onLog?.Invoke(
                $" {Path.GetFileName(disc.MdsPath)} records pregap sectors that are present in the data file; writing INDEX 00 for them."
            );
        }

        if (disc.NeedsSubchannelStrip)
        {
            var strippedPath = Path.Combine(
                workDir,
                Path.GetFileNameWithoutExtension(dataFilePath) + ".stripped" + BinExtension
            );
            onLog?.Invoke(
                $" {Path.GetFileName(dataFilePath)} stores {disc.SectorSize}-byte sectors; stripping subchannel data down to {MdsDisc.RawSectorSize} bytes so chdman can read it."
            );

            var stripped = await StripSubchannelAsync(
                    dataFilePath,
                    strippedPath,
                    disc.SectorSize,
                    token
                )
                .ConfigureAwait(false);
            if (stripped is not null) return Result.Failed(stripped);

            var cuePath = await WriteCueAsync(
                    disc,
                    workDir,
                    Path.GetFileName(strippedPath),
                    token,
                    pregapsInFile
                )
                .ConfigureAwait(false);
            return Result.Cue(cuePath);
        }

        var reference = await ReferenceOrCopyAsync(dataFilePath, workDir, onLog, token)
            .ConfigureAwait(false);
        var plainCuePath = await WriteCueAsync(disc, workDir, reference, token, pregapsInFile)
            .ConfigureAwait(false);
        return Result.Cue(plainCuePath);
    }

    /// <summary>
    ///     Joins the descriptor's data files when there is more than one, or the first volume of a
    ///     ".i00"/".001" split set; otherwise returns the single file it was given.
    /// </summary>
    /// <param name="disc">The parsed Alcohol image.</param>
    /// <param name="workDir">Directory for the joined file.</param>
    /// <param name="dataFiles">Resolved data files, in track order.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    private static async Task<string> JoinDataFilesAsync(
        MdsDisc disc,
        string workDir,
        IReadOnlyList<string> dataFiles,
        Action<string>? onLog,
        CancellationToken token
    )
    {
        if (dataFiles.Count > 1)
        {
            onLog?.Invoke(
                $" {Path.GetFileName(disc.MdsPath)} names {dataFiles.Count.ToString(CultureInfo.InvariantCulture)} data files; joining them in order."
            );
            var joinedPath = Path.Combine(
                workDir,
                Path.GetFileNameWithoutExtension(disc.MdsPath) + BinExtension
            );
            await SplitImageJoiner
                .JoinAsync(dataFiles, joinedPath, token)
                .ConfigureAwait(false);
            return joinedPath;
        }

        var volumeSet = SplitImageJoiner.TryGetVolumeSet(dataFiles[0]);
        if (volumeSet is null) return dataFiles[0];

        onLog?.Invoke(
            $" {Path.GetFileName(dataFiles[0])} is part 1 of a {volumeSet.Count}-part split image; joining the parts."
        );
        var volumePath = Path.Combine(
            workDir,
            Path.GetFileNameWithoutExtension(disc.MdsPath) + BinExtension
        );
        await SplitImageJoiner.JoinAsync(volumeSet, volumePath, token).ConfigureAwait(false);
        return volumePath;
    }

    /// <summary>
    ///     Works out whether the data file already carries the descriptor's pregaps. Only a
    ///     single-session CD whose track lengths add up to the file size, and whose start LBAs line
    ///     up with the sequential layout, is classified; anything else keeps the old behaviour of
    ///     ignoring the pregap fields.
    /// </summary>
    /// <param name="disc">The parsed Alcohol image.</param>
    /// <param name="dataFilePath">Resolved data file.</param>
    private static PregapPlan ClassifyPregapPlan(MdsDisc disc, string dataFilePath)
    {
        if (!disc.HasPregapInfo || disc.SessionCount > 1) return PregapPlan.None;

        var sectorSize = disc.SectorSize;
        if (sectorSize <= 0) return PregapPlan.None;

        long totalBytes;
        try
        {
            totalBytes = new FileInfo(dataFilePath).Length;
        }
        catch (Exception)
        {
            return PregapPlan.None;
        }

        if (totalBytes <= 0 || totalBytes % sectorSize != 0) return PregapPlan.None;

        // The descriptor's start LBAs must line up with the sequential layout, or a cue's INDEX
        // values would point somewhere other than where the data actually sits.
        long running = 0;
        foreach (var track in disc.Tracks)
        {
            running += track.PregapSectors;
            if (running != track.StartLba) return PregapPlan.None;

            running += track.LengthSectors;
        }

        var fileSectors = totalBytes / sectorSize;
        var dataSectors = disc.Tracks.Sum(static t => t.LengthSectors);
        var pregapSectors = disc.Tracks.Sum(static t => t.PregapSectors);

        if (fileSectors == dataSectors + pregapSectors) return PregapPlan.PregapsInFile;

        return fileSectors == dataSectors ? PregapPlan.Padding : PregapPlan.None;
    }

    /// <summary>
    ///     Rebuilds the image with the descriptor's pregap sectors inserted as zeros, keeping the
    ///     first <see cref="MdsDisc.RawSectorSize" /> bytes of every sector when the source carries
    ///     subchannel data. Returns null on success or a user-facing reason on failure.
    /// </summary>
    /// <param name="sourcePath">The .mdf holding the track data.</param>
    /// <param name="destinationPath">Where the rebuilt image is written.</param>
    /// <param name="disc">The parsed Alcohol image.</param>
    /// <param name="stripSubchannel">True to drop the subchannel tail of every sector.</param>
    /// <param name="token">Cancellation token.</param>
    private static async Task<string?> WritePregapPaddedImageAsync(
        string sourcePath,
        string destinationPath,
        MdsDisc disc,
        bool stripSubchannel,
        CancellationToken token
    )
    {
        var sourceSectorSize = disc.SectorSize;
        var outputSectorSize = stripSubchannel ? MdsDisc.RawSectorSize : sourceSectorSize;
        var expectedSourceBytes =
            disc.Tracks.Sum(static t => t.LengthSectors) * sourceSectorSize;

        long sourceLength;
        try
        {
            sourceLength = new FileInfo(sourcePath).Length;
        }
        catch (Exception ex)
        {
            return $"{Path.GetFileName(sourcePath)} could not be read: {ex.Message}";
        }

        if (sourceLength != expectedSourceBytes)
        {
            return
                $"{Path.GetFileName(sourcePath)} is {sourceLength:N0} bytes but the descriptor's track lengths describe {expectedSourceBytes:N0}, so the file is truncated or the descriptor is wrong";
        }

        var readBuffer = new byte[sourceSectorSize * StripChunkSectors];
        var writeBuffer = new byte[outputSectorSize * StripChunkSectors];
        var pregapBuffer = new byte[outputSectorSize * StripChunkSectors];

        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            readBuffer.Length,
            true
        );
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            writeBuffer.Length,
            true
        );

        foreach (var track in disc.Tracks)
        {
            token.ThrowIfCancellationRequested();

            var pregapBytes = track.PregapSectors * outputSectorSize;
            while (pregapBytes > 0)
            {
                var step = (int)Math.Min(pregapBytes, pregapBuffer.Length);
                await output
                    .WriteAsync(pregapBuffer.AsMemory(0, step), token)
                    .ConfigureAwait(false);
                pregapBytes -= step;
            }

            var remaining = track.LengthSectors;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();

                var sectors = (int)Math.Min(remaining, StripChunkSectors);
                var wantBytes = sectors * sourceSectorSize;
                var read = await input
                    .ReadAtLeastAsync(readBuffer.AsMemory(0, wantBytes), wantBytes, false, token)
                    .ConfigureAwait(false);
                if (read < wantBytes)
                {
                    return
                        $"{Path.GetFileName(sourcePath)} is shorter than the descriptor's track lengths say, so it is truncated";
                }

                if (stripSubchannel)
                {
                    for (var sector = 0; sector < sectors; sector++)
                    {
                        readBuffer
                            .AsSpan(sector * sourceSectorSize, outputSectorSize)
                            .CopyTo(writeBuffer.AsSpan(sector * outputSectorSize));
                    }

                    await output
                        .WriteAsync(writeBuffer.AsMemory(0, sectors * outputSectorSize), token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await output.WriteAsync(readBuffer.AsMemory(0, read), token).ConfigureAwait(false);
                }

                remaining -= sectors;
            }
        }

        await output.FlushAsync(token).ConfigureAwait(false);

        return null;
    }

    /// <summary>
    ///     Copies <paramref name="sourcePath" /> to <paramref name="destinationPath" /> keeping only the
    ///     first <see cref="MdsDisc.RawSectorSize" /> bytes of every sector. Returns null on success or a
    ///     user-facing reason on failure.
    /// </summary>
    /// <param name="sourcePath">The .mdf holding oversized sectors.</param>
    /// <param name="destinationPath">Where the 2352-byte image is written.</param>
    /// <param name="sectorSize">Bytes per sector in the source.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<string?> StripSubchannelAsync(
        string sourcePath,
        string destinationPath,
        int sectorSize,
        CancellationToken token
    )
    {
        if (sectorSize <= MdsDisc.RawSectorSize) return $"sector size {sectorSize} carries no subchannel data to strip";

        var length = new FileInfo(sourcePath).Length;
        if (length == 0 || length % sectorSize != 0)
        {
            return
                $"{Path.GetFileName(sourcePath)} is {length:N0} bytes, which is not a whole number of {sectorSize}-byte sectors, so it is truncated or the descriptor is wrong";
        }

        var readBuffer = new byte[sectorSize * StripChunkSectors];
        var writeBuffer = new byte[MdsDisc.RawSectorSize * StripChunkSectors];

        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            readBuffer.Length,
            true
        );
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            writeBuffer.Length,
            true
        );

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var read = await input
                .ReadAtLeastAsync(readBuffer, readBuffer.Length, false, token)
                .ConfigureAwait(false);
            if (read == 0) break;

            var sectors = read / sectorSize;
            for (var sector = 0; sector < sectors; sector++)
            {
                readBuffer
                    .AsSpan(sector * sectorSize, MdsDisc.RawSectorSize)
                    .CopyTo(writeBuffer.AsSpan(sector * MdsDisc.RawSectorSize));
            }

            await output
                .WriteAsync(writeBuffer.AsMemory(0, sectors * MdsDisc.RawSectorSize), token)
                .ConfigureAwait(false);

            if (read < readBuffer.Length) break;
        }

        return null;
    }

    /// <summary>
    ///     Writes a single-FILE cue describing every track, and returns its path.
    /// </summary>
    /// <param name="disc">The parsed Alcohol image.</param>
    /// <param name="workDir">Directory the cue is written into.</param>
    /// <param name="dataFileReference">FILE entry to record, relative to <paramref name="workDir" />.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="pregapsInFile">
    ///     True when the referenced data file contains the descriptor's pregap sectors, so INDEX 00
    ///     can be written for them.
    /// </param>
    public static async Task<string> WriteCueAsync(
        MdsDisc disc,
        string workDir,
        string dataFileReference,
        CancellationToken token,
        bool pregapsInFile = false
    )
    {
        var builder = new StringBuilder();
        builder.Append("FILE \"").Append(dataFileReference).Append("\" BINARY\r\n");

        foreach (var track in disc.Tracks)
        {
            builder
                .Append("  TRACK ")
                .Append(track.Number.ToString("00", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(track.CueTrackType)
                .Append("\r\n");

            if (pregapsInFile && track.PregapSectors > 0)
            {
                var pregapLba = track.StartLba - track.PregapSectors;
                if (pregapLba < 0) pregapLba = 0;

                builder.Append("    INDEX 00 ").Append(FormatMsf(pregapLba)).Append("\r\n");
            }

            // INDEX 01 at the recorded LBA. Pregaps are only written when the data file actually
            // carries them; otherwise an INDEX 00 would read the wrong sectors.
            builder.Append("    INDEX 01 ").Append(FormatMsf(track.StartLba)).Append("\r\n");
        }

        var cuePath = Path.Combine(
            workDir,
            Path.GetFileNameWithoutExtension(disc.MdsPath) + CueExtension
        );

        // No BOM: chdman's cue parser does not skip one.
        await File.WriteAllTextAsync(cuePath, builder.ToString(), new UTF8Encoding(false), token)
            .ConfigureAwait(false);

        return cuePath;
    }

    /// <summary>Formats an absolute sector count as the MM:SS:FF a cue INDEX expects.</summary>
    /// <param name="lba">Absolute sector number.</param>
    public static string FormatMsf(long lba)
    {
        const int framesPerSecond = 75;
        const int framesPerMinute = framesPerSecond * 60;

        if (lba < 0) lba = 0;

        var minutes = lba / framesPerMinute;
        var remainder = lba % framesPerMinute;
        var seconds = remainder / framesPerSecond;
        var frames = remainder % framesPerSecond;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minutes:00}:{seconds:00}:{frames:00}"
        );
    }

    /// <summary>
    ///     Returns the file name to record in the cue: the file itself when it can be referenced
    ///     relatively from the work directory, otherwise a copy placed in the work directory.
    /// </summary>
    /// <param name="dataFilePath">The data file.</param>
    /// <param name="workDir">Directory for generated files.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    private static async Task<string> ReferenceOrCopyAsync(
        string dataFilePath,
        string workDir,
        Action<string>? onLog,
        CancellationToken token
    )
    {
        var reference = GetReferencePath(workDir, dataFilePath);
        if (reference is not null) return reference;

        reference = Path.GetFileName(dataFilePath);
        onLog?.Invoke(
            $" {Path.GetFileName(dataFilePath)} cannot be referenced relatively from the work directory; copying it."
        );
        await CopyAsync(dataFilePath, Path.Combine(workDir, reference), token)
            .ConfigureAwait(false);
        return reference;
    }

    private static string? GetReferencePath(string workDir, string dataFilePath)
    {
        try
        {
            var relative = Path.GetRelativePath(workDir, dataFilePath);
            return Path.IsPathRooted(relative) ? null : relative;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken token
    )
    {
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            true
        );
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            true
        );
        await input.CopyToAsync(output, token).ConfigureAwait(false);
    }

    /// <summary>How the descriptor's pregap fields line up with the data file.</summary>
    private enum PregapPlan
    {
        /// <summary>No usable pregap information; keep the descriptor's fields out of the cue.</summary>
        None,

        /// <summary>The data file already contains the pregap sectors, so INDEX 00 can point at them.</summary>
        PregapsInFile,

        /// <summary>The data file lacks the pregap sectors; they have to be rebuilt as zeros.</summary>
        Padding
    }

    /// <summary>What chdman should be handed for an Alcohol image.</summary>
    /// <param name="CuePath">Cue to convert as a CD, or null.</param>
    /// <param name="DvdImagePath">Image to convert as a DVD, or null.</param>
    /// <param name="FailureReason">Why nothing could be prepared, or null on success.</param>
    public sealed record Result(string? CuePath, string? DvdImagePath, string? FailureReason)
    {
        /// <summary>True when nothing failed and one of the paths is set.</summary>
        public bool Success => FailureReason is null;

        /// <summary>A result carrying a cue to convert as a CD.</summary>
        /// <param name="cuePath">Path of the generated cue.</param>
        public static Result Cue(string cuePath)
        {
            return new Result(cuePath, null, null);
        }

        /// <summary>A result carrying a cooked image to convert as a DVD.</summary>
        /// <param name="imagePath">Path of the data file.</param>
        public static Result Dvd(string imagePath)
        {
            return new Result(null, imagePath, null);
        }

        /// <summary>A result carrying why nothing could be prepared.</summary>
        /// <param name="reason">User-facing failure reason.</param>
        public static Result Failed(string reason)
        {
            return new Result(null, null, reason);
        }
    }
}
