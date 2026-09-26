using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MDSSharp;

/// <summary>
///     Decrypts and decompresses the descriptor of a Daemon Tools MDS v2 / MDX image, and decrypts
///     the optional track-data encryption header. MDS v2 keeps its descriptor encrypted (AES-256
///     with a password-less key derived from the file's salt) and zlib-compressed, and may
///     additionally encrypt the track data with AES-256 in LRW mode. The algorithms were ported
///     from the MIT-licensed mdsx project (https://github.com/Marisa-Chan/mdsx) and validated
///     against its test images.
/// </summary>
internal static class MdxCrypto
{
    /// <summary>Size of the PKCS#5 salt at the start of the encryption header.</summary>
    private const int SaltSize = 64;

    /// <summary>Size of the 512-byte primary encryption header.</summary>
    private const int EncryptionHeaderSize = 512;

    /// <summary>Offset of the encrypted part inside the encryption header (past the salt).</summary>
    private const int EncryptionHeaderDataOffset = 64;

    /// <summary>Size of the 256-byte key data area inside the encryption header.</summary>
    private const int KeyDataSize = 256;

    /// <summary>Offset of the key data area inside the encryption header.</summary>
    private const int KeyDataOffset = EncryptionHeaderDataOffset + 16;

    /// <summary>Offset of the compressed descriptor size inside the encryption header.</summary>
    private const int CompressedSizeOffset = KeyDataOffset + KeyDataSize;

    /// <summary>Offset of the decompressed descriptor size inside the encryption header.</summary>
    private const int DecompressedSizeOffset = CompressedSizeOffset + 4;

    /// <summary>Size of the descriptor file header that precedes the descriptor body.</summary>
    private const int FileHeaderSize = 0x30;

    /// <summary>Magic value ("TRUE") of a deciphered encryption header, stored little-endian.</summary>
    private const uint HeaderMagic = 0x54525545;

    /// <summary>The sentinel encryption-header offset of a single-file MDX image.</summary>
    private const uint MdxMarker = 0xFFFFFFFF;

    /// <summary>Number of PBKDF2 iterations used by the descriptor key derivation.</summary>
    private const int Pbkdf2Iterations = 2000;

    /// <summary>Derived key length: 32 bytes of IV seed, 32 bytes of padding, then the AES key.</summary>
    private const int MasterKeyLength = 120 + 32;

    /// <summary>Maximum accepted password length; longer passwords are truncated like the reference.</summary>
    private const int MaxPasswordLength = 64;

    /// <summary>Offset of the track-data encryption header pointer in the decrypted descriptor.</summary>
    private const int DataEncryptionHeaderOffset = 0x58;

    /// <summary>
    ///     Decrypts the descriptor of <paramref name="fileBytes" /> and returns the reconstructed
    ///     descriptor buffer plus whether the file is a single-file MDX container. Offsets inside
    ///     the returned buffer are relative to its start, which includes the 18-byte signature and
    ///     version prefix copied from the file header.
    /// </summary>
    /// <param name="fileBytes">Whole contents of the .mds/.mdx file.</param>
    /// <returns>The decrypted descriptor and whether the source was an MDX container.</returns>
    /// <exception cref="InvalidDataException">The file is not a readable MDS v2/MDX image.</exception>
    internal static (byte[] Descriptor, bool IsMdx) DecryptDescriptor(byte[] fileBytes)
    {
        if (fileBytes.Length < FileHeaderSize + 16)
            throw new InvalidDataException("the MDS v2 file is truncated.");

        var marker = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(0x2C));
        var isMdx = marker == MdxMarker;

        long encryptionHeaderOffset;
        long descriptorOffset;
        long declaredDescriptorSize;
        if (isMdx)
        {
            var footerOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fileBytes.AsSpan(0x30));
            var footerLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(fileBytes.AsSpan(0x38));
            descriptorOffset = footerOffset;
            declaredDescriptorSize = footerLength - 64;
            encryptionHeaderOffset = footerOffset + declaredDescriptorSize;

