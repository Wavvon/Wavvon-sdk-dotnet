// Echoes every message in the channels it can read, and answers /echo.
//
//   WAVVON_HUB=https://hub.example  WAVVON_SEED=<64 hex chars>
//   WAVVON_INVITE=<code, first run only>  WAVVON_WEBHOOK_URL=https://bot.example/hook
//   PORT=8080 dotnet run --project samples/EchoBot
//
// The identity needs a role carrying apps.register to register /echo. The hub POSTs invocations
// to WAVVON_WEBHOOK_URL, which must reach PORT here.

using Wavvon.Sdk;
using Wavvon.Sdk.AspNetCore;
using Wavvon.Sdk.Generated.Channels.Item.Messages;
using Wavvon.Sdk.Generated.Me.App.Commands;
using Wavvon.Sdk.Generated.Me.App.Profile;

static string Env(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"set {name}");

using var identity = Identity.FromSeed(Env("WAVVON_SEED"));
using var client = new WavvonClient(new WavvonClientOptions
{
    HubUrl = new Uri(Env("WAVVON_HUB")),
    Identity = identity,
    InviteCode = Environment.GetEnvironmentVariable("WAVVON_INVITE"),
});
var me = (await client.LoginAsync()).CanonicalPubkey;
var info = await client.InfoAsync();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}");
await using var app = builder.Build();
app.MapWavvonDispatch("/hook", new DispatchHandlerOptions
{
    HubPublicKey = info.PublicKey!,
    OnSlashCommand = (inv, _) => Task.FromResult(new AppResponse
    {
        Reply = new AppReply(inv.ArgsRaw.Length > 0 ? inv.ArgsRaw : "(nothing to echo)"),
        Ephemeral = true,
    }),
});
await app.StartAsync();

await client.SetAppProfileAsync(new ProfilePutRequestBody { Name = "Echo", WebhookUrl = Env("WAVVON_WEBHOOK_URL") });
await client.SetAppCommandsAsync([new CommandsPutRequestBody_commands { Name = "echo", Description = "Repeat what you say, to you only", Args = "<text>" }]);

await using var events = client.Connect();
events.StateChanged += (_, state) => Console.WriteLine($"socket: {state}");
try
{
    await foreach (var e in events.ReadAllAsync(app.Lifetime.ApplicationStopping))
    {
        switch (e)
        {
            case UnknownEvent unknown:
                Console.WriteLine($"unmodelled frame: {unknown.RawType}");
                break;
            case MessageEvent { Message: var message } m
                when message.Sender != me && message.VisibleToPubkey is null && message.Content?.StartsWith('/') == false:
                await client.Rest.Channels[m.ChannelId].Messages.PostAsync(
                    new MessagesPostRequestBody { Content = $"echo: {message.Content}" });
                break;
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C.
}
