// Smoke test against a real hub, through the public API only.
//
// Against a running hub:
//   WAVVON_SMOKE_HUB      hub URL, e.g. http://localhost:3600
//   WAVVON_SMOKE_INVITE   an invite whose role grants apps.register
//   WAVVON_SMOKE_CHANNEL  optional channel id; default: the first non-category channel
//   WAVVON_SMOKE_WEBHOOK_HOST  host the hub can reach this process at (default 127.0.0.1)
//
// Without WAVVON_SMOKE_HUB it boots one: a copy of the hub binary (WAVVON_HUB_BIN, default
// ../../server/target/debug/wavvon-hub[.exe] from the repo root) on a fresh database created in
// the PostgreSQL docker container WAVVON_PG_CONTAINER (default wavvon-pg, reached at WAVVON_PG,
// default postgres://postgres:postgres@localhost:5432), and sets up a role, invite and channel as
// its owner first.
//
// Authenticates as a fresh identity, posts a message, registers a slash command, invokes it, and
// checks that the verified invocation's reply lands in the channel.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Wavvon.Sdk;
using Wavvon.Sdk.AspNetCore;
using Wavvon.Sdk.Generated.Channels;
using Wavvon.Sdk.Generated.Channels.Item.Messages;
using Wavvon.Sdk.Generated.Invites;
using Wavvon.Sdk.Generated.Me.App.Commands;
using Wavvon.Sdk.Generated.Me.App.Profile;
using Wavvon.Sdk.Generated.Roles;

static void Step(string s) => Console.WriteLine($"smoke: {s}");

var hubUrl = Environment.GetEnvironmentVariable("WAVVON_SMOKE_HUB");
if (hubUrl is not null)
{
    var invite = Environment.GetEnvironmentVariable("WAVVON_SMOKE_INVITE");
    if (invite is null)
    {
        Console.Error.WriteLine("set WAVVON_SMOKE_INVITE too");
        return 2;
    }
    return await Smoke.RunAsync(new Uri(hubUrl), invite, Environment.GetEnvironmentVariable("WAVVON_SMOKE_CHANNEL"), Step);
}
return await LocalHub.RunAsync(Step);

