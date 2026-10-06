# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

DefinitelyHuman is an AI-powered IRC bot that behaves like a real person in chat. It connects to IRC, lurks in a channel, and responds based on a simulated human **attention model** rather than reacting to every message. It includes a Blazor Server web dashboard: a single timeline merging the chat log with the agent's decisions, plus a live focus gauge.

## Build & Run

```powershell
dotnet build DefinitelyHuman.slnx        # build the solution
dotnet run --project DefinitelyHuman      # run bot + web dashboard
```

Locally the dashboard runs on http://localhost:5265 (`Properties/launchSettings.json`).

### Configuration (`DefinitelyHuman/.env`)

Settings are loaded from `.env` in the working directory via `dotenv.net` (gitignored; see `.env.example` for a template). Note `dotenv.net` **overwrites** existing environment variables by default — so a value present in `.env` wins over a real env var of the same name. With no `.env` present, real environment variables are used.

- `ANTHROPIC_API_KEY` (required) — Claude API key.
- `ANTHROPIC_MODEL` (default `claude-haiku-4-5-20251001`) - only the default for the chat model; the Settings page overrides it.
- `IRC_HOST` (default `localhost`), `IRC_PORT` (default `6667`), `IRC_CHANNEL` (default `#clankersunite`; the home channel, joined on connect), `IRC_NICK` (default `DefinitelyHuman`)
- `IRC_PASSWORD` (optional) — sent as the IRC server password (`PASS`); blank/absent means none. NetIRC has no SASL support.
- `IRC_READONLY_CHANNELS` (optional) - comma-separated channels the bot may join and read but must never write to (shadow mode, see below).
- `IRC_USERNAME` (optional) — IRC username sent at registration, when it must differ from the nick. soju needs `user/network` here; blank means NetIRC's default (the nick).

The csproj copies `.env` to the build/publish output — **remove it from a publish before uploading anywhere**.

The chat database is `chatting.db` under `Environment.SpecialFolder.LocalApplicationData` (`%LocalAppData%` on Windows, `$XDG_DATA_HOME` on Linux).

No test projects exist yet. There is no formal lint step; verify changes with a build. The running app locks `DefinitelyHuman.exe`, so a full build fails to copy the exe while the bot is running — use `dotnet build ... -t:Compile` to compile-check without stopping it.

## Architecture

Single .NET 10 project (`Microsoft.NET.Sdk.Web`) with folder-based separation of concerns:

