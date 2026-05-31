using dotenv.net;
using IrcBouncer;
using IrcBouncer.Config;

DotEnv.Load();

var config = new BouncerConfig
{
    UpstreamHost = Environment.GetEnvironmentVariable("IRC_HOST") ?? "localhost",
    UpstreamPort = int.TryParse(Environment.GetEnvironmentVariable("IRC_PORT"), out int port) ? port : 6667,
    ListenPort = int.TryParse(Environment.GetEnvironmentVariable("BOUNCER_PORT"), out int bp) ? bp : 6660,
    BufferSize = int.TryParse(Environment.GetEnvironmentVariable("BOUNCER_BUFFER_SIZE"), out int bs) ? bs : 500,
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Log($"IRC Bouncer starting — upstream {config.UpstreamHost}:{config.UpstreamPort}, listening on localhost:{config.ListenPort}");

var bouncer = new Bouncer(config);
try
{
    await bouncer.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Log("Shutting down.");
}

return;

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
