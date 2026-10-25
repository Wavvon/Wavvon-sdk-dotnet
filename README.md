# Wavvon.Sdk

Write programs that talk to a [Wavvon](https://github.com/Wavvon) hub in C#:
bots, apps with slash commands and buttons, event consumers, mini-app hosts,
and receivers for outgoing webhooks.

A program on Wavvon is a client like any other: an Ed25519 keypair, the same
challenge-response login, the same permissions. There is no bot account. What
makes a program an *app* is a role carrying `apps.register`. The design this
package implements is
[sdk.md](https://github.com/Wavvon/Wavvon-docs/blob/main/docs/sdk.md) (tier 1).

- .NET 8 and .NET 10. Nullable-annotated, async with `CancellationToken`
  throughout, `IAsyncEnumerable` for event streams and paging.
- Depends on `Microsoft.Kiota.Bundle` (the generated REST client) and
  `NSec.Cryptography` (Ed25519; the BCL has none). HKDF, HMAC-SHA256 and
  WebSockets come from the BCL.
- `Wavvon.Sdk.AspNetCore` adds one minimal-API extension for the webhook.
- Apache-2.0, so a closed-source program can use it.

## Install

```sh
dotnet add package Wavvon.Sdk
dotnet add package Wavvon.Sdk.AspNetCore   # only for MapWavvonDispatch
```

## Quick start

```csharp
using Wavvon.Sdk;
using Wavvon.Sdk.AspNetCore;
using Wavvon.Sdk.Generated.Models;
using Wavvon.Sdk.Generated.Me.App.Profile;

// A 32-byte Ed25519 seed, hex. It never leaves the process.
using var identity = Identity.FromSeed(Environment.GetEnvironmentVariable("WAVVON_SEED")!);
using var client = new WavvonClient(new WavvonClientOptions
{
    HubUrl = new Uri("https://hub.example"),
    Identity = identity,
    InviteCode = Environment.GetEnvironmentVariable("WAVVON_INVITE"), // first run, invite-only hub
});
var hub = await client.InfoAsync();

var app = WebApplication.CreateSlimBuilder(args).Build();
app.MapWavvonDispatch("/hook", new DispatchHandlerOptions
{
    HubPublicKey = hub.PublicKey!,
    OnSlashCommand = (inv, ct) => Task.FromResult(new AppResponse { Reply = new AppReply($"you said: {inv.ArgsRaw}") }),
});
await app.StartAsync();

await client.SetAppProfileAsync(new ProfilePutRequestBody { Name = "Echo", WebhookUrl = "https://bot.example/hook" });
await client.SetAppCommandsAsync([new AppCommandDef { Name = "echo", Description = "Repeat after me" }]);

await using var events = client.Connect();
await foreach (var e in events.ReadAllAsync())
{
    switch (e)
    {
        case MessageEvent m: Console.WriteLine($"{m.Message.Sender}: {m.Message.Content}"); break;
        case UnknownEvent u: Console.WriteLine($"new frame type: {u.RawType}"); break;
    }
}
```

A fuller one is [samples/EchoBot](samples/EchoBot/Program.cs).

## What is in it

| Piece | Entry point |
|---|---|
| Login (challenge, sign, verify) that renews itself | `WavvonClient`, `WavvonAuth.AuthenticateAsync`, `Identity` |
| Every REST route, generated from `openapi.yaml` by Kiota | `client.Rest` (for example `client.Rest.Channels[id].Messages.PostAsync(...)`) |
| Any request through the session | `client.SendAsync(HttpRequestMessage)` |
| Retry with backoff on 429, 5xx, timeouts | built into both of the above; `RetryPolicy` |
| Capability checks | `HasCapabilityAsync`, `RequireCapabilityAsync` |
| Keyset paging to the end of a list | `Pager.PaginateAsync` |
| WebSocket events with resume after reconnect | `client.Connect()` -> `EventConnection.ReadAllAsync` |
| HTTP polling instead of a socket | `PollEventsAsync(cursor)` |
| App registration | `SetAppProfileAsync`, `SetAppCommandsAsync`, `SetAppSubscriptionsAsync` |
| Slash command and button webhooks | `DispatchHandler`, `WebhookVerifier.VerifyDispatch`, `MapWavvonDispatch` |
| Outgoing webhook receiver | `WebhookVerifier.VerifyOutgoingWebhook` |
| Mini-app host | `EventConnection.AnnounceAppAsync`, `DismissAppAsync`, `SendMiniAppMessageAsync` |

Request and response bodies of `client.Rest` are Kiota's generated classes,
named after the spec (`ProfilePutRequestBody`, `AppCommandDef`).
App registration takes them directly, so a field the spec adds is usable the
day it is regenerated.

The verifiers take headers as a `Func<string, string?>` and the body as raw
bytes, so they work with any web framework: in ASP.NET Core,
`name => request.Headers[name]`.

## Capabilities each feature needs

Hubs say what they support in `GET /info` `capabilities`. The SDK checks those
strings and never compares version numbers. A feature the hub does not
advertise throws `UnsupportedByHubException`.

| Feature | Capability |
|---|---|
| `SetAppProfileAsync`, `SetAppCommandsAsync`, `SetAppSubscriptionsAsync`, embeds, game cards | `apps.register` |
| `VerifyDispatch` / `VerifyOutgoingWebhook` (they require `X-Wavvon-Signature-V1`) | `webhooks.signedtimestamp` |
| Paging `GET /users`, `/conversations/{id}/messages`, `/admin/reports` | `list.cursor` |
| Paging bans, mutes, invites, pending, conversations, pins, polls | `list.cursor.lists` |
| `PingMessage` / `PongEvent` over the socket | `ws.ping` |

Login, REST, the socket and polling need no capability.

## Behaviour you can rely on

- **A transient failure is never an answer.** 429, 5xx, timeouts and network
  errors are retried with capped exponential backoff and full jitter. If the
  budget runs out you get `HubUnavailableException`, not an empty list or a
  404. A definitive refusal is `HubException` with the hub's own words. Set
  `RetryOptions.MaxAttempts = int.MaxValue` to wait forever.
- **Sessions renew themselves.** A 401 triggers one fresh login (shared by
  concurrent callers) and a retry. `client.StateChanged` and
  `events.StateChanged` show when it is happening.
- **Unknown is delivered.** A socket frame or dispatch type this version does
  not model reaches you as `UnknownEvent` / `UnknownDispatch` with the raw
  payload.
- **Missed events are replayed.** The event connection remembers the last
  `hub_event` seq (`LastSeq`) and asks the hub to resume from it after every
  reconnect, dropping anything the replay repeats. If the gap is older than
  the hub keeps (72 hours) you get `ReplayUnavailableEvent`. Register the app
  profile before `Connect()`: the hub only pushes and replays events on a
  socket opened by a registered app.
- **The seed stays with you.** No call sends a seed or private key anywhere.

## Conformance

Every signed byte sequence is checked against the shared wire vectors in
Wavvon-server (`crates/identity/wire-vectors.json`, format 1): the auth
challenge, the Ed25519 dispatch signature, and the outgoing-webhook HKDF +
HMAC. CI reads them from Wavvon-server at commit
`eb11d75657ee8362dd42a7982fc5b13a3dcc4e68`.

The REST client was generated from Wavvon-docs at the commit in
[src/Wavvon.Sdk/Generated/SPEC_COMMIT](src/Wavvon.Sdk/Generated/SPEC_COMMIT).

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