internal static class Smoke
{
    public static async Task<int> RunAsync(Uri hubUrl, string inviteCode, string? channelId, Action<string> step)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        using var identity = Identity.FromSeed(RandomNumberGenerator.GetBytes(32));
        using var client = new WavvonClient(new WavvonClientOptions
        {
            HubUrl = hubUrl,
            Identity = identity,
            InviteCode = inviteCode,
            Retry = new RetryOptions { OnRetry = i => step($"retrying after {i.Status?.ToString() ?? "network error"} (attempt {i.Attempt})") },
        });
        try
        {
            var session = await client.LoginAsync(ct);
            step($"authenticated as {session.CanonicalPubkey} ({session.Scope})");
            await client.RequireCapabilityAsync("apps.register", ct);
            await client.RequireCapabilityAsync("webhooks.signedtimestamp", ct);
            var info = await client.InfoAsync(cancellationToken: ct);

            SlashInvocation? invocation = null;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            app.MapWavvonDispatch("/hook", new DispatchHandlerOptions
            {
                HubPublicKey = info.PublicKey!,
                OnSlashCommand = (inv, _) =>
                {
                    invocation = inv;
                    return Task.FromResult(new AppResponse { Reply = new AppReply($"pong: {inv.ArgsRaw}") });
                },
            });
            await app.StartAsync(ct);
            var port = new Uri(app.Urls.First()).Port;
            var webhookUrl = $"http://{Environment.GetEnvironmentVariable("WAVVON_SMOKE_WEBHOOK_HOST") ?? "127.0.0.1"}:{port}/hook";

            await client.SetAppProfileAsync(new ProfilePutRequestBody { Name = "SDK smoke", Description = "Wavvon.Sdk smoke test", WebhookUrl = webhookUrl }, ct);
            await client.SetAppCommandsAsync([new CommandsPutRequestBody_commands { Name = "sdksmoke", Description = "Answers pong", CooldownSeconds = 0 }], ct);
            step($"registered /sdksmoke -> {webhookUrl}");

            // After the profile, so the hub treats this socket as an app's.
            await using var events = client.Connect();
            var messages = new ConcurrentQueue<MessageEvent>();
            var unknown = new ConcurrentDictionary<string, bool>();
            var opened = new TaskCompletionSource();
            var reader = Task.Run(async () =>
            {
                await foreach (var e in events.ReadAllAsync(ct))
                {
                    switch (e)
                    {
                        case HelloEvent:
                            opened.TrySetResult();
                            break;
                        case MessageEvent m:
                            messages.Enqueue(m);
                            break;
                        case UnknownEvent u:
                            unknown[u.RawType] = true;
                            break;
                    }
                }
            }, ct);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            step("socket open");

            if (channelId is null)
            {
                var channels = await client.Rest.Channels.GetAsync(cancellationToken: ct) ?? [];
                channelId = channels.FirstOrDefault(c => c.IsCategory != true)?.Id
                    ?? throw new InvalidOperationException("no channel to post in");
            }

            async Task<MessageEvent> WaitForAsync(Func<MessageEvent, bool> predicate, string what)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    var hit = messages.FirstOrDefault(predicate);
                    if (hit is not null)
                    {
                        return hit;
                    }
                    await Task.Delay(50, ct);
                }
                throw new TimeoutException($"timed out waiting for {what}");
            }

            async Task PostAsync(string content) =>
                await client.Rest.Channels[channelId].Messages.PostAsync(new MessagesPostRequestBody { Content = content }, cancellationToken: ct);

            var text = $"hello from Wavvon.Sdk {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            await PostAsync(text);
            var own = await WaitForAsync(m => m.Message.Content == text, "our own message over the socket");
            if (own.Message.CreatedAt is null)
            {
                throw new InvalidOperationException("message created_at did not parse");
            }
            step($"posted a message and saw it come back (created_at {own.Message.CreatedAt})");

            await PostAsync("/sdksmoke ping");
            var reply = await WaitForAsync(m => m.Message.Content == "pong: ping", "the slash command reply");
            if (invocation is not { Command: "sdksmoke", ArgsRaw: "ping" })
            {
                throw new InvalidOperationException($"odd invocation {invocation}");
            }
            if (reply.Message.Sender != session.CanonicalPubkey)
            {
                throw new InvalidOperationException($"reply sent by {reply.Message.Sender}");
            }
            step("verified the invocation and the reply landed");

            if (!unknown.IsEmpty)
            {
                step($"frames this SDK does not model (delivered as unknown): {string.Join(", ", unknown.Keys)}");
            }
            await events.DisposeAsync();
            await reader;
            await app.StopAsync(ct);
            step("PASSED");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"smoke: FAILED: {e}");
            return 1;
        }
    }
}