```
DefinitelyHuman/
  Program.cs               — startup wiring, env config, DI registration, web host
  Agent/
    ChatAgent.cs           — AI agent (Anthropic): attention-gated glances, prompt building, stateless model calls
    ChatAgentOptions.cs    — API key, model, nick, EnableThinking, LogReasoning
    Attention.cs           — the focus model: a [0,1] value decayed lazily from timestamps (no loop)
    AgentLog.cs            — persists agent events to SQLite via a background drain task; raises Updated for the UI
    AppSettings.cs         — dashboard-editable settings kept in SQLite: the model for each job, memory switches
    UsageLog.cs            — records every model call (job, model, tokens, web searches) and prices it from a hand-kept list-price table
  Memory/
    MemoryService.cs       — background loop, fact extraction from unread chat lines, recall for the agent, entity merge/wipe
    MemoryService.Curation.cs — the nightly pass: merge duplicate facts, invalidate contradicted ones, insights, entity profiles
    MemoryService.Descriptions.cs — web lookup of what a non-person entity is (one search-backed call each)
  Data/
    ChattingContext.cs     — EF Core DbContext (SQLite)
    Message.cs             — chat line (Channel, Timestamp, Nick, Text, IsOwnMessage)
    AgentEvent.cs          — agent event (Kind: Decision/Thinking/Error/ToolCall, Summary, Detail, optional MessageId)
    CachedLinkPreview.cs   — persisted OpenGraph preview, keyed by URL
    Memory.cs              — long-term memory tables: Entity, EntityAlias, EntityDescription, Fact, FactEvidence, MemoryRun, ProcessedMessage, Setting
  Irc/
    IrcBot.cs              — IRC client wrapper, DB logging, log reads, typing delay, replay grace window
    IrcBotOptions.cs       — nick, host, port, channel, password, username
    IrcBotService.cs       — BackgroundService wiring IrcBot's activity nudges to ChatAgent
    OutgoingFilterConnection.cs — NetIRC IConnection wrapper: every outgoing line passes one filter (USER rewrite for soju, read-only channel block)
  Utilities/
    LinkPreviewService.cs  — fetches + caches OpenGraph previews for URLs in chat (dashboard only)
  Web/
    App.razor              — Blazor root component (layout renders static; pages are interactive islands)
    Routes.razor           — routing
    _Imports.razor         — shared Razor usings
    Layout/
      MainLayout.razor     — page layout + sidebar nav
      ChannelNav.razor     — the sidebar's Timeline section: one entry per channel the bot is in or has a log of, with join/leave controls (interactive island; owns the channel <-> URL slug mapping)
      FocusWidget.razor    — live focus gauge + pending glance + link status in the sidebar (interactive island, polls every 1s)
    Components/Chat/
      ChatVirtualize.razor(.cs/.js) — bottom-anchored virtualized list with per-item height tracking, jump-to-index and at-bottom notifications
    Pages/
      Home.razor           — the timeline of one channel (`/` = the bot's channel, `/c/{slug}` = any logged channel): chat lines and agent events merged by timestamp, with link previews, find and decision stepping
      MemoryFacts.razor    — `/memory`: filterable table of facts
      MemoryEntities.razor, MemoryEntity.razor — entity list; one entity's profile, connection graph (inline SVG), facts, links, merge
      MemoryAsk.razor      — `/memory/ask`: a question answered by the chat model from the matching facts, which are listed under it
      MemoryLinks.razor    — who posted which link when (read from the chat log, no model)
      MemoryRuns.razor     — extraction and curation runs with token counts; "Curate now"
      Usage.razor          — `/usage`: estimated cost by job and by day, from `ModelUsage` (which `EnsureSchema` seeds once from the older `MemoryRuns` token totals)
      Settings.razor       — model per job, memory switches, wipe memory
      Error.razor, NotFound.razor
    Components/Memory/
      FactTable.razor      — fact rows (every entity named in the text is a link) with their evidence lines (each links to its place in the timeline) and invalidate/restore; shared by the facts, entity and ask pages
Scripts/
  ImportHalloyLog.cs       — file-based script: imports a Halloy log export into the chat database
```

**NetIRC** is the IRC client library, referenced as a NuGet package (`NetIRC` v1.1.2). The IRC plumbing lives in `Irc/IrcBot.cs` (connect, channel-join, message logging, sending). NetIRC always sends the nick as the IRC username and cannot parse IRCv3 message tags, so the client is constructed directly (not via its builder) to allow wrapping the connection.

## How the bot decides to talk

The bot is **state-driven, not event-driven**: it does not react to individual messages. Every message is written to the SQLite log; `IrcBot` then fires a payload-light `ChannelActivity(channel, line, mentionsMe)` nudge (the `line` is for the decision log; `mentionsMe` is the "highlight beep", a case-insensitive substring match on the nick). `ChatAgent` reacts to "the log changed":

