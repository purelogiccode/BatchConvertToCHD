using CCDSharp.Models;
using CCDSharp.Parsers;
using CCDSharp.Writers;

namespace CCDSharp;

/// <summary>
///     Main entry point for parsing and converting CloneCD disc images.
///     Provides a simple facade over the CCDSharp library's parsing and writing capabilities.
/// </summary>
public static class CcdConverter
{
    /// <summary>
    ///     Parses a CloneCD .ccd file into a DiscImage model.
    /// </summary>
    /// <param name="ccdFilePath">Path to the .ccd file.</param>
    /// <returns>The parsed disc image model.</returns>
    /// <exception cref="FileNotFoundException">If the .ccd file does not exist.</exception>
    /// <exception cref="FormatException">If the .ccd file is malformed.</exception>
    public static DiscImage Parse(string ccdFilePath)
    {
        return CcdParser.Parse(ccdFilePath);
    }

    /// <summary>
    ///     Converts a CloneCD image (.ccd/.img) to a CUE/BIN pair.
    ///     The .img file is copied as the .bin data file, and a .cue sheet is generated.
    /// </summary>
    /// <param name="ccdFilePath">Path to the .ccd file.</param>
    /// <param name="outputCuePath">Path for the output .cue file.</param>
    /// <param name="copyBinFile">If true, copies the .img to a .bin file. If false, references the .img directly.</param>
    /// <returns>The path to the .cue file created.</returns>
    public static string ConvertToCueBin(
        string ccdFilePath,
        string outputCuePath,
        bool copyBinFile = false
    )
    {
        var disc = CcdParser.Parse(ccdFilePath);
        return CueBinWriter.Write(disc, outputCuePath, copyBinFile);
    }

    /// <summary>
    ///     Converts a CloneCD image (.ccd/.img) to a standard .iso file.
    ///     Extracts 2048-byte user data sectors from the raw 2352-byte sectors.
    ///     Only valid for discs with data tracks (Mode 1 or Mode 2 Form 1).
    /// </summary>
    /// <param name="ccdFilePath">Path to the .ccd file.</param>
    /// <param name="isoFilePath">Path for the output .iso file.</param>
    /// <param name="progress">Optional progress callback (bytesWritten, totalBytes).</param>
    /// <returns>The path to the .iso file created.</returns>
    public static string ConvertToIso(
        string ccdFilePath,
        string isoFilePath,
        Action<long, long>? progress = null
    )
    {
        var disc = CcdParser.Parse(ccdFilePath);
        return IsoWriter.Write(disc, isoFilePath, progress);
    }

    /// <summary>
    ///     Generates a CUE sheet string from a parsed DiscImage.
    /// </summary>
    /// <param name="disc">The parsed disc image.</param>
    /// <param name="binFileName">The BIN file name to reference in the CUE sheet.</param>
    /// <returns>The complete CUE sheet content.</returns>
    public static string GenerateCueSheet(DiscImage disc, string binFileName)
    {
        return CueBinWriter.GenerateCueSheet(disc, binFileName);
    }

    /// <summary>
    ///     Checks if a file is a valid CloneCD .ccd file by verifying the [CloneCD] header.
    /// </summary>
    /// <param name="filePath">Path to the file to check.</param>
    /// <returns>True if the file appears to be a valid CCD file.</returns>
    public static bool IsCcdFile(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        try
        {
            using var reader = new StreamReader(filePath);
            var firstLine = reader.ReadLine();
            return firstLine?.Trim().Equals("[CloneCD]", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Gets a summary of the disc image (track count, modes, etc.).
    /// </summary>
    /// <param name="disc">The parsed disc image.</param>
    /// <returns>A human-readable summary string.</returns>
    public static string GetSummary(DiscImage disc)
    {
        var dataTracks = disc.Tracks.Count(t => !t.IsAudio);
        var audioTracks = disc.Tracks.Count(t => t.IsAudio);

        return $"CloneCD v{disc.Version}: {disc.Tracks.Count} tracks ({dataTracks} data, {audioTracks} audio), "
               + $"{disc.Sessions} session(s), Catalog: {disc.Catalog ?? "none"}";
    }
}