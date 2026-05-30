using DefinitelyHuman.Agent;
using DefinitelyHuman.Data;
using DefinitelyHuman.Irc;
using DefinitelyHuman.Web;
using dotenv.net;

DotEnv.Load();

await using var db = new ChattingContext();
db.Database.EnsureCreated();

string apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                ?? throw new InvalidOperationException("Set ANTHROPIC_API_KEY in .env or environment.");

string botNick = Environment.GetEnvironmentVariable("IRC_NICK") ?? "DefinitelyHuman";
string ircHost = Environment.GetEnvironmentVariable("IRC_HOST") ?? "localhost";
int ircPort = int.TryParse(Environment.GetEnvironmentVariable("IRC_PORT"), out int port) ? port : 6667;
string ircChannel = Environment.GetEnvironmentVariable("IRC_CHANNEL") ?? "#clankersunite";
string? ircPassword = Environment.GetEnvironmentVariable("IRC_PASSWORD");
if (string.IsNullOrWhiteSpace(ircPassword)) ircPassword = null;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Decision log: always captured in memory and shown on the dashboard's Reasoning page.
var agentLog = new AgentLog();
builder.Services.AddSingleton(agentLog);

// enableThinking costs extra (output) tokens on every glance — flip on for better answers,
// off to save tokens. logReasoning only echoes the decision log + thinking to the console;
// the dashboard log is captured regardless.
builder.Services.AddSingleton(new ChatAgent(apiKey, botNick, agentLog, enableThinking: false, logReasoning: false));
builder.Services.AddSingleton(new IrcBot(botNick, ircHost, ircPort, ircChannel, ircPassword));
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
