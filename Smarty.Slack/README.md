# Smarty.Slack

Smarty, in your Slack workspace. Tag **@smarty** in any thread and it joins as a teammate: it reads what's
going on in the thread, decides when it's actually being spoken to, and does real work in the background
(web research), posting answers back into the thread. Casual, fast, snappy — not chunky.

It reuses the existing engine (`Smarty.Api`'s `Orchestrator` + `Session`) unchanged. The only differences
are a Slack-flavoured system prompt, a project-free toolset, web-only workers, and a Slack output sink. The
web chat app is completely unaffected.

## How it behaves

- **A thread is a conversation.** Each thread Smarty is tagged in becomes its own session (the equivalent of
  a chat). No projects — a thread is the only "entry point".
- **Tagging starts it listening.** An explicit `@smarty` always gets a reply, and puts the thread into
  "listening" mode. The first time it engages a thread, it backfills what was already said so it has context.
- **A pre-processor decides when to chime in.** For later untagged messages in a listening thread, a cheap,
  fast classifier (`{respond, reason}`, no chain-of-thought, no tools) decides whether the message is for
  Smarty or just colleagues talking. When unsure, it stays quiet. Every message is kept in context either way.
- **Async, like the chat.** It replies immediately with a short line ("On it — checking that now 👀"), runs
  the work in the background, and posts the result into the thread when it's ready.
- **Answers to its questions just happen in-thread.** If a task needs a decision, Smarty asks in the thread;
  whoever replies, that reply is routed back to the waiting task.

## Set up the Slack app

1. Create an app at <https://api.slack.com/apps> → **From scratch** (or use the manifest below).
2. **Socket Mode** → enable it. This is why no public URL / tunnel is needed — the bot dials out from your PC.
3. **App-Level Token**: create one with scope `connections:write`. This is your `SLACK_APP_TOKEN` (`xapp-…`).
4. **OAuth & Permissions** → add **Bot Token Scopes**:
   `app_mentions:read`, `channels:history`, `groups:history`, `chat:write`, `users:read`.
5. **Event Subscriptions** → subscribe to **bot events**: `app_mention`, `message.channels`, `message.groups`.
6. **Install** the app to your workspace. Copy the **Bot User OAuth Token** (`xoxb-…`) → `SLACK_BOT_TOKEN`.
7. Invite the bot into a channel (`/invite @smarty`).

### App manifest (optional shortcut for steps 2–6)

```yaml
display_information:
  name: Smarty
features:
  bot_user:
    display_name: smarty
    always_online: true
oauth_config:
  scopes:
    bot:
      - app_mentions:read
      - channels:history
      - groups:history
      - chat:write
      - users:read
settings:
  event_subscriptions:
    bot_events:
      - app_mention
      - message.channels
      - message.groups
  socket_mode_enabled: true
```

## Run

Set the environment variables and run the project. Requires a local Ollama (same as the web app).

```powershell
$env:SLACK_BOT_TOKEN     = "xoxb-…"          # required
$env:SLACK_APP_TOKEN     = "xapp-…"          # required (Socket Mode)
$env:SMARTY_COMPANY_NAME = "Acme Ltd"        # who Smarty is working with (shown in its prompt)
$env:SMARTY_COMPANY_CONTEXT = "We build…"    # optional: extra context/tone for the prompt
$env:SMARTY_MODEL        = "qwen3:4b"         # optional (default qwen3:4b)
$env:OLLAMA_BASE_URL     = "http://localhost:11434"  # optional
# $env:SMARTY_SLACK_DATA_DIR = "C:\…"        # optional; defaults to a local slack-data folder

dotnet run --project Smarty.Slack
```

Then, in Slack, in any channel the bot is in:

> **@smarty** what's the weather in London this weekend?

Smarty acks in-thread, researches in the background, and replies with the answer.

## Environment variables

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `SLACK_BOT_TOKEN` | yes | — | Bot token (`xoxb-…`) for the Web API |
| `SLACK_APP_TOKEN` | yes | — | App-level token (`xapp-…`) for Socket Mode |
| `SMARTY_COMPANY_NAME` | no | `the team` | Who Smarty is working with |
| `SMARTY_COMPANY_CONTEXT` | no | — | Extra workspace context/tone for the prompt |
| `SMARTY_MODEL` | no | `qwen3:4b` | Ollama model |
| `OLLAMA_BASE_URL` | no | `http://localhost:11434` | Ollama gateway |
| `SMARTY_SLACK_DATA_DIR` | no | `./slack-data` | Isolated data dir (never the web app's data) |
