namespace Charter.Server;

/// <summary>
/// A fresh, per-session capability secret. The review server binds one to a <see cref="ReviewSession"/>
/// and only honours requests that present a matching key, so a plan served on loopback is not readable by
/// any other local process that merely guesses the port.
/// </summary>
/// <remarks>
/// STUB (TDD red). The next task implements the real logic; every behavioral member throws
/// <see cref="NotImplementedException"/> for now so the authored tests compile and fail.
/// </remarks>
public sealed class CapabilityKey
{
    /// <summary>The opaque secret value a caller must present to be granted access.</summary>
    public string Value => throw new NotImplementedException();

    /// <summary>Mint a fresh, cryptographically random capability key for a new session.</summary>
    public static CapabilityKey Create() => throw new NotImplementedException();

    /// <summary>
    /// True only when <paramref name="presented"/> is non-null/non-empty and equals this key's
    /// <see cref="Value"/> (constant-time compare in the real implementation).
    /// </summary>
    public bool Matches(string? presented) => throw new NotImplementedException();
}
