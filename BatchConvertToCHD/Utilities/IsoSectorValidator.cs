using System.IO;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Pre-conversion validation for disc image files: a disc image must be a multiple of one of the
/// standard sector sizes (CD raw 2352, DVD/data 2048, Mode 2 XA 2336, Mode 2 Form 1 2324);
/// anything else is corrupt or truncated. Running chdman on such a file wastes a long conversion,
/// so the app validates first and skips with a clear warning.
/// </summary>
internal static class IsoSectorValidator
{
    /// <summary>
    /// Standard CD/DVD sector sizes used to validate disc image alignment.
    /// 2448 and 2368 are raw CD sectors carrying subchannel data (2352 + 96, and 2352 + 16), which
    /// Alcohol and CloneCD rips routinely use; without them such images are wrongly reported as
    /// possibly corrupt.
    /// </summary>
    internal static readonly long[] StandardSectorSizes = [2352, 2048, 2336, 2324, 2448, 2368];

    /// <summary>
    /// Returns a user-facing warning when <paramref name="imagePath"/> has a size that is not
    /// divisible by any standard sector size, or null when it is aligned (or unknown).
    /// Text descriptor files (.cue/.gdi/.toc) must not be validated — their size is irrelevant.
    /// </summary>
    /// <param name="imagePath">Path of the disc image file to validate.</param>
    internal static string? GetSectorSizeWarning(string imagePath)
    {
        if (
            imagePath.EndsWith(FileExtensions.Cue, StringComparison.OrdinalIgnoreCase)
            || imagePath.EndsWith(FileExtensions.Gdi, StringComparison.OrdinalIgnoreCase)
            || imagePath.EndsWith(FileExtensions.Toc, StringComparison.OrdinalIgnoreCase)
        )
        {
            return null;
        }

        long fileSize;
        try
        {
            fileSize = new FileInfo(imagePath).Length;
        }
        catch (Exception)
        {
            return null;
        }

        if (fileSize > 0 && StandardSectorSizes.All(sectorSize => fileSize % sectorSize != 0))
        {
            return
                $"file size ({fileSize:N0} bytes) is not divisible by any standard sector size (2048/2324/2336/2352/2368/2448). The file may be corrupt or truncated.";
        }

        return null;
    }
}