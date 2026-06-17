# Smarty.Chat — agent tester

A classic React + TypeScript + Tailwind chat UI for exercising `Smarty.Agents` through the
`Smarty.Api` streaming backend. Tokens stream live; tool calls (e.g. `run_shell_command`) and
their results render inline; thinking is on its own toggle.

## Run it (two terminals)

**1. API** (`Smarty.Api`, listens on `http://localhost:5179`):

```bash
dotnet run --project Smarty.Api
```

**2. Frontend** (Vite dev server, proxies `/api` → `:5179`):

```bash
cd Smarty.Chat
npm install      # first time only
npm run dev
```

Then open the URL Vite prints (http://localhost:5173 if free). Requires the local Ollama
gateway running qwen3 (the API reads `Ollama:BaseUrl` / `Ollama:Model` from `appsettings.json`).

## Controls
- **model** — pick any model the gateway reports (`GET /api/models`).
- **tools (shell)** — when on, the agent gets the real `run_shell_command` tool.
- **show thinking** — reveal the streamed chain-of-thought (off the main answer stream).

## API surface (open, CORS-any)
- `GET  /health` — liveness + default model.
- `GET  /api/models` — passthrough of the gateway's model list.
- `POST /api/chat` — Server-Sent Events stream: `content`, `reasoning`, `tool_started`,
  `tool_completed`, `completed`, `done`, `error`. Body:
  `{ system?, model?, enableTools?, messages: [{ role, content }] }`.