            if (
                descriptorOffset < FileHeaderSize + 16
                || declaredDescriptorSize < 16
                || encryptionHeaderOffset + EncryptionHeaderSize > fileBytes.Length
            )
            {
                throw new InvalidDataException(
                    "the MDX descriptor area is outside the file, so the image is corrupt or truncated."
                );
            }
        }
        else
        {
            encryptionHeaderOffset = marker;
            descriptorOffset = FileHeaderSize;
            declaredDescriptorSize = -1;

            if (
                encryptionHeaderOffset < FileHeaderSize + 16
                || encryptionHeaderOffset + EncryptionHeaderSize > fileBytes.Length
            )
            {
                throw new InvalidDataException(
                    "the MDS v2 encryption header is outside the file, so the descriptor is corrupt or truncated."
                );
            }
        }

        var header = new byte[EncryptionHeaderSize];
        Array.Copy(fileBytes, encryptionHeaderOffset, header, 0, EncryptionHeaderSize);

        DecipherEncryptionHeader(header, mainHeader: true);

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(EncryptionHeaderDataOffset + 4)
        );
        var keySize = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(EncryptionHeaderDataOffset + 10)
        );
        var keyDataChecksum = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(EncryptionHeaderDataOffset)
        );
        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(CompressedSizeOffset)
        );
        var decompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(DecompressedSizeOffset)
        );
        var keyData = header.AsSpan(KeyDataOffset, KeyDataSize);

        if (magic != HeaderMagic || keySize != KeyDataSize)
        {
            throw new InvalidDataException(
                "the MDS v2 descriptor encryption header could not be deciphered; the file is corrupt or uses an unsupported variant."
            );
        }

        if (Crc32Standard(keyData) != keyDataChecksum)
        {
            throw new InvalidDataException(
                "the MDS v2 descriptor encryption header failed its checksum; the file is corrupt."
            );
        }

        var descriptorLength = (int)(((compressedSize + 15L) / 16L) * 16);
        if (descriptorLength <= 0)
        {
            throw new InvalidDataException(
                "the MDS v2 descriptor size does not match the file layout; the file is corrupt or truncated."
            );
        }

        if (!isMdx && FileHeaderSize + descriptorLength != encryptionHeaderOffset)
        {
            throw new InvalidDataException(
                "the MDS v2 descriptor size does not match the file layout; the file is corrupt or truncated."
            );
        }

        if (isMdx && descriptorLength != declaredDescriptorSize)
        {
            throw new InvalidDataException(
                "the MDX descriptor size does not match the file layout; the image is corrupt or truncated."
            );
        }

        var descriptor = new byte[descriptorLength];
        Array.Copy(fileBytes, descriptorOffset, descriptor, 0, descriptorLength);

        var aesKey = keyData.Slice(32, 32).ToArray();
        var iv = keyData.Slice(0, 16).ToArray();
        DecipherCbc(aesKey, iv, descriptor);

        var output = new byte[decompressedSize + 18];
        try
        {
            using var input = new MemoryStream(descriptor, 0, (int)compressedSize);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var read = 0;
            while (read < decompressedSize)
            {
                var count = zlib.Read(output, 18 + read, (int)decompressedSize - read);
                if (count == 0) break;

                read += count;
            }

            if (read != decompressedSize)
            {
                throw new InvalidDataException(
                    "the MDS v2 descriptor decompressed to an unexpected size; the file is corrupt or truncated."
                );
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"the MDS v2 descriptor could not be decompressed: {ex.Message}",
                ex
            );
        }

        Array.Copy(fileBytes, 0, output, 0, 18);
        return (output, isMdx);
    }

    /// <summary>
    ///     Deciphers the track-data encryption header stored in the descriptor and returns its
    ///     decrypted key data. A null <paramref name="password" /> uses the format's password-less
    ///     derivation (TAGES-style images); otherwise the supplied password is used.
    /// </summary>
    /// <param name="descriptor">Decrypted descriptor buffer.</param>
    /// <param name="password">User password, or null for the password-less derivation.</param>
    /// <returns>The 256-byte decrypted key data, or null when the image has no data encryption.</returns>
    /// <exception cref="InvalidDataException">The header cannot be deciphered with the given password.</exception>
    internal static byte[]? DecipherDataHeader(byte[] descriptor, string? password)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(
            descriptor.AsSpan(DataEncryptionHeaderOffset)
        );
        if (offset == 0) return null;

        if (offset + EncryptionHeaderSize > descriptor.Length)
        {
            throw new InvalidDataException(
                "the MDS v2 track-data encryption header is outside the descriptor; the file is corrupt."
            );
        }

        var header = new byte[EncryptionHeaderSize];
        Array.Copy(descriptor, offset, header, 0, EncryptionHeaderSize);
        DecipherEncryptionHeader(header, mainHeader: false, password);

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(EncryptionHeaderDataOffset + 4)
        );
        var keySize = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(EncryptionHeaderDataOffset + 10)
        );
        var keyDataChecksum = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(EncryptionHeaderDataOffset)
        );
        var keyData = header.AsSpan(KeyDataOffset, KeyDataSize);

        if (magic != HeaderMagic || keySize != KeyDataSize || Crc32Standard(keyData) != keyDataChecksum)
        {
            throw new InvalidDataException(
                password is null
                    ? "the track data is encrypted and no password was supplied (or the image is corrupt)."
                    : "the track data is encrypted and the supplied password is wrong (or the image is corrupt)."
            );
        }

        return keyData.ToArray();
    }

    /// <summary>
    ///     Deciphers a 512-byte encryption header in place. The main descriptor header is
    ///     password-less and uses AES-256 CBC with de-whitening; the track-data header uses
    ///     AES-256 LRW with the salt-derived or supplied password.
    /// </summary>
    /// <param name="header">The 512-byte encryption header, deciphered in place.</param>
    /// <param name="mainHeader">True for the descriptor header, false for the track-data header.</param>
    /// <param name="password">User password for the track-data header, or null.</param>
    private static void DecipherEncryptionHeader(
        byte[] header,
        bool mainHeader,
        string? password = null
    )
    {
        var salt = header.AsSpan(0, SaltSize);
        var passwordBytes = password is null ? DerivePassword(salt) : PasswordBytes(password);

        var masterKey = Pbkdf2Ripemd160(
            passwordBytes,
            salt.ToArray(),
            Pbkdf2Iterations,
            MasterKeyLength
        );

        var aesKey = masterKey.AsSpan(32, 32).ToArray();
        var encrypted = header.AsSpan(EncryptionHeaderDataOffset).ToArray();

        if (mainHeader)
        {
            DecipherCbc(aesKey, masterKey.AsSpan(0, 16).ToArray(), encrypted);
        }
        else
        {
            DecipherLrw(aesKey, masterKey.AsSpan(0, 16).ToArray(), encrypted, sectorNumber: 1);
        }

        Array.Copy(encrypted, 0, header, EncryptionHeaderDataOffset, encrypted.Length);
    }

    /// <summary>Converts a password string to its byte form, truncated to the reference limit.</summary>
    /// <param name="password">Password text.</param>
    private static byte[] PasswordBytes(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        if (bytes.Length <= MaxPasswordLength) return bytes;

        var truncated = new byte[MaxPasswordLength];
        Array.Copy(bytes, truncated, MaxPasswordLength);
        return truncated;
    }

    /// <summary>
    ///     Derives the descriptor password from the salt, reproducing the "unshuffle" step of the
    ///     reference implementation: a CRC32 of the salt seeds a 32-bit LCG whose output is mixed
    ///     into each 32-bit word, with zero octets replaced by 0x5F.
    /// </summary>
    /// <param name="salt">The 64-byte salt from the encryption header.</param>
    /// <returns>The 64-byte derived password.</returns>
    private static byte[] DerivePassword(ReadOnlySpan<byte> salt)
    {
        var password = new byte[SaltSize];
        salt.CopyTo(password);

        var modifier = Crc32Edc(password) ^ 0x567372ff;
        for (var i = 0; i < SaltSize / 4; i++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(password.AsSpan(i * 4));
            modifier = (modifier * 0x35e85a6d) + 0x1548dce9;
            value = value ^ modifier ^ 0xec564717;

            if ((value & 0x000000ff) == 0) value |= 0x0000005f;
            if ((value & 0x0000ff00) == 0) value |= 0x00005f00;
            if ((value & 0x00ff0000) == 0) value |= 0x005f0000;
            if ((value & 0xff000000) == 0) value |= 0x5f000000;

            BinaryPrimitives.WriteUInt32LittleEndian(password.AsSpan(i * 4), value);
        }

        return password;
    }

    /// <summary>
    ///     Decrypts a buffer with AES-256 in the CBC variant used by the format: every 16-byte block
    ///     is de-whitened with the upper half of the IV before decryption, and 512-byte groups each
    ///     restart from the IV.
    /// </summary>
    /// <param name="key">32-byte AES key.</param>
    /// <param name="iv">16-byte IV.</param>
    /// <param name="data">Buffer decrypted in place; its length must be a multiple of 16.</param>
    private static void DecipherCbc(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();

        var iv0 = BinaryPrimitives.ReadUInt64LittleEndian(iv.AsSpan(0));
        var iv1 = BinaryPrimitives.ReadUInt64LittleEndian(iv.AsSpan(8));

        var offset = 0;
        while (offset < data.Length)
        {
            var length = Math.Min(512, data.Length - offset);
            var blockIv0 = iv0;
            var blockIv1 = iv1;

            for (var i = 0; i < length; i += 16)
            {
                var span = data.AsSpan(offset + i, 16);
                var d0 = BinaryPrimitives.ReadUInt64LittleEndian(span) ^ iv1;
                var d1 = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]) ^ iv1;
                BinaryPrimitives.WriteUInt64LittleEndian(span, d0);
                BinaryPrimitives.WriteUInt64LittleEndian(span[8..], d1);

                var cipher0 = d0;
                var cipher1 = d1;

                decryptor.TransformBlock(data, offset + i, 16, data, offset + i);

                var p0 = BinaryPrimitives.ReadUInt64LittleEndian(span) ^ blockIv0;
                var p1 = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]) ^ blockIv1;
                BinaryPrimitives.WriteUInt64LittleEndian(span, p0);
                BinaryPrimitives.WriteUInt64LittleEndian(span[8..], p1);

                blockIv0 = cipher0;
                blockIv1 = cipher1;
            }

            offset += length;
        }
    }

    /// <summary>
    ///     Decrypts a buffer with AES-256 in LRW mode, the variant used for MDS v2 track data: each
    ///     16-byte block is XORed with the tweak (the tweak key multiplied by the block counter in
    ///     GF(2^128)), decrypted, and XORed with the tweak again.
    /// </summary>
    /// <param name="aesKey">32-byte AES key.</param>
    /// <param name="tweakKey">16-byte tweak key.</param>
    /// <param name="data">Buffer decrypted in place; its length must be a multiple of 16.</param>
    /// <param name="sectorNumber">First tweak counter value.</param>
    internal static void DecipherLrw(
        byte[] aesKey,
        byte[] tweakKey,
        byte[] data,
        ulong sectorNumber
    )
    {
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();

        var tweak = new byte[16];
        for (var i = 0; i < data.Length; i += 16)
        {
            Gf128Multiply(tweakKey, sectorNumber + (ulong)(i / 16), tweak);
            var span = data.AsSpan(i, 16);
            for (var j = 0; j < 16; j++) span[j] ^= tweak[j];

            decryptor.TransformBlock(data, i, 16, data, i);

            for (var j = 0; j < 16; j++) span[j] ^= tweak[j];
        }
    }

    /// <summary>
    ///     Multiplies a 16-byte key by a 64-bit counter in GF(2^128) using the format's big-endian
    ///     byte convention (the last byte holds the lowest-degree coefficients), reducing modulo
    ///     x^128 + x^7 + x^2 + x + 1.
    /// </summary>
    /// <param name="key">16-byte field element.</param>
    /// <param name="index">Counter, placed in the low 64 bits big-endian.</param>
    /// <param name="destination">Receives the 16-byte product.</param>
    private static void Gf128Multiply(ReadOnlySpan<byte> key, ulong index, Span<byte> destination)
    {
        Span<byte> y = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(y[8..], index);

        destination.Clear();
        Span<byte> v = stackalloc byte[16];
        key.CopyTo(v);

        for (var byteIndex = 15; byteIndex >= 0; byteIndex--)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                if (((y[byteIndex] >> bit) & 1) != 0)
                {
                    for (var j = 0; j < 16; j++) destination[j] ^= v[j];
                }

                var highBit = (v[0] & 0x80) != 0;
                for (var j = 0; j < 15; j++) v[j] = (byte)((v[j] << 1) | (v[j + 1] >> 7));
                v[15] = (byte)(v[15] << 1);
                if (highBit) v[15] ^= 0x87;
            }
        }
    }

    /// <summary>
    ///     Computes the CRC32 used for the salt password derivation: the CD EDC polynomial
    ///     0xD8018001, reflected, initial value 0 and no final inversion.
    /// </summary>
    /// <param name="data">Bytes to checksum.</param>
    /// <returns>The CRC32 value.</returns>
    private static uint Crc32Edc(ReadOnlySpan<byte> data)
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) * 0xD8018001u);
            }

            table[i] = crc;
        }

        var result = 0u;
        foreach (var value in data)
        {
            result = table[(result ^ value) & 0xFF] ^ (result >> 8);
        }

        return result;
    }

    /// <summary>
    ///     Computes the standard CRC-32 (IEEE 802.3) used for the key-data checksum: reflected
    ///     polynomial 0xEDB88320, initial value and final XOR both 0xFFFFFFFF.
    /// </summary>
    /// <param name="data">Bytes to checksum.</param>
    /// <returns>The CRC-32 value.</returns>
    private static uint Crc32Standard(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    ///     Derives a key with PBKDF2-HMAC-RIPEMD160. .NET does not expose RIPEMD-160, so the PRF is
    ///     implemented locally.
    /// </summary>
    /// <param name="password">Password bytes.</param>
    /// <param name="salt">Salt bytes.</param>
    /// <param name="iterations">Iteration count.</param>
    /// <param name="length">Number of bytes to produce.</param>
    /// <returns>The derived key.</returns>
    private static byte[] Pbkdf2Ripemd160(byte[] password, byte[] salt, int iterations, int length)
    {
        var result = new byte[length];
        var blockCount = (length + 19) / 20;
        var u = new byte[20];
        var t = new byte[20];

        for (var block = 1; block <= blockCount; block++)
        {
            var saltBlock = new byte[salt.Length + 4];
            salt.CopyTo(saltBlock, 0);
            BinaryPrimitives.WriteUInt32BigEndian(saltBlock.AsSpan(salt.Length), (uint)block);

            HmacRipemd160(password, saltBlock, u);
            u.CopyTo(t, 0);

            for (var i = 1; i < iterations; i++)
            {
                HmacRipemd160(password, u, u);
                for (var j = 0; j < 20; j++)
                {
                    t[j] ^= u[j];
                }
            }

            var copy = Math.Min(20, length - ((block - 1) * 20));
            Array.Copy(t, 0, result, (block - 1) * 20, copy);
        }

        return result;
    }

    /// <summary>Computes HMAC-RIPEMD160 over <paramref name="data" /> with <paramref name="key" />.</summary>
    /// <param name="key">HMAC key.</param>
    /// <param name="data">Message.</param>
    /// <param name="output">Receives the 20-byte MAC.</param>
    private static void HmacRipemd160(byte[] key, byte[] data, byte[] output)
    {
        const int blockSize = 64;
        var padded = new byte[blockSize];
        var effectiveKey = key.Length > blockSize ? Ripemd160.Hash(key) : key;
        effectiveKey.CopyTo(padded, 0);

        var inner = new byte[blockSize + data.Length];
        var outer = new byte[blockSize + 20];
        for (var i = 0; i < blockSize; i++)
        {
            inner[i] = (byte)(padded[i] ^ 0x36);
            outer[i] = (byte)(padded[i] ^ 0x5c);
        }

        data.CopyTo(inner, blockSize);
        var innerHash = Ripemd160.Hash(inner);
        innerHash.CopyTo(outer, blockSize);
        var outerHash = Ripemd160.Hash(outer);
        outerHash.CopyTo(output, 0);
    }
}
