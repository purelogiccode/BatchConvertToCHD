namespace CSOSharp.Models;

/// <summary>
/// Error codes returned by CSO file operations.
/// </summary>
public enum CsoError
{
    /// <summary>
    /// No error. Operation completed successfully.
    /// </summary>
    None,

    /// <summary>
    /// The file does not contain a valid CSO/CISO magic header.
    /// </summary>
    InvalidHeader,

    /// <summary>
    /// The CSO file version is not supported (only v1 and v2 are supported).
    /// </summary>
    UnsupportedVersion,

    /// <summary>
    /// The file could not be found or opened.
    /// </summary>
    FileNotFound,

    /// <summary>
    /// An I/O error occurred while reading the file.
    /// </summary>
    IoError,

    /// <summary>
    /// The index table is corrupt or references invalid offsets.
    /// </summary>
    CorruptIndex,

    /// <summary>
    /// A block decompression failed.
    /// </summary>
    DecompressionError,

    /// <summary>
    /// The specified block index is out of range.
    /// </summary>
    BlockOutOfRange,

    /// <summary>
    /// The block size in the header is zero or invalid.
    /// </summary>
    InvalidBlockSize,
}