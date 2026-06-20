# Smarty — Projects & Context Routing Specification

Long-running endeavours — planning a holiday, a house move, a job hunt — need to **persist** and be
**returnable-to from the single chat entry point**, without cluttering everyday chat. Projects are the
container for that. The system routes each message (and each delegated job) to a project, or to none,
and scopes context accordingly.

Builds on [`MEMORY_SPEC.md`](MEMORY_SPEC.md) — a project is, in large part, just a **namespace over the
memory we already have.**

---

## 1. Two kinds of context

| | What | How it's surfaced |
|---|---|---|
| **Who you are** | global facts about the user (allergies, where you live, preferences) | per-message relevance retrieval (the memory system) |
| **What you're working on** | projects (the sister holiday, the house move) | **routed to as a unit** by project |

The second is the new piece. And the key property: **routing to a project is far more reliable than
per-fact relevance**, because you have a *handful* of projects, not hundreds of facts. Coarse routing
over a few things is robust — it sidesteps the messy relevance problem entirely.

---

## 2. Data model

A project is a small entity; everything else is a tag on what we already store.

```jsonc
// Project
{ "slug": "sister-holiday", "title": "Holiday with my sister",
  "description": "Planning a week away with Emma, summer 2026.",
  "status": "active",            // active | done | archived
  "created": "2026-06-20T..." }
```

- **A memory fact** (see MEMORY_SPEC) gains one optional field: **`project`** (a slug).
  - `project: null` → a global "who you are" fact.
  - `project: "sister-holiday"` → belongs to that project.
  - Same store, same deterministic write rules — projects are just a dimension on it.
- **A task** gains the same optional `project`, so a delegated job knows which project it serves.

No separate context store. "Tell me about the trip" = the facts where `project = sister-holiday`.

---

## 3. `delegate(task, project?)`

The offloader takes an optional project slug:

- **blank** → a general job, no project context.
- **slug** → the worker runs *inside* that project: it gets the project's context injected, and anything
  it remembers is tagged to the project (see §5).

---

## 4. Routing (the orchestrator)

The orchestrator decides which project — if any — a message belongs to:

- **Explicit reference** is the strong signal: *"the trip with my sister"* → match against project
  titles/descriptions (embeddings or keyword — trivially reliable over a few projects).
- **Current focus**: if you've been talking about a project, later messages default to it until you
  switch. (A soft, session-level "active project".)
- **Ambiguous / none** → general chat, or it asks which project.

A matched project means: its context is surfaced to the orchestrator, and any job it delegates inherits
the project slug.

---

## 5. Project-scoped behaviour — the prompt shift *(the important bit)*

When the orchestrator or a worker is operating **inside** a project, three things change:

1. **Project context is injected** — the project's title, description, and its accumulated facts/notes,
   so the agent knows the state of play.
2. **Memory is reframed.** The system prompt gently nudges:
   > *"You're working on the project **'{title}'**: {description}. Use memory to track details about
   > **this project** — decisions, dates, findings — not facts about the user."*
   So `set_memory` captures **project** context, not personal facts.
3. **Writes are auto-tagged.** `set_memory` calls made in a project context are automatically stamped
   `project = {slug}` — the model decides *what* is worth keeping; the system handles *where* it goes.

Outside a project, memory behaves exactly as it does today: facts about the user, untagged.

---

## 6. How a project accumulates

A project is dormant until you touch it, then builds up state as you work it: project-tagged tasks
write their findings back as project facts; things you tell it ("we've settled on Lisbon, first week of
July") become project facts. Over time the project holds a running picture — which is what makes
*"give me the TLDR of the trip"* answerable: summarise the project's facts.

---

## 7. Lifecycle

- **Create** — explicit (`create_project(title, description)` → returns a slug), or the assistant
  **proposes and you confirm** when a multi-step endeavour clearly emerges ("want me to make this a
  project so I can keep track?"). Never created silently.
- **`list_projects`** — see what's on the go.
- **Complete / archive** — `status` flips; archived projects stay queryable but drop out of routing.

---

## 8. Worked example

> **You:** Could you give me the TLDR of the trip plans with my sister?

1. Orchestrator matches the message → **Sister Holiday** project.
2. Pulls its context — facts: *Lisbon, 1st week July; flights ~£180; Emma prefers an apartment.*
3. Summarises it. The project had been dormant; mentioning it brought it back, and it never cluttered
   the chat in between.

> **You:** Great — go find flights for it.

→ `delegate("find flights, Lisbon, 1st week July", project: "sister-holiday")` → the worker runs with
the project context, searches, and writes the options back as **project** facts (not as facts about
you).

---

## 9. Build phases

1. **Project store** + the `project` field on facts/tasks + `create_project` / `list_projects`.
2. **`delegate(project?)`** → project-context injection + the §5 prompt reframe + auto-tagging of
   `set_memory` writes.
3. **Routing** — match a message to a project, the soft "current focus", and asking when ambiguous.

---

## Principles (consistent with the rest of Smarty)

- Projects are created **explicitly or proposed-and-confirmed** — never silently.
- The same **deterministic** memory store underneath; `project` is just a namespace, not a new system.
- **Routing over a few projects is reliable** — that's the whole reason this works where per-fact
  relevance struggles.
- One chat entry point → routes each message to a project (or none) → injects that context → workers
  inherit it.