1. **Attention** (`Attention.cs`) tracks a `focus` value in `[0,1]`, decayed lazily on read with a ~4 min half-life ("casual lurker"). It starts at 0. A direct mention snaps focus to `1.0` (`Notice()`); replying restores it to `~0.9` (`Engaged()`); ambient chatter just lets it fade.
2. On activity: a **mention** always schedules a glance; so does a **follow-up** (no mention, but focus ≥ `ActiveConversationFocus`, i.e. within a few minutes of the bot replying). An **ambient** message (lower focus) schedules one only if a focus-weighted roll passes (`NoticesAmbient`) — otherwise the bot returns *before any model call* (no tokens spent on what it "didn't see").
3. **Debounce**: at most one glance is pending at a time (a `CancellationTokenSource`); a burst of messages collapses into one read+reply. A fresh ping during a lazy ambient glance reschedules it sooner.
4. After a **notice delay** (mention or follow-up: 1–4s; ambient: scales up to ~2 min as focus drops), the glance reads the channel log **since the last engagement** (that channel's `LastFocusedAt`, in memory — reset to process start on restart), capped to the most recent ~150 messages (`IrcBotService.MaxBacklog`), with a `[... N earlier messages ...]` marker on overflow. The last few already-read lines from before the bookmark (`IrcBotService.ContextTail` = 10) are prepended under an "Earlier, already read" heading, so the model can tell who it was just talking with. The heading carries the age of the last of those lines; the system prompt's "patience" rule uses it to go quiet on one person who has kept the bot talking recently (prompt-only, so it is the model's judgment over those 10 lines, not a counter).
5. The glance picks one of three instruction tiers by focus: **mention** (must respond), **active-convo** (focus ≥ `ActiveConversationFocus` = 0.5: respond if addressed/about you), **idle** (low focus: reluctant). The model answers as **structured output** (a JSON schema enforced by the API via `ChatOptions.ResponseFormat`): `notes` (private scratchpad, stored as the event detail), `reply` (bool), `message` (the line to send). Anything that doesn't parse as `reply: true` with a message means staying quiet (`ChatAgent.ParseDecision`), so the model's deliberation can never be sent to the channel.
6. Model calls are **stateless** — a fresh `CreateSessionAsync()` per glance, so the bot's only memory is the backlog it just read (no unbounded session growth).

**Bouncer replay**: a bouncer replays missed messages in a burst right after registration, without timestamps. Messages arriving within `IrcBot.ReplayWindow` (10s) of registration are logged but do not nudge the agent. They are logged with the current time, so they still show up as context on the next glance.

**Several channels, one attention**: focus is global (one person, one pair of eyes), but the conversation is per channel. `ChatAgent` keeps a `ChannelState` per channel (unread bookmark, pending glance) and one `_conversationChannel`, the channel it last spoke in. "Mid-conversation" (always notice, 1–4s glance, the active-convo tier) applies only there; in every other channel a message goes through the ambient roll on the shared focus and the idle tier. A mention in any channel snaps focus to 1.0 and gets the mention tier there. Replies and decision events go to the channel the glance was for.

**Joining and leaving**: `IrcBot.JoinAsync`/`PartAsync` send the commands; `IrcBot.JoinedChannels` is tracked from the server's own JOIN/PART/KICK lines about the bot (so it includes channels a bouncer rejoins on connect) and `ChannelsChanged` fires when it changes. The sidebar's join box and leave buttons call these. Behind soju, a join or part is remembered by the bouncer; without one, only `IRC_CHANNEL` is joined on start.

**Read-only channels (shadow mode)**: in a channel listed in `IRC_READONLY_CHANNELS` the bot glances and asks the model as usual, but a reply is only recorded as a `would have replied: "..."` event. The channel's bookmark still advances, but it never becomes the conversation channel and the reply does not refresh focus. The guarantee does not rest on the agent: `OutgoingFilterConnection.WritesTo` drops any PRIVMSG/NOTICE/TAGMSG/TOPIC/KICK/MODE/INVITE aimed at a read-only channel at the last point before the socket (and logs a warning), and `IrcBot.SendMessageAsync` refuses as well. Set the variable before joining such a channel.

Not implemented: tools (the `ToolCall` event kind is reserved), private messages.

## Long-term memory

Inspired by mem0 and Zep/Graphiti, all in the same SQLite file: **entities** (people, projects, tools, places, topics; every name they go by is an `EntityAlias`, lowercased) and **facts** about them (one self-contained sentence with a subject entity and an optional object entity, which makes it a graph edge). Facts are never deleted: one that stops being true gets `InvalidatedAt` (and `SupersededByFactId`). `FactEvidence` links each fact to the chat lines it rests on.

- **Extraction** (`MemoryService`, a `BackgroundService` waking every minute): chat lines without a `ProcessedMessage` row are unread. They are taken per channel, oldest first, in conversation chunks (ended by a 30 min silence once the chunk has 20 lines, or at 80 lines); a chunk at the end of the log waits 10 min of quiet. One model call per chunk returns structured facts citing numbered lines. The prompt lists every known entity by name and, for the ones the chunk names, what they are (description and latest facts), so a namesake becomes its own entity instead of being attached to the wrong one. The same loop does live chat and bulk backlog: importing an old log just creates unread lines. Extraction only adds; a chunk that fails three times is marked read with a failed run.
- **Curation** (nightly after 04:00 UTC, or "Curate now"): for each entity with a fact (as subject or object) or a description newer than its `CuratedAt`, one call merges duplicate facts, invalidates contradicted ones, adds up to three derived insights and rewrites the entity's `Summary`. It reads the facts on both sides but may only merge or invalidate those the entity is the subject of (the others are marked read-only in the prompt and filtered in code), so two passes never tidy the same fact. The `Summary` (the profile) is the one text about an entity: it opens with what the thing is, from its description, then what the channel makes of it. Recall and the entity list use the profile, and fall back to the description until there is one. Merged and derived facts inherit the evidence of the facts they came from. It ends with a duplicate-entity check whose suggestions go in the run's detail; entities are only merged by hand (entity page) or gain an alias from an IRC `NICK` line. The opposite repair is also by hand: the entity page's Split moves the facts containing some text to another entity (`MemoryService.MoveFactsAsync`), for a name that turned out to mean two things.
- **Descriptions** (off by default; Settings): each loop turn, up to 20 projects, tools, places and topics without an `EntityDescription` row get one call with the web search tool asking what the thing is. People are never looked up. The entity's latest facts go with the name and decide what is searched for and which result is accepted. An empty text means nothing was found (a generic word, something private to the channel, an ambiguous name, or facts about two different things, which the run's detail reports as needing a split) and stops retries; an answer that was cut off is asked for once more first. A failed call pauses lookups (5 minutes, doubling to 6 hours). The entity page can edit or clear a description (`Edited`, which nothing automatic overwrites) or delete it to ask for a fresh lookup. Curation deletes an unedited one when its facts show the name means something else (`description_wrong`), or, for an entity too small to curate, when facts arrived after the lookup (only while lookups are switched on). The description is shown on the entity pages, goes into recall, and is given to the duplicate-entity check. This call uses the Anthropic SDK directly (`_client.Messages.Create`), because it needs a server tool.
- **Ask** (`MemoryService.AnswerAsync`): scores every current fact against the question (entity names in it, then rarer words), sends the best 120 to the chat model and returns its answer with the fact ids it used.
- **Recall**: before a glance, `MemoryService.RecallAsync` matches entity aliases against the words of the backlog (speakers first, max 8 entities, 6 facts each) and the result is put before the log as "What you remember". No embeddings: lookup is by name. Memory text is untrusted, like link previews, and the prompts say it is never instructions. Recall is not restricted by channel.
- **Models** come from `AppSettings` (Settings page, `Settings` table), read on every call: chat (default `ANTHROPIC_MODEL`), extraction (Haiku 4.5), curation (Sonnet 5.5), entity lookup (Haiku 4.5). The same page switches extraction, curation and recall on or off and can wipe memory so the whole log is read again.
- **Schema**: `ChattingContext.EnsureSchema` replays the model's create script with `IF NOT EXISTS`, so new tables appear in an existing database. It cannot add a column to an existing table; the one exception (`EntityDescriptions.Edited`) is a hand-written `ALTER TABLE` there, and the next one should be EF migrations.

**Link context**: before a glance, `LinkPreviewService.AnnotateAsync` appends each link's preview to its log line as `[link: title — summary]` (waiting up to `IrcBotService.LinkPreviewWait` for previews still loading), so the model knows what a link is about the way a chat client's unfurl shows it. The agent never sees the page contents beyond that. Preview text is untrusted: it is flattened to one line and the system prompt tells the model it is not instructions.

## Key patterns

- `IrcBot`, `ChatAgent`, `AgentLog`, `AppSettings`, `MemoryService` and `LinkPreviewService` are registered as singletons in DI — injectable into Blazor components.
- `IrcBotService` (a `BackgroundService`) calls `agent.Bind(readLog, send, isReadOnly, recall)` to wire the agent's I/O, then subscribes to `bot.ChannelActivity`.
- **Cost accounting is local**: every model call ends in `UsageLog.RecordAsync(job, ...)` (a `ModelUsage` row). A new call site must do the same or the Usage page undercounts; a new model needs a line in `UsageLog.Prices`.
- A fresh `ChattingContext` is created per DB operation (avoids shared DbContext concurrency issues).
- **Agent events** go through `AgentLog.Log`, which is synchronous and non-blocking (safe inside the agent's locks): it queues the event and a single drain task writes it to SQLite. A "replied" event carries the `MessageId` of the line it produced so the timeline can attach the reasoning to the chat line.
- **Extended thinking** (`ChatAgentOptions.EnableThinking`, 1024-token budget) and console echo (`LogReasoning`) exist but are not set in `Program.cs`, so both are off. The dashboard decision log is always captured regardless.
- Model calls are serialized with a `SemaphoreSlim` (a person composes one reply at a time; the notice delays can otherwise overlap).
- **UI notifications are fire-and-forget** (`_ = NotifyMessageLogged()`): a slow/disconnected Blazor circuit must never stall the IRC loop.
- The layout renders statically; components needing live updates (`ChannelNav`, `FocusWidget`, `Home`) are `@rendermode InteractiveServer` islands. `FocusWidget` polls focus on a `Timer` created in `OnAfterRender` (not during prerender) and disposed on teardown. `Home` refreshes on `IrcBot.MessageLogged`, `AgentLog.Updated`, and `LinkPreviewService.PreviewReady`.
- **Timeline outcomes are read from the event summary text**: `Home` styles and steps between "replied…" and "…decided to stay quiet" events by matching the strings `ChatAgent` logs (`RepliedPrefix`/`QuietSuffix` in `Home.razor`). Reword those summaries and the timeline must follow.
- **Sidebar status is deliberately modest**: the dot is only `IrcBot.IsConnected` (our link to `IrcBot.Endpoint`, which in production is the bouncer, not the network). `IrcBot.LastLineAt` ("last line Nm ago") is the evidence that the channel is actually reaching the bot.
- `Home` takes `?line={MessageId}` and scrolls to that chat line once the list is ready (used by the evidence links on the memory pages).
- The timeline is paged from SQLite through `ChatVirtualize`'s `ItemsProvider` (a UNION of `Messages` and `AgentEvents` ordered by timestamp).
- Typing delay simulates human speed (4–8 chars/sec + random pause).
- Replies over 400 chars are discarded (IRC message limit), and so is a reply to a channel the bot is no longer in (kicked while "typing"). `IrcBot.SendMessageAsync` returns null for both and the agent logs `reply not sent: "..."` instead of "replied".

### Tuning knobs

- **Attention feel**: `Attention` constructor — `halfLifeMinutes` (distractibility), `glanceFloor` (when a channel feels too dead to watch), `engagedFocus`, and the notice-delay formulas.
- **Active-conversation threshold**: `ChatAgent.ActiveConversationFocus`.
- **Backlog cap**: `IrcBotService.MaxBacklog`. **Earlier-context tail**: `IrcBotService.ContextTail`.
- **Replay grace window**: `IrcBot.ReplayWindow`.
- **Memory**: the constants at the top of `MemoryService` (chunking, recall size) and `MemoryService.Curation.cs` (curation hour, limits), and the extraction/curation prompts there.
- **Persona / reply judgment**: the system prompt and the three per-glance instruction tiers in `ChatAgent`.

## Deployment

Production runs on a Linux server behind a [soju](https://soju.im) bouncer, which holds the IRC connection so the bot can restart without leaving the channel.

- soju listens on `localhost:6667` for the bot; the bot logs in with `IRC_USERNAME=<soju user>/<network>` and `IRC_PASSWORD=<soju password>`. soju handles the network login (SASL) and rejoins saved channels.
- The bot runs as a systemd service from a framework-dependent `dotnet publish -c Release` output (needs the ASP.NET Core 10 runtime), with settings supplied as environment variables rather than a `.env` file.
- The dashboard binds to `127.0.0.1:5000` there and has no authentication — reach it over an SSH tunnel, never expose it.

## Dependencies

- **NetIRC** (v1.1.2, NuGet package) — IRC client
- **Microsoft.Agents.AI.Anthropic** (1.23.0-preview) — Claude via Microsoft AI agent framework
- **Microsoft.EntityFrameworkCore.Sqlite** — chat log, agent events, link preview cache, long-term memory, settings
- **dotenv.net** — `.env` file loading
