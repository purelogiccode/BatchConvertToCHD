using CHDSharp;

namespace CHDBattleTest;

public static class Classifier
{
    public static (bool IsChd, uint Version, MediaKind Kind, ulong LogicalBytes, string? Error) Inspect(string path)
    {
        if (!Chd.IsChdFile(path, out uint version))
            return (false, 0, MediaKind.Unknown, 0, "not a CHD file");

        var err = ChdFile.Open(path, out var chd);
        if (err != CHDSharp.Models.ChdError.Chderrnone)
            return (true, version, MediaKind.Unknown, 0, $"open failed: {err}");

        try
        {
            ulong logical = (ulong)chd.HunkCount * chd.HunkBytes;
            MediaKind kind = DetectKind(path, chd);
            return (true, version, kind, logical, null);
        }
        finally
        {
            chd.Dispose();
        }
    }

    private static MediaKind DetectKind(string path, ChdFile chd)
    {
        var classErr = Chd.Classify(path, out string? classification);
        if (classErr == CHDSharp.Models.ChdError.Chderrnone && classification is not null)
        {
            return classification switch
            {
                "cd" => MediaKind.Cd,
                "gd-rom" => MediaKind.GdRom,
                "dvd" => MediaKind.Dvd,
                "hdd" => MediaKind.Hdd,
                _ => MediaKind.Unknown
            };
        }

        bool isAv = false;
        foreach (var meta in chd.Metadata)
        {
            string s = meta.ToString() ?? "";
            if (s.Contains("AVLD", StringComparison.Ordinal) || s.Contains("AVAV", StringComparison.Ordinal))
            {
                isAv = true;
                break;
            }
        }

        return isAv ? MediaKind.LaserDisc : MediaKind.Unknown;
    }
}