internal static class LocalHub
{
    public static async Task<int> RunAsync(Action<string> step)
    {
        var root = RepoRoot();
        var exe = OperatingSystem.IsWindows() ? "wavvon-hub.exe" : "wavvon-hub";
        var bin = Environment.GetEnvironmentVariable("WAVVON_HUB_BIN") ?? Path.GetFullPath(Path.Combine(root, "../../server/target/debug", exe));
        if (!File.Exists(bin))
        {
            Console.Error.WriteLine($"no hub binary at {bin}; build Wavvon-server (cargo build -p wavvon-hub) or set WAVVON_HUB_BIN");
            return 2;
        }

        var pg = Environment.GetEnvironmentVariable("WAVVON_PG") ?? "postgres://postgres:postgres@localhost:5432";
        var container = Environment.GetEnvironmentVariable("WAVVON_PG_CONTAINER") ?? "wavvon-pg";
        const string db = "wavvon_sdk_dotnet_smoke";
        Psql(container, $"DROP DATABASE IF EXISTS {db}");
        Psql(container, $"CREATE DATABASE {db}");

        // A copy, so a running smoke never locks the build output.
        var cwd = Directory.CreateTempSubdirectory("wavvon-sdk-dotnet-smoke-").FullName;
        var hubBin = Path.Combine(cwd, exe);
        File.Copy(bin, hubBin);

        var httpPort = 3900 + Random.Shared.Next(500);
        var hubUrl = new Uri($"http://localhost:{httpPort}");
        using var ownerIdentity = Identity.FromSeed(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(hubBin)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment =
            {
                ["WAVVON_DATABASE_URL"] = $"{pg}/{db}",
                // The webhook receiver listens on loopback.
                ["WAVVON_ALLOW_PRIVATE_WEBHOOKS"] = "true",
                ["WAVVON_HTTP_PORT"] = httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["WAVVON_VOICE_UDP_PORT"] = (httpPort + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["WAVVON_PUBLIC_URL"] = hubUrl.AbsoluteUri.TrimEnd('/'),
                ["WAVVON_OWNER_PUBKEY"] = ownerIdentity.PublicKey,
                ["WAVVON_WEB_CLIENT_DIR"] = "",
            },
        };
        var log = new ConcurrentQueue<string>();
        using var hub = Process.Start(start) ?? throw new InvalidOperationException("could not start the hub");
        hub.OutputDataReceived += (_, e) => log.Enqueue(e.Data ?? "");
        hub.ErrorDataReceived += (_, e) => log.Enqueue(e.Data ?? "");
        hub.BeginOutputReadLine();
        hub.BeginErrorReadLine();

        var code = 1;
        try
        {
            using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
            {
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (true)
                {
                    if (hub.HasExited)
                    {
                        throw new InvalidOperationException($"hub exited ({hub.ExitCode})\n{string.Join('\n', log)}");
                    }
                    try
                    {
                        if ((await probe.GetAsync(new Uri(hubUrl, "info"))).IsSuccessStatusCode)
                        {
                            break;
                        }
                    }
                    catch (HttpRequestException)
                    {
                        // Not up yet.
                    }
                    catch (TaskCanceledException)
                    {
                        // Not up yet.
                    }
                    if (DateTime.UtcNow > deadline)
                    {
                        throw new TimeoutException($"hub never answered\n{string.Join('\n', log)}");
                    }
                    await Task.Delay(400);
                }
            }
            step($"local hub up at {hubUrl}");

            using var owner = new WavvonClient(new WavvonClientOptions { HubUrl = hubUrl, Identity = ownerIdentity });
            var role = await owner.Rest.Roles.PostAsync(new RolesPostRequestBody { Name = "Apps", Priority = 10, Permissions = ["apps.register"] });
            var invite = await owner.Rest.Invites.PostAsync(new InvitesPostRequestBody { MaxUses = 1, GrantRoleId = role!.Id });
            var channel = await owner.Rest.Channels.PostAsync(new ChannelsPostRequestBody { Name = "sdk-smoke" });
            step("role, invite and channel ready");

            code = await Smoke.RunAsync(hubUrl, invite!.Code!, channel!.Id, step);
            if (code != 0 && Environment.GetEnvironmentVariable("WAVVON_SMOKE_VERBOSE") is not null)
            {
                Console.WriteLine(string.Join('\n', log));
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
        }
        finally
        {
            hub.Kill(entireProcessTree: true);
            await hub.WaitForExitAsync();
            try
            {
                Directory.Delete(cwd, recursive: true);
            }
            catch (IOException)
            {
                // Windows may hold a file a moment longer; it is a temp dir.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
        return code;
    }

    private static void Psql(string container, string sql)
    {
        using var p = Process.Start(new ProcessStartInfo("docker", ["exec", container, "psql", "-U", "postgres", "-c", sql])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"psql failed: {error}");
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wavvon.Sdk.slnx")))
            {
                return dir.FullName;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}
