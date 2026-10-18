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
    /// <summary>The only challenge scheme this SDK signs besides the raw 32-byte fallback.</summary>
    public const string ChallengeScheme = "tagged-v1";

    /// <summary>The only length a raw (untagged) challenge is ever signed at.</summary>
    public const int RawChallengeLength = 32;

    private static readonly byte[] ChallengeTag = Encoding.UTF8.GetBytes("wavvon/auth-challenge/v1\0");

    /// <summary>
    /// The bytes a client signs to answer a challenge. Under <c>tagged-v1</c>: the domain prefix plus
    /// the challenge. With no scheme (an older hub): the raw challenge, and only at exactly 32 bytes,
    /// so this can never be used to sign arbitrary bytes. Any other scheme is refused, not guessed at.
    /// </summary>
    /// <exception cref="AuthSchemeException">Unknown scheme, or a raw challenge of the wrong length.</exception>
    public static byte[] ChallengeSigningBytes(ReadOnlySpan<byte> challenge, string? scheme)
    {
        if (scheme == ChallengeScheme)
        {
            return [.. ChallengeTag, .. challenge];
        }
        if (scheme is null)
        {
            if (challenge.Length != RawChallengeLength)
            {
                throw new AuthSchemeException(
                    $"refusing to sign a raw {challenge.Length}-byte challenge; only {RawChallengeLength} bytes are ever signed raw");
            }
            return challenge.ToArray();
        }
        throw new AuthSchemeException($"unknown auth challenge scheme \"{scheme}\"");
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
                var bytes = ChallengeSigningBytes(Hex.Decode(challenge.Challenge, "the challenge"), challenge.Scheme);
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

    private sealed record VerifyRequest(string PublicKey, string Challenge, string Signature, string? InviteCode);

    private sealed record VerifyAnswer(string Token, string? Scope, string? CanonicalPubkey);
}
