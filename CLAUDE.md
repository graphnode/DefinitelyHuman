# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

DefinitelyHuman is an AI-powered IRC bot that behaves like a real person in chat. It connects to IRC, lurks in a channel, and responds based on a simulated human **attention model** rather than reacting to every message. It includes a Blazor Server web dashboard for viewing chat logs, the agent's decision log, and a live focus gauge.

## Build & Run

```powershell
dotnet build DefinitelyHuman.slnx        # build the solution
dotnet run --project DefinitelyHuman      # run bot + web dashboard
```

The web dashboard runs on the default Kestrel ports (http://localhost:5000).

### Configuration (`DefinitelyHuman/.env`)

Settings are loaded from `DefinitelyHuman/.env` via `dotenv.net` (gitignored; see `.env.example` for a template). Note `dotenv.net` **overwrites** existing environment variables by default — so a value present in `.env` wins over a real env var of the same name.

- `ANTHROPIC_API_KEY` (required) — Claude API key. May come from `.env` or the real environment.
- `IRC_HOST` (default `localhost`), `IRC_PORT` (default `6667`), `IRC_CHANNEL` (default `#clankersunite`), `IRC_NICK` (default `DefinitelyHuman`)
- `IRC_PASSWORD` (optional) — SASL PLAIN password; blank/absent means no authentication.

No test projects exist yet. There is no formal lint step; verify changes with a build. The running app locks `DefinitelyHuman.exe`, so a full build fails to copy the exe while the bot is running — use `dotnet build ... -t:Compile` to compile-check without stopping it.

## Architecture

Single .NET 10 project (`Microsoft.NET.Sdk.Web`) with folder-based separation of concerns:

```
DefinitelyHuman/
  Program.cs               — startup wiring, env config, DI registration, web host
  Agent/
    ChatAgent.cs           — AI agent (Anthropic): attention-gated glances, prompt building, stateless model calls
    Attention.cs           — the focus model: a [0,1] value decayed lazily from timestamps (no loop)
    AgentLog.cs            — in-memory ring buffer of the agent's decisions, for the dashboard
  Data/
    ChattingContext.cs     — EF Core DbContext (SQLite)
    Message.cs             — Message entity (Channel, Timestamp, Nick, Text, IsOwnMessage)
  Irc/
    IrcBot.cs              — IRC client wrapper, DB logging, log reads, typing delay
    IrcBotService.cs       — BackgroundService wiring IrcBot's activity nudges to ChatAgent
  Web/
    App.razor              — Blazor root component (layout renders static; pages are interactive islands)
    Routes.razor           — routing
    _Imports.razor          — shared Razor usings
    Layout/
      MainLayout.razor     — page layout + sidebar nav
      FocusWidget.razor    — live focus gauge in the sidebar (interactive island, polls every 1s)
    Pages/
      Home.razor           — chat log dashboard
      Reasoning.razor      — the agent's decision log
      Error.razor, NotFound.razor
```

**NetIRC** is included as a git submodule (`NetIRC/`) at tag v1.1.2, referenced as a project. It has been extended with SASL PLAIN authentication support and a `RawDataSent` event. Note: the submodule currently has **local modifications** for SASL/`RawDataSent` that are not yet committed inside the submodule — a fresh `--recursive` clone would not have them.

## How the bot decides to talk

The bot is **state-driven, not event-driven**: it does not react to individual messages. Every message is written to the SQLite log; `IrcBot` then fires a payload-light `ChannelActivity(line, mentionsMe)` nudge (the `line` is for the decision log; `mentionsMe` is the "highlight beep"). `ChatAgent` reacts to "the log changed":

1. **Attention** (`Attention.cs`) tracks a `focus` value in `[0,1]`, decayed lazily on read with a ~4 min half-life ("casual lurker"). A direct mention snaps focus to `1.0` (`Notice()`); replying restores it to `~0.9` (`Engaged()`); ambient chatter just lets it fade.
2. On activity: a **mention** always schedules a glance; an **ambient** message schedules one only if a focus-weighted roll passes (`NoticesAmbient`) — otherwise the bot returns *before any model call* (no tokens spent on what it "didn't see").
3. **Debounce**: at most one glance is pending at a time (a `CancellationTokenSource`); a burst of messages collapses into one read+reply. A fresh ping during a lazy ambient glance reschedules it sooner.
4. After a **notice delay** (mention: short padding capped ~1 min; ambient: scales up to ~2 min as focus drops), the glance reads the channel log **since the last engagement** (`_lastFocusedAt`), capped to the most recent ~150 messages (`IrcBotService.MaxBacklog`), with a `[... N earlier messages ...]` marker on overflow.
5. The glance picks one of three instruction tiers by focus: **mention** (must respond), **active-convo** (focus ≥ `ActiveConversationFocus` = 0.5: respond if addressed/about you), **idle** (low focus: reluctant). The model returns a reply or `[SILENT]`.
6. Model calls are **stateless** — a fresh `CreateSessionAsync()` per glance, so the bot's only memory is the backlog it just read (no unbounded session growth).

## Key patterns

- `IrcBot`, `ChatAgent`, and `AgentLog` are registered as singletons in DI — injectable into Blazor components.
- `IrcBotService` (a `BackgroundService`) calls `agent.Bind(readLog, send)` to wire the agent's I/O, then subscribes to `bot.ChannelActivity`.
- `IrcBot` creates a scoped `ChattingContext` per DB operation (avoids shared DbContext concurrency issues).
- **Stateless model calls** with optional extended thinking (`enableThinking` flag, off by default — costs output tokens per glance) and console echo (`logReasoning` flag). The dashboard decision log is always captured regardless of `logReasoning`.
- Model calls are serialized with a `SemaphoreSlim` (a person composes one reply at a time; the notice delays can otherwise overlap).
- **UI notifications are fire-and-forget** (`_ = NotifyMessageLogged()`): a slow/disconnected Blazor circuit must never stall the IRC loop. (A disconnected circuit's JS interop blocks for the 60s default timeout — `Home.razor` also passes a short `TimeSpan` timeout to its JS interop calls to fail fast.)
- The layout renders statically; components needing live updates (`FocusWidget`, `Reasoning`, `Home`) are `@rendermode InteractiveServer` islands. `FocusWidget` polls focus on a `Timer` created in `OnAfterRender` (not during prerender) and disposed on teardown.
- Typing delay simulates human speed (4–8 chars/sec + random pause).
- Replies over 400 chars are discarded (IRC message limit).

### Tuning knobs

- **Attention feel**: `Attention` constructor — `halfLifeMinutes` (distractibility), `glanceFloor` (when a channel feels too dead to watch), `engagedFocus`, and the notice-delay formulas.
- **Active-conversation threshold**: `ChatAgent.ActiveConversationFocus`.
- **Backlog cap**: `IrcBotService.MaxBacklog`.
- **Persona / reply judgment**: the system prompt and the three per-glance instruction tiers in `ChatAgent`.

## Dependencies

- **NetIRC** (v1.1.2, submodule) — IRC client with SASL support
- **Microsoft.Agents.AI.Anthropic** (1.8.0-preview) — Claude via Microsoft AI agent framework
- **Microsoft.EntityFrameworkCore.Sqlite** — chat log persistence
- **dotenv.net** — `.env` file loading
