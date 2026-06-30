# Smarty.Control

The command centre for your Smarty instance — a modern, mobile-first web app that shows what Smarty is
doing **right now** across every surface (the local chat **and** Slack), plus everything it knows and works with.

## What it shows

- **Live** — every conversation streaming in real time, with cleanly formatted tool-calling visuals
  (which tool ran, its arguments, its result), worker progress, questions, and files as they happen.
- **Tasks** — all background runs, live and past: status, persona, steps, and a one-tap stop for local runs.
- **Files** — every reference bucket (global, per-persona, per-brand) with drag-and-drop upload.
- **Memory** — every durable fact, grouped by scope, with add and retire.
- **Personas** — full management (name, description, capabilities) and the exact tools each persona can call,
  with what they do. The system prompt is never shown or edited.

## How it works

It's a React + TypeScript + Tailwind app (same stack and theme as `Smarty.Chat`). The backend lives in
`Smarty.Api` under `/api/control/*`, fronted by a `ControlHub` that every conversation mirrors its event
stream into. `Smarty.Slack` runs in a separate process and **forwards** its events to the hub over HTTP, so
Slack threads stream live alongside the local chat. A single global SSE stream (`/api/control/stream`) fans
every event out to the dashboard.

## Build & run

```bash
cd Smarty.Control
npm install
npm run build       # outputs to Smarty.Control/dist
```

`Smarty.Api` serves the built app at **<http://localhost:5179/control>** from the same origin.

For development with hot reload, run `npm run dev` (port 5174); it proxies `/api` to `Smarty.Api` on 5179.

### Live Slack in the dashboard

Point the Slack process at the API's hub (and, if set, share the token):

```bash
SMARTY_CONTROL_HUB_URL=http://localhost:5179   # the Smarty.Api base URL
SMARTY_CONTROL_TOKEN=<optional shared secret>  # must match the API's SMARTY_CONTROL_TOKEN
```

If `SMARTY_CONTROL_TOKEN` is set on `Smarty.Api`, the cross-process ingest endpoint requires it.
