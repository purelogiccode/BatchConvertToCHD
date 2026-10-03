using System.Globalization;
using System.Text;
using BatchConvertToCHD.Models;
using CHDSharp;
using CHDSharp.Models;

namespace BatchConvertToCHD.Utilities;

/// <summary>
///     Builds the Explorer's "Image Info" report for a CHD: header fields, image type, codecs,
///     metadata tags, CD/GD-ROM track table, and the per-codec hunk distribution (bounded so a
///     multi-terabyte image cannot stall the report).
/// </summary>
internal static class ChdInfoReport
{
    /// <summary>Hunks scanned for the codec distribution before the scan is truncated.</summary>
    private const int MaxHunksToScan = 1_000_000;

    /// <summary>Longest metadata text preview written into the report.</summary>
    private const int MaxMetadataTextPreview = 100;

    /// <summary>
    ///     Builds the report for <paramref name="chdPath" />. Never throws: unreadable headers or
    ///     failed opens are reported in the text.
    /// </summary>
    /// <param name="chdPath">Path of the CHD image.</param>
    /// <returns>The multi-line report text.</returns>
    internal static string Build(string chdPath)
    {
        var builder = new StringBuilder();
        builder.Append("File:        ").AppendLine(Path.GetFileName(chdPath));
        builder.Append("Size:        ").AppendLine(FormatBytes((ulong)new FileInfo(chdPath).Length));

        if (Chd.ReadHeader(chdPath, out var header) != ChdError.Chderrnone || header is null)
        {
            builder.AppendLine();
            builder.AppendLine("The CHD header could not be read.");
            return builder.ToString();
        }

        builder
            .Append("Version:     V")
            .AppendLine(header.Version.ToString(CultureInfo.InvariantCulture));
        builder.Append("Hunk size:   ").AppendLine(FormatBytes(header.HunkBytes));
        builder
            .Append("Total hunks: ")
            .AppendLine(header.TotalHunks.ToString("N0", CultureInfo.InvariantCulture));
        builder.Append("Logical:     ").AppendLine(FormatBytes(header.TotalBytes));
        builder.Append("Unit size:   ").AppendLine(FormatBytes(header.UnitBytes));
        builder.Append("SHA-1:       ").AppendLine(HexOrUnavailable(header.Sha1));
        builder.Append("MD5:         ").AppendLine(HexOrUnavailable(header.Md5));
        if (header.HasParent)
        {
            builder.Append("Parent SHA-1:").Append(' ').AppendLine(HexOrUnavailable(header.ParentSha1));
        }

        var err = ChdFile.Open(chdPath, out var chd);
        if (err != ChdError.Chderrnone || chd is null)
        {
            builder.AppendLine();
            builder.Append("Open error:  ").AppendLine(err.GetMessage());
            return builder.ToString();
        }

        using (chd)
        {
            builder.Append("Type:        ").AppendLine(DescribeType(chd));
            builder.Append("Codecs:      ").AppendLine(DescribeCodecs(chd));
            AppendMetadata(builder, chd);
            AppendTracks(builder, chd);
            AppendHunkDistribution(builder, chd);
        }

        return builder.ToString();
    }

    /// <summary>Describes the image type from the CHD's own flags.</summary>
    /// <param name="chd">The opened CHD.</param>
    /// <returns>The type name.</returns>
    private static string DescribeType(ChdFile chd)
    {
        if (chd.IsGdRom) return "GD-ROM";
        if (chd.IsCd) return "CD";
        if (chd.IsDvd) return "DVD";
        if (chd.IsHdd) return "Hard disk";
        return "A/V (laserdisc)";
    }

    /// <summary>Lists the configured codecs with their display names.</summary>
    /// <param name="chd">The opened CHD.</param>
    /// <returns>The codec list, plus the secondary codec when present.</returns>
    private static string DescribeCodecs(ChdFile chd)
    {
        var names = chd.Compression.Select(ChdFile.GetHunkCodecNameForCodec).ToList();
        if (chd.SecondaryCodec != ChdCodec.None)
            names.Add($"{ChdFile.GetHunkCodecNameForCodec(chd.SecondaryCodec)} (secondary)");

        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }

