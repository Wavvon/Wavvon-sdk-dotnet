using NSec.Cryptography;

namespace Wavvon.Sdk;

/// <summary>
/// The Ed25519 keypair a program holds. Built from a 32-byte seed that the operator keeps in a
/// file or environment variable; the seed never leaves this object and no SDK call sends it.
/// </summary>
public sealed class Identity : IDisposable
{
    private readonly Key _key;

    private Identity(Key key)
    {
        _key = key;
        PublicKey = Hex.Encode(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    /// <summary>The Ed25519 public key, lowercase hex. This is the program's identity on every hub.</summary>
    public string PublicKey { get; }

    /// <summary>Builds an identity from a 32-byte Ed25519 seed.</summary>
    /// <exception cref="ArgumentException">The seed is not 32 bytes.</exception>
    public static Identity FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
        {
            throw new ArgumentException("an Ed25519 seed is 32 bytes", nameof(seed));
        }
        return new Identity(Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey));
    }

    /// <summary>Builds an identity from a 32-byte Ed25519 seed written as 64 hex characters.</summary>
    /// <exception cref="ArgumentException">The text is not 64 hex characters.</exception>
    public static Identity FromSeed(string seedHex)
    {
        ArgumentNullException.ThrowIfNull(seedHex);
        var seed = Hex.TryDecode(seedHex.Trim()) ?? throw new ArgumentException("the seed is not hex", nameof(seedHex));
        return FromSeed(seed);
    }

    /// <summary>Signs <paramref name="message"/> and returns the 64-byte signature.</summary>
    public byte[] Sign(ReadOnlySpan<byte> message) => SignatureAlgorithm.Ed25519.Sign(_key, message);

    /// <summary>Checks an Ed25519 signature against a hex public key. Malformed input verifies as false.</summary>
    public static bool Verify(string publicKeyHex, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        var raw = Hex.TryDecode(publicKeyHex);
        return raw is not null
            && NSec.Cryptography.PublicKey.TryImport(SignatureAlgorithm.Ed25519, raw, KeyBlobFormat.RawPublicKey, out var key)
            && key is not null
            && SignatureAlgorithm.Ed25519.Verify(key, message, signature);
    }

    /// <inheritdoc/>
    public void Dispose() => _key.Dispose();
}
