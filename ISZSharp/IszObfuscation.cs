namespace ISZSharp;

/// <summary>
///     Reverses the byte obfuscation UltraISO applies to the segment and chunk tables.
///     Both tables are written XORed with the bytes B6 8C A5 DE - the bitwise complement of "IsZ!" -
///     cycled a byte at a time. The published specification does not mention this, but every
///     independent reader de-obfuscates every table (libMirage's ISZ filter and isz-tool, which this
///     implementation was checked against), so any table read here is treated as obfuscated.
/// </summary>
internal static class IszObfuscation
{
    /// <summary>The XOR key: the bitwise complement of the four signature bytes, in order.</summary>
    private static ReadOnlySpan<byte> Mask => [0xB6, 0x8C, 0xA5, 0xDE];

    /// <summary>Applies the XOR in place; applying it a second time restores the original bytes.</summary>
    /// <param name="data">Table bytes to transform.</param>
    internal static void Apply(Span<byte> data)
    {
        for (var index = 0; index < data.Length; index++)
            data[index] ^= Mask[index % Mask.Length];
    }
}