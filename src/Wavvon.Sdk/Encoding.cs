namespace Wavvon.Sdk;

internal static class Hex
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
#if NET9_0_OR_GREATER
        Convert.ToHexStringLower(bytes);
#else
        Convert.ToHexString(bytes).ToLowerInvariant();
#endif

    /// <summary>Decodes hex, or returns <see langword="null"/> when the text is not hex.</summary>
    public static byte[]? TryDecode(string hex)
    {
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static byte[] Decode(string hex, string what) =>
        TryDecode(hex) ?? throw new FormatException($"{what} is not a hex string");
}

internal static class Base64Url
{
    /// <summary>Decodes unpadded base64url, the form the hub shows secrets in.</summary>
    public static byte[] Decode(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                throw new FormatException("secret is not base64url");
            }
        }
        var b64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64 + new string('=', (4 - (b64.Length % 4)) % 4));
    }
}
