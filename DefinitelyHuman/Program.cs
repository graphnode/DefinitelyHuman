using DefinitelyHuman.Agent;
using DefinitelyHuman.Data;
using DefinitelyHuman.Irc;
using DefinitelyHuman.Web;
using dotenv.net;

DotEnv.Load();

await using var db = new ChattingContext();
db.Database.EnsureCreated();

var ircBotOptions = new IrcBotOptions
{
    Nick = Environment.GetEnvironmentVariable("IRC_NICK") ?? "DefinitelyHuman",
    Host = Environment.GetEnvironmentVariable("IRC_HOST") ?? "localhost",
    Port = int.TryParse(Environment.GetEnvironmentVariable("IRC_PORT"), out int port) ? port : 6667,
    Channel = Environment.GetEnvironmentVariable("IRC_CHANNEL") ?? "#clankersunite",
    Password = Environment.GetEnvironmentVariable("IRC_PASSWORD")
};

var chatAgentOptions = new ChatAgentOptions
{
    ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? throw new InvalidOperationException("Set ANTHROPIC_API_KEY in .env or environment."),
    Model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-haiku-4-5-20251001",
    Nick = ircBotOptions.Nick,
};

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging(b =>
{
    b.ClearProviders();
    b.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "[HH:mm:ss] ";
    });
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Decision log: always captured in memory and shown on the dashboard's Reasoning page.
var agentLog = new AgentLog();
builder.Services.AddSingleton(agentLog);

// enableThinking costs extra (output) tokens on every glance — flip on for better answers,
// off to save tokens. logReasoning only echoes the decision log + thinking to the console;
// the dashboard log is captured regardless.
builder.Services.AddSingleton<ChatAgent>(sp => new ChatAgent(chatAgentOptions, sp.GetRequiredService<AgentLog>(), sp.GetRequiredService<ILogger<ChatAgent>>()));
builder.Services.AddSingleton<IrcBot>(sp => new IrcBot(ircBotOptions, sp.GetRequiredService<ILogger<IrcBot>>()));

builder.Services.AddHostedService<IrcBotService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
