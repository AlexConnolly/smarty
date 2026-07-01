<div align="center">

# 🟣 Smarty

### Your own personal assistant — running entirely on your machine.

No cloud. No API keys. No subscriptions. Smarty talks to you like a real assistant, then quietly
goes off and *does the thing* — researches the web, checks your system, runs background tasks — all on
a local model you control.

<img src="docs/landing.png" alt="Smarty — How can I help?" width="800" />

</div>

---

## What is it?

Most AI assistants are a chat box bolted to someone else's servers — you type, you **wait**, you get one
answer. Smarty works the way a real assistant would: you ask, it gets going, and you both carry on.

## ⚡ It works while you keep talking

Ask Smarty for something real and it doesn't freeze the chat — it gets going in the background and hands
the conversation straight back to you:

> **You:** What's the latest news?
>
> **Smarty:** Checking that out for you now — anything else I can help with while I work on it?

So you keep chatting, kick off more jobs, or change your mind mid-task. Smarty runs them all at once,
re-steers when you do, and drops each result in the moment it's ready. Less chatbot, more handing things
to someone capable.

## 🧰 What it can actually do — tools

Smarty's workers don't just talk; they have real **tools** and use them to get you real answers:

- 🌐 **`web_search`** — searches the web, with automatic engine fallback so a rate-limit doesn't dead-end it.
- 📄 **`get_page_answer`** — actually opens a page, reads it, and extracts the answer — *grounded in the
  real content,* not guessed from memory.
- 🖥️ **`run_shell_command`** — your machine's shell: system info, files, scripts, local APIs — anything
  you could type yourself.
- 📊 **System info** — disk, memory, CPU, and OS, read straight from the OS.

It's all built on a small, **tool-first agent framework** (`Smarty.Agents`), so adding a new capability
is just adding a tool: give it a name, a one-line description, and what to run — and the model can use
it. Want Smarty to control your lights, hit your calendar, or query your database? That's a tool.

<div align="center">
<img src="docs/chat.png" alt="Smarty answering a question" width="800" />
</div>

### Also in the box

- 🎙️ **Voice notes** — talk to it; local Whisper transcribes on-device.
- 🧠 **Thinks before it answers** — and tells you when it *can't* get something instead of fabricating.
- 📈 **Learns from use** — every interaction (and your 👍/👎) is logged locally toward a future fine-tune.
- 🔒 **100% local** — your data never leaves the machine.

---

## How it works

```
            you  ⇄  Orchestrator  ──delegates──►  Worker(s)
                   (talks, routes,                (shell · web_search ·
                    relays, manages tasks)         get_page_answer · system info)
                          │                              │
                          └────────── local Ollama ◄─────┘
                                   (one model, two roles)
```

- **`Smarty.Chat`** — a React + Vite + Tailwind web UI (streamed replies, voice notes, feedback).
- **`Smarty.Api`** — an ASP.NET Core service: the orchestrator, the workers, Whisper transcription, and
  the persistent event stream. Serves the UI too, so it's a single origin.
- **`Smarty.Agents`** — a small, dependency-light C# agent framework (agents, tools, the Ollama
  provider) that everything is built on.

Everything runs against a local [**Ollama**](https://ollama.com) model (default **`qwen3.5:latest`**).

---

## Setup

Get it running from scratch. Commands shown for **Windows**; macOS/Linux notes inline.

### 1. Prerequisites

| You need | Why | Get it |
|---|---|---|
| **[Ollama](https://ollama.com/download)** | runs the local model | one-click installer |
| **[.NET 7 SDK](https://dotnet.microsoft.com/download/dotnet/7.0)** | builds & runs the API | installer |
| **[Node.js 18+](https://nodejs.org)** | builds the web UI | installer |
| A GPU with **~8 GB VRAM** (recommended) | speed | optional — runs on CPU, just slower |

### 2. Pull a model

```bash
ollama pull qwen3.5:latest
```

> 💡 On lighter hardware? `ollama pull qwen3.5:4b` is ~2× faster (set the model in step 4). `qwen3.5:latest`
> (a 9.7B model, ~6 GB) is the recommended default if you've got the VRAM.

Make sure Ollama is running (`ollama serve`, or it starts automatically after install).

### 3. Get the code

```bash
git clone https://github.com/AlexConnolly/smarty.git
cd smarty
```

### 4. Build the web UI

```bash
cd Smarty.Chat
npm install
npm run build      # outputs to Smarty.Chat/dist, which the API serves
cd ..
```

### 5. Run it

```bash
cd Smarty.Api
dotnet run
```

Then open **<http://localhost:5179>** — and say hi. 🎉

> 📱 **Use it from your phone:** the API binds all interfaces, so on the same Wi-Fi just visit
> `http://<your-pc-lan-ip>:5179`.

That's it — chat, web research, voice notes, and system queries all work out of the box. (The Whisper
voice model downloads itself on first use.)

---

## Configuration

Override anything via `Smarty.Api/appsettings.json` or environment variables (`__` = nested key):

| Setting | Env var | Default | What it does |
|---|---|---|---|
| `Ollama:Model` | `Ollama__Model` | `qwen3.5:latest` | which local model to use |
| `Ollama:BaseUrl` | `Ollama__BaseUrl` | `http://localhost:11434` | where Ollama is |
| `Urls` | `Urls` | `http://localhost:5179` | what address to serve on |
| `Whisper:ModelPath` | `Whisper__ModelPath` | `models/ggml-base.bin` | local voice model (auto-downloads) |
| `Training:Dir` | `Training__Dir` | `Smarty.Api/training-data` | where interaction/feedback logs go |

Example — run faster on a smaller model:

```bash
Ollama__Model=qwen3.5:4b dotnet run        # macOS/Linux
$env:Ollama__Model="qwen3.5:4b"; dotnet run  # Windows PowerShell
```

---

## Project layout

```
smarty/
├── Smarty.Api/        ASP.NET Core API — orchestrator, workers, Whisper, SSE  (also serves the UIs)
├── Smarty.Chat/       React + Vite + Tailwind web client
├── Smarty.Control/    the command centre — live view of every conversation, task, file, memory & persona
│                      (served by Smarty.Api at /control; see Smarty.Control/README.md)
├── Smarty.Slack/      Slack gateway (separate process; forwards its activity to the control hub)
├── src/Smarty.Agents/ the C# agent framework (agents, tools, Ollama provider)
├── samples/           a minimal console sample
└── tests/             unit tests for the agent framework
```

---

## Status & notes

Smarty is an evolving personal project — a local-first take on what a genuinely useful assistant could
be. Reliability is bounded by whatever local model you run; it's designed to **fail honestly** (tell you
when it can't get something) rather than fabricate. Contributions and ideas welcome.

*Built with C#, React, and a local LLM. Powered by [Ollama](https://ollama.com).*
