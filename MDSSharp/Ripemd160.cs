using System.Buffers.Binary;

namespace MDSSharp;

/// <summary>
///     A self-contained RIPEMD-160 implementation, needed because .NET does not ship the algorithm
///     and the MDS v2 descriptor key derivation uses it as the PBKDF2 PRF.
/// </summary>
internal static class Ripemd160
{
    /// <summary>Message word selection for the left line.</summary>
    private static readonly int[] R =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
        3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
        1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2,
        4, 0, 5, 9, 7, 12, 2, 10, 14, 1, 3, 8, 11, 6, 15, 13,
    ];

    /// <summary>Message word selection for the right line.</summary>
    private static readonly int[] Rp =
    [
        5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
        6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
        15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
        8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14,
        12, 15, 10, 4, 1, 5, 8, 7, 6, 2, 13, 14, 0, 3, 9, 11,
    ];

    /// <summary>Rotation amounts for the left line.</summary>
    private static readonly int[] S =
    [
        11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
        7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
        11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
        11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12,
        9, 15, 5, 11, 6, 8, 13, 12, 5, 12, 13, 14, 11, 8, 5, 6,
    ];

    /// <summary>Rotation amounts for the right line.</summary>
    private static readonly int[] Sp =
    [
        8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
        9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
        9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
        15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8,
        8, 5, 12, 9, 12, 5, 14, 6, 8, 13, 6, 5, 15, 13, 11, 11,
    ];

    /// <summary>Round constants for the left line.</summary>
    private static readonly uint[] K = [0x00000000, 0x5A827999, 0x6ED9EBA1, 0x8F1BBCDC, 0xA953FD4E];

    /// <summary>Round constants for the right line.</summary>
    private static readonly uint[] Kp = [0x50A28BE6, 0x5C4DD124, 0x6D703EF3, 0x7A6D76E9, 0x00000000];

    /// <summary>Computes the RIPEMD-160 digest of <paramref name="message" />.</summary>
    /// <param name="message">Bytes to hash.</param>
    /// <returns>The 20-byte digest.</returns>
    internal static byte[] Hash(byte[] message)
    {
        var h0 = 0x67452301u;
        var h1 = 0xEFCDAB89u;
        var h2 = 0x98BADCFEu;
        var h3 = 0x10325476u;
        var h4 = 0xC3D2E1F0u;

        var paddedLength = (((message.Length + 8) / 64) + 1) * 64;
        var padded = new byte[paddedLength];
        message.CopyTo(padded, 0);
        padded[message.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(
            padded.AsSpan(paddedLength - 8),
            (ulong)message.Length * 8
        );

        var words = new uint[16];
        for (var offset = 0; offset < paddedLength; offset += 64)
        {
            for (var i = 0; i < 16; i++)
            {
                words[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(offset + (i * 4)));
            }

            var a = h0;
            var b = h1;
            var c = h2;
            var d = h3;
            var e = h4;
            var ap = h0;
            var bp = h1;
            var cp = h2;
            var dp = h3;
            var ep = h4;

            for (var j = 0; j < 80; j++)
            {
                var t = RotateLeft(a + F(j, b, c, d) + words[R[j]] + K[j / 16], S[j]) + e;
                a = e;
                e = d;
                d = RotateLeft(c, 10);
                c = b;
                b = t;

                var tp = RotateLeft(ap + F(79 - j, bp, cp, dp) + words[Rp[j]] + Kp[j / 16], Sp[j]) + ep;
                ap = ep;
                ep = dp;
                dp = RotateLeft(cp, 10);
                cp = bp;
                bp = tp;
            }

            var next0 = h1 + c + dp;
            h1 = h2 + d + ep;
            h2 = h3 + e + ap;
            h3 = h4 + a + bp;
            h4 = h0 + b + cp;
            h0 = next0;
        }

        var output = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0), h0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), h1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), h2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), h3);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), h4);
        return output;
    }

    /// <summary>Selects the round function for step <paramref name="step" />.</summary>
    /// <param name="step">Round index 0-79.</param>
    /// <param name="x">First word.</param>
    /// <param name="y">Second word.</param>
    /// <param name="z">Third word.</param>
    private static uint F(int step, uint x, uint y, uint z)
    {
        return step switch
        {
            < 16 => x ^ y ^ z,
            < 32 => (x & y) | (~x & z),
            < 48 => (x | ~y) ^ z,
            < 64 => (x & z) | (y & ~z),
            _ => x ^ (y | ~z),
        };
    }

    /// <summary>Rotates a 32-bit value left.</summary>
    /// <param name="value">Value to rotate.</param>
    /// <param name="bits">Bit count.</param>
    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
}
