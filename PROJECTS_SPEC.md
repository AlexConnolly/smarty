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

- **blank** → a general job, no project context. *This is the default — most tasks are one-offs.*
- **slug** → the worker runs *inside* that project: it gets the project's context injected, and anything
  it remembers is tagged to the project (see §5).

**`delegate` validates; it never creates.** An unknown slug is rejected with a pushback — *"no project
'x' — use `list_projects` to find the right one, or `create_project` if this is genuinely a new ongoing
thing."* So a project can never be spawned as a side-effect of delegating.

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

## 7. Lifecycle — and keeping it to a *handful*

Projects must stay few and deliberate. Four guards stack to prevent sprawl:

1. **Default is no project.** Most tasks delegate with a blank project. A project is the exception.
2. **`delegate` can't create** (§3) — an unknown slug is pushed back, never auto-created.
3. **Creation is its own deliberate, confirmed step** — `create_project(title, description)` → returns a
   slug. The assistant **proposes and you confirm** ("this looks like an ongoing thing — want me to track
   it as a project?") rather than minting one silently. Reserved for genuinely long-running, multi-session
   endeavours, never one-off tasks.
4. **Reference by meaning, not invented slugs** — routing (§4) matches a message to *existing* projects,
   so the model reaches for the real one instead of coining a near-duplicate; the delegate pushback also
   nudges "check `list_projects` first."

Other verbs: **`list_projects`** (what's on the go) and **complete / archive** (`status` flips; archived
projects stay queryable but drop out of routing).

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

---

## 10. Routing as reference resolution (Phase 3, built)

The naive view of routing is *classification* — "which bucket does this message belong to?" That fails
fast, because a project's **title and its contents drift apart almost immediately**. You start
`holiday-with-my-sister-emma`, then everything you say is "the flights", "the Lisbon trip", "Emma's
dates" — none of which are in the title. Title-matching is dead on arrival.

So routing is **reference resolution**, not classification. "The flights next week" is a pronoun-like
reference; the job is to resolve *what it points at* by searching the project's accumulated **facts**,
not just its name. The project's memory **is** its searchable surface area.

**`find_project(statement)`** (orchestrator tool, `MemoryStore.RankProjects`):
- Embeds the statement once; for each active project, scores the **max cosine** across its
  title+description **and every fact recorded inside it**. One strong fact hit (`destination: Lisbon`)
  is enough to surface the project — that's the whole point.
- Returns the resolved slug **only on a confident match**; otherwise it tells the model to **ask the
  user**. It never invents a project. Creation stays an explicit, confirmed act.

**Calibration** (against `nomic-embed-text`): its cosines have a compressed range — unrelated statements
still sit ~0.42–0.51 on topical adjacency, while a genuine reference scores ~0.61+. Measured:

| statement | vs title | vs fact (Lisbon) |
|---|---|---|
| "what time are the flights next week" | 0.527 | **0.611** |
| "book the table for the Lisbon trip" | 0.470 | **0.679** |
| "renew my car insurance" | 0.448 | 0.382 |
| "capital of France" | 0.426 | 0.510 |

→ `strong = 0.55` (true matches clear it; noise doesn't), `weak floor = 0.52` (below = no match → ask).
Uncertainty biases to **ask**, never assume. The fact, not the title, is what makes "the flights"
resolve — confirming the drift problem this solves.

### Write-routing: project facts go through a task, not the orchestrator

The orchestrator does no work itself — and a project fact should be written with the project's context
loaded, so it reconciles with what's already known there. So chat-level `set_memory` **won't write
project facts at all.** The tool *advertises* a `project` slot (so the model reaches for it), but the
top level **rejects** a project-scoped write and redirects: *delegate it into the project, and the
worker records it with the project's full context.* Workers already auto-tag their writes to their
project, so this falls out naturally — and it trains the right reflex (route project work, don't do it
up here). Pretend-you-can, reject, redirect.

Reads are different: reading isn't work. Once `find_project` resolves a reference it sets a **current
focus** on the session; while focused, that project's relevant facts are surfaced into chat context
(via `ProjectFocusNote`) so the orchestrator can answer about it directly. Writes routed, reads
surfaced. The focus drops when the project's gone or the user clearly moves on.
