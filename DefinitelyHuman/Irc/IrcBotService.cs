using DefinitelyHuman.Agent;
using DefinitelyHuman.Utilities;

namespace DefinitelyHuman.Irc;

public class IrcBotService(IrcBot bot, ChatAgent agent, LinkPreviewService previews) : BackgroundService
{
    // How long a glance waits for the previews of links it hasn't seen yet.
    private static readonly TimeSpan LinkPreviewWait = TimeSpan.FromSeconds(5);

    // How many recent messages a single glance reads at most (older overflow is summarized).
    private const int MaxBacklog = 150;

    // How many already-read lines from before that are shown again, so the bot can tell who
    // it was just talking with.
    private const int ContextTail = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wire the agent's I/O: read the log since it was last involved and speak to the channel.
        agent.Bind(
            readLog: async (channel, since) => await previews.AnnotateAsync(
                await bot.ReadLogSinceAsync(channel, since, MaxBacklog, ContextTail), LinkPreviewWait),
            send: bot.SendMessageAsync);

        // The bot doesn't get handed messages — just a nudge that the log changed (plus the
        // line itself, for the decision log).
        bot.ChannelActivity += agent.OnChannelActivity;

        await bot.ConnectAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
