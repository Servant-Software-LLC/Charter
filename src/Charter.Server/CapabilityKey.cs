using System.Security.Cryptography;
using System.Text;

namespace Charter.Server;

/// <summary>
/// A fresh, per-session capability secret. The review server binds one to a <see cref="ReviewSession"/>
/// and only honours requests that present a matching key, so a plan served on loopback is not readable by
/// any other local process that merely guesses the port.
/// </summary>
public sealed class CapabilityKey
{
    private const int SecretByteLength = 32;

    private CapabilityKey(string value) => Value = value;

    /// <summary>The opaque secret value a caller must present to be granted access.</summary>
    public string Value { get; }

    /// <summary>
    /// Mint a fresh, cryptographically random capability key for a new session. The value is URL-safe
    /// (lowercase hex) so it can travel as the <c>?key=</c> query-string parameter without escaping.
    /// </summary>
    public static CapabilityKey Create()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretByteLength);
        return new CapabilityKey(Convert.ToHexString(secret).ToLowerInvariant());
    }

    /// <summary>
    /// True only when <paramref name="presented"/> is non-null/non-empty and equals this key's
    /// <see cref="Value"/>. The comparison is constant-time to avoid leaking the secret byte-by-byte.
    /// </summary>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(Value);
        var actual = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
