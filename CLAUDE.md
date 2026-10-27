# CLAUDE.md

Guidance for working in this repo: `Wavvon.Sdk`, the C#/.NET SDK for programs
(bots and apps) that talk to a Wavvon hub. The design is
[sdk.md](https://github.com/Wavvon/Wavvon-docs/blob/main/docs/sdk.md); this is
its tier 1. Hub code lives in
[Wavvon-server](https://github.com/Wavvon/Wavvon-server), the REST spec and
protocol docs in [Wavvon-docs](https://github.com/Wavvon/Wavvon-docs). The
TypeScript SDK, [Wavvon-sdk-js](https://github.com/Wavvon/Wavvon-sdk-js), is the
reference for behaviour; this one mirrors its surface in idiomatic C#.

## Commands

```sh
sh scripts/check          # format check + warnings-as-errors build + tests. Must pass before a commit.
dotnet test --solution Wavvon.Sdk.slnx
sh scripts/generate [<Wavvon-docs checkout>] [<git-ref>]   # regenerate the REST client
dotnet run --project samples/Smoke                          # real hub binary, see below
```

.NET 10 SDK (see `global.json`; tests run on Microsoft.Testing.Platform).
Libraries target net8.0 and net10.0. The test project targets both with
`RollForward=Major`, so the net8.0 build also runs where only .NET 10 is
installed.

Code comments in English, only where the why is not obvious; XML doc comments
on every public member (the build fails without them). No comments in GitHub
Actions workflow files: explain choices in the commit message.

## Layout

1. **Generated REST** - `src/Wavvon.Sdk/Generated/`, Kiota output from
   Wavvon-docs `openapi.yaml`, produced by `scripts/generate` (Kiota is a local
   dotnet tool, `dotnet-tools.json`). Committed. `SPEC_COMMIT` records the docs
   commit. The spec is used as is: a gap is fixed in Wavvon-docs, never patched
   here. Kiota reads an integer without `format: int64` as int32, so an i64 the
   hub sends needs that format in the spec. Never edit the generated code by
   hand.
2. **Hand-written core** - `src/Wavvon.Sdk/*.cs`:
   - `Identity.cs` the Ed25519 keypair (NSec); `Auth.cs` challenge rules and
     login; `Retry.cs` backoff and the transient rule; `WavvonClient.cs` the
     session (one `HttpMessageHandler` that adds the bearer, retries and logs
     in again on 401, under both Kiota and `SendAsync`), capabilities, app
     registration, polling; `Pager.cs`; `Events.cs` frame types and
     `WsEvent.Parse`; `EventConnection.cs` the reconnecting socket;
     `Webhooks.cs` reply types and `WebhookVerifier`; `DispatchHandler.cs`
     the framework-agnostic webhook handler.
   - `src/Wavvon.Sdk.AspNetCore` - `MapWavvonDispatch`, nothing else.
3. **Tests** - `test/Wavvon.Sdk.Tests` (xUnit v3). HTTP is faked with an
   `HttpMessageHandler`; the event connection runs over real WebSockets on
   loopback TCP. `ConformanceTests` reads Wavvon-server
   `crates/identity/wire-vectors.json` (`WAVVON_WIRE_VECTORS`, relative to the
   repo root, default `../../server/...`). Strings starting with `@` refer to
   `fixtures`. It is never copied into this repo. Missing file: loud skip
   locally, failure when `CI` is set.

## Rules the SDK must keep (sdk.md §5, §6)

- **Capabilities, never versions.** Gate features on `GET /info`
  `capabilities` and throw `UnsupportedByHubException`. Never compare `version`.
- **429 is "not now", never "no".** Retry 429/5xx/timeouts/network errors with
  capped exponential backoff and jitter. On exhaustion throw
  `HubUnavailableException`; never turn a transient failure into an empty
  list, "not found", or any other definitive answer. A pager page that fails
  must throw, not end the list. Only transport exceptions count as transient:
  `AuthSchemeException` and the like propagate at once.
- **Unknown is loud.** Unknown socket frames and dispatch types are delivered
  as `UnknownEvent` / `UnknownDispatch` with the raw payload, never dropped. A
  known type whose shape does not parse is also delivered as unknown.
- **Re-auth on its own**, with the state observable (`StateChanged`).
  Concurrent callers share one login; one caller cancelling does not cancel it.
- **Auth challenge**: `scheme: "tagged-v1"` -> sign
  `"wavvon/auth-challenge/v1\0" || challenge`; a missing or unknown scheme ->
  `AuthSchemeException`, never a raw signature (hubs refuse it since
  Wavvon-server#199).
- **Never send the seed** or any private key anywhere. Never invent a wire
  format: signed bytes come from wire-format.md and the vectors; a new one is
  a server-side change first. Never model a bot as an account or send
  `is_bot`. Tier 1 is community-axis only (no DMs E2E, pairing, home hub).

Code is authoritative over docs. When they disagree, follow the hub code and
report the doc bug to Wavvon-docs.

## Facts verified against the hub that the docs get wrong or omit

- The challenge is hex. The hub removes the pending challenge when verify
  reads it, so a transient verify failure restarts from a fresh challenge
  (the whole challenge+verify pair is one retry unit).
- `resume` and `hub_event` push only work on a socket whose identity had an
  app profile **when it connected**. Register first, then `Connect()`.
- Replay can re-deliver: live events that arrive during a replay are flushed
  after it, so drop `seq <= LastSeq`.
- The app side of a mini-app sends `app_announce` / `app_dismiss` and talks to
  players with `mini_app_message` + `to_pubkey`; players arrive with
  `from_pubkey`. `app_join` is answered by the hub, not the app.
- Paging: a bare array plus `limit` (default 100, max 500) and `cursor` = key
  of the previous page's last row; the key differs per endpoint (`code`,
  `public_key`, `id`), hence the key selector. A hub that ignores the cursor
  is detected. Channel messages too since Wavvon-server#184.
- Outgoing-webhook HKDF input is the **decoded** secret bytes (the admin sees
  base64url without padding); the HMAC key is the 32 HKDF bytes, not hex.
- `GET /me/events` reads the audit log (Wavvon-server#183): `hub_event`
  envelopes after a `seq` cursor, 410 outside the 72-hour window, no ack.

## Smoke test

`samples/Smoke` runs against a real hub. With `WAVVON_SMOKE_HUB` (+
`WAVVON_SMOKE_INVITE` for an invite whose role grants `apps.register`,
optional `WAVVON_SMOKE_CHANNEL`) it uses that hub. Without it, it copies the
hub binary (`WAVVON_HUB_BIN`, default a sibling Wavvon-server checkout's
`target/debug/wavvon-hub`) to a temp dir so the build output is never locked,
boots it on a fresh database in a PostgreSQL docker container
(`WAVVON_PG_CONTAINER`, default `wavvon-pg`) with
`WAVVON_ALLOW_PRIVATE_WEBHOOKS=true`, sets up a role, invite and channel as
owner, runs the smoke and kills only that hub. In-process tests have missed
every cross-process bug this project has had: run it when touching auth, the
socket or webhooks.

## Git

Work on `develop`; `main` is for releases. Commit in logical steps.
