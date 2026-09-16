using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Helmsman.Core.Identifiers;

/// <summary>
/// A SHA-256 digest in lowercase hex.
/// </summary>
/// <remarks>
/// Used for two purposes that are the same operation: linking audit events into a chain, and
/// addressing artifacts by their content. Artifacts are content-addressed, so an artifact's
/// identity <em>is</em> its digest — giving it a separate id type would let the same bytes
/// exist under two identities and quietly break provenance.
/// </remarks>
public readonly record struct Sha256Hash
{
    private const int HexLength = 64;

    private Sha256Hash(string hex) => Hex = hex;

    /// <summary>The digest as 64 lowercase hex characters.</summary>
    public string Hex { get; }

    /// <summary>
    /// The all-zero digest, used as the predecessor of the first event in a chain.
    /// </summary>
    /// <remarks>
    /// A distinguished genesis value means "no predecessor" is representable without a null,
    /// so chain verification never has to special-case the first link.
    /// </remarks>
    public static Sha256Hash Genesis { get; } = new(new string('0', HexLength));

    /// <summary>Digest of raw bytes.</summary>
    public static Sha256Hash OfBytes(ReadOnlySpan<byte> content)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(content, digest);
        // Convert.ToHexStringLower is .NET 9+; on the LTS target we lower explicitly.
        return new Sha256Hash(Convert.ToHexString(digest).ToLowerInvariant());
    }

    /// <summary>Digest of a string's UTF-8 encoding.</summary>
    public static Sha256Hash OfUtf8(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return OfBytes(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>Parses a hex digest, throwing when malformed.</summary>
    public static Sha256Hash Parse(string value) =>
        TryParse(value, out Sha256Hash hash)
            ? hash
            : throw new FormatException($"'{value}' is not a 64-character lowercase hex SHA-256 digest.");

    /// <summary>Parses a hex digest without throwing.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out Sha256Hash hash)
    {
        hash = default;

        if (value is null || value.Length != HexLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool isLowerHex = character is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isLowerHex)
            {
                return false;
            }
        }

        hash = new Sha256Hash(value);
        return true;
    }

    /// <summary>True when this digest has not been assigned.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Hex);

    /// <summary>The first eight characters, for logs and report tables.</summary>
    public string Abbreviated => IsEmpty ? string.Empty : Hex[..8];

    public override string ToString() => Hex;
}