    /// <summary>Appends the metadata tag list, with a short text preview for text entries.</summary>
    /// <param name="builder">Report builder.</param>
    /// <param name="chd">The opened CHD.</param>
    private static void AppendMetadata(StringBuilder builder, ChdFile chd)
    {
        builder.AppendLine();
        builder.AppendLine("Metadata:");
        if (chd.Metadata.Count == 0)
        {
            builder.AppendLine("  (none)");
            return;
        }

        foreach (var meta in chd.Metadata)
        {
            builder
                .Append("  ")
                .Append(meta.Tag)
                .Append(" (")
                .Append(meta.Data.Length.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" bytes");

            if (meta.IsText)
            {
                var text = ExtractTextPreview(meta.Data);
                if (text.Length > 0) builder.Append(": ").Append(text);
            }

            builder.AppendLine(")");
        }
    }

    /// <summary>Appends the CD/GD-ROM track table when the image has one.</summary>
    /// <param name="builder">Report builder.</param>
    /// <param name="chd">The opened CHD.</param>
    private static void AppendTracks(StringBuilder builder, ChdFile chd)
    {
        if (chd.Tracks is not { Count: > 0 }) return;

        builder.AppendLine();
        builder.AppendLine("Tracks:");
        foreach (var track in chd.Tracks)
        {
            builder
                .Append("  ")
                .Append(track.TrackNumber.ToString("D2", CultureInfo.InvariantCulture))
                .Append(": ")
                .Append(track.TrackType)
                .Append(", ")
                .Append(track.Frames.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" frames, ")
                .Append(FormatBytes((ulong)((long)track.Frames * track.DataSize)));

            if (track.SubType != ChdSubType.None) builder.Append(", subcode ").Append(track.SubType);
            builder.AppendLine();
        }
    }

    /// <summary>Appends the per-codec hunk distribution, capped at <see cref="MaxHunksToScan" /> hunks.</summary>
    /// <param name="builder">Report builder.</param>
    /// <param name="chd">The opened CHD.</param>
    private static void AppendHunkDistribution(StringBuilder builder, ChdFile chd)
    {
        if (chd.HunkCount == 0) return;

        var scanCount = Math.Min(chd.HunkCount, MaxHunksToScan);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (uint i = 0; i < scanCount; i++)
        {
            var name = chd.GetHunkCodecName(i);
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        builder.AppendLine();
        builder.Append("Hunks:       ");
        if (scanCount < chd.HunkCount)
        {
            builder
                .Append("(first ")
                .Append(scanCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" of ")
                .Append(chd.HunkCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(")");
        }

        builder.AppendLine();
        foreach (
            var count in counts
                .OrderByDescending(static kv => kv.Value)
                .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
        )
        {
            builder
                .Append("  ")
                .Append(count.Key)
                .Append(": ")
                .AppendLine(count.Value.ToString("N0", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Extracts a single-line printable preview from a text metadata payload.</summary>
    /// <param name="data">The metadata payload.</param>
    /// <returns>The preview, or an empty string.</returns>
    private static string ExtractTextPreview(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        var cleaned = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\0') continue;
            cleaned.Append(ch is '\r' or '\n' or '\t' ? ' ' : ch);
        }

        var result = cleaned.ToString().Trim();
        return result.Length <= MaxMetadataTextPreview
            ? result
            : string.Concat(result.AsSpan(0, MaxMetadataTextPreview), "...");
    }

    /// <summary>Formats a byte count with the shared size formatter.</summary>
    /// <param name="bytes">The byte count.</param>
    /// <returns>The formatted size.</returns>
    private static string FormatBytes(ulong bytes)
    {
        return ChdExplorerItem.FormatSize(bytes);
    }

    /// <summary>Formats a hash as lowercase hex, or a placeholder when absent.</summary>
    /// <param name="hash">The hash bytes, or null/empty.</param>
    /// <returns>The hex string or <c>"(not available)"</c>.</returns>
    private static string HexOrUnavailable(byte[]? hash)
    {
        return hash is { Length: > 0 }
            ? Convert.ToHexString(hash).ToLowerInvariant()
            : "(not available)";
    }
}
