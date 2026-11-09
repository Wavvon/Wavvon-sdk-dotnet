using System.Net.Http.Json;
using System.Text;

namespace Wavvon.Sdk;

/// <summary>A session the hub granted.</summary>
/// <param name="Token">The bearer token for REST and the WebSocket.</param>
/// <param name="Scope"><c>member</c>, or <c>lobby</c> on a hub that holds newcomers back.</param>
/// <param name="CanonicalPubkey">The identity the hub attributes this session's actions to.</param>
public sealed record Session(string Token, string Scope, string CanonicalPubkey);

/// <summary>Challenge-response login (wire-format.md, "Auth challenge answer").</summary>
public static class WavvonAuth
{
    /// <summary>The challenge scheme older hubs name: the answer names no server.</summary>
    public const string ChallengeScheme = "tagged-v1";

    /// <summary>The challenge scheme current hubs and farms name: the answer is bound to their key.</summary>
    public const string BoundChallengeScheme = "tagged-v2";

    private const int ChallengeLength = 32;

    private static readonly byte[] ChallengeTag = Encoding.UTF8.GetBytes("wavvon/auth-challenge/v1\0");

    private static readonly byte[] BoundChallengeTag = Encoding.UTF8.GetBytes("wavvon/auth-challenge/v2\0");

    /// <summary>
    /// The bytes a client signs to answer a challenge. Under <c>tagged-v2</c>: the v2 tag, the server
    /// public key (u32 little-endian length, lowercase hex), then the challenge. Under <c>tagged-v1</c>:
    /// the v1 tag plus the challenge. Any other scheme, or none, is refused rather than signed untagged.
    /// </summary>
    /// <param name="challenge">The 32 decoded challenge bytes.</param>
    /// <param name="scheme">The scheme the challenge response named.</param>
    /// <param name="serverPubkey">
    /// Under <c>tagged-v2</c>, the key the server's own <c>GET /info</c> presented, never one from the
    /// challenge response: otherwise a hostile hub could relay another hub's challenge and sign in there.
    /// </param>
    /// <exception cref="AuthSchemeException">A missing or unknown scheme, or a challenge that is not 32 bytes.</exception>
    public static byte[] ChallengeSigningBytes(ReadOnlySpan<byte> challenge, string? scheme, string? serverPubkey = null)
    {
        if (scheme is not (ChallengeScheme or BoundChallengeScheme))
        {
            throw new AuthSchemeException(scheme is null
                ? "the hub named no auth challenge scheme; untagged challenges are not signed"
                : $"unknown auth challenge scheme \"{scheme}\"");
        }
        if (challenge.Length != ChallengeLength)
        {
            throw new AuthSchemeException($"auth challenge is {challenge.Length} bytes, expected {ChallengeLength}");
        }
        if (scheme == ChallengeScheme)
        {
            return [.. ChallengeTag, .. challenge];
        }
        ArgumentException.ThrowIfNullOrEmpty(serverPubkey);
        var key = Encoding.UTF8.GetBytes(serverPubkey);
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)key.Length);
        return [.. BoundChallengeTag, .. length, .. key, .. challenge];
    }

    /// <summary>
    /// Challenge, sign, verify. A transient failure at either step restarts from a fresh challenge,
    /// because the hub drops a pending challenge as soon as verify reads it.
    /// </summary>
    /// <param name="http">Sends the two requests; no bearer is added.</param>
    /// <param name="hubUrl">The hub's base URL.</param>
    /// <param name="identity">Signs the challenge.</param>
    /// <param name="inviteCode">Needed once, on an <c>invite_only</c> hub, to be admitted. Ignored for members.</param>
    /// <param name="retry">The retry budget; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the login.</param>
    /// <exception cref="HubException">The hub refused the login.</exception>
    /// <exception cref="HubUnavailableException">The hub stayed unreachable.</exception>
    public static async Task<Session> AuthenticateAsync(
        HttpMessageInvoker http,
        Uri hubUrl,
        Identity identity,
        string? inviteCode = null,
        RetryOptions? retry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(hubUrl);
        ArgumentNullException.ThrowIfNull(identity);

        Task<HttpResponseMessage> PostAsync<T>(string path, T body, CancellationToken ct) =>
            http.SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(hubUrl, path)) { Content = WavvonJson.Content(body) }, ct);

        string? serverPubkey = null;
        using var response = await RetryPolicy.SendAsync(
            async ct =>
            {
                var challengeResponse = await PostAsync("auth/challenge", new { public_key = identity.PublicKey }, ct).ConfigureAwait(false);
                if (!challengeResponse.IsSuccessStatusCode)
                {
                    return challengeResponse;
                }
                ChallengeAnswer challenge;
                using (challengeResponse)
                {
                    challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeAnswer>(WavvonJson.Options, ct).ConfigureAwait(false)
                        ?? throw new HubException(challengeResponse.StatusCode, "empty challenge");
                }
                if (challenge.Scheme == BoundChallengeScheme && serverPubkey is null)
                {
                    var infoResponse = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(hubUrl, "info")), ct).ConfigureAwait(false);
                    if (!infoResponse.IsSuccessStatusCode)
                    {
                        return infoResponse;
                    }
                    using (infoResponse)
                    {
                        var info = await infoResponse.Content.ReadFromJsonAsync<ServerInfo>(WavvonJson.Options, ct).ConfigureAwait(false);
                        serverPubkey = info?.PublicKey ?? throw new HubException(infoResponse.StatusCode, "info names no public_key");
                    }
                }
                var bytes = ChallengeSigningBytes(Hex.Decode(challenge.Challenge, "the challenge"), challenge.Scheme, serverPubkey);
                return await PostAsync(
                    "auth/verify",
                    new VerifyRequest(identity.PublicKey, challenge.Challenge, Hex.Encode(identity.Sign(bytes)), inviteCode),
                    ct).ConfigureAwait(false);
            },
            retry,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HubException(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        var granted = await response.Content.ReadFromJsonAsync<VerifyAnswer>(WavvonJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new HubException(response.StatusCode, "empty verify answer");
        return new Session(
            granted.Token,
            granted.Scope ?? "member",
            string.IsNullOrEmpty(granted.CanonicalPubkey) ? identity.PublicKey : granted.CanonicalPubkey);
    }

    private sealed record ChallengeAnswer(string Challenge, string? Scheme);

    private sealed record ServerInfo(string? PublicKey);

    private sealed record VerifyRequest(string PublicKey, string Challenge, string Signature, string? InviteCode);

    private sealed record VerifyAnswer(string Token, string? Scope, string? CanonicalPubkey);
}
