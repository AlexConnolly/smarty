# Smarty — Brain Specification

What Smarty knows, who is allowed to recall it, and how it avoids stating six-month-old news as fact.

This supersedes the scoping half of [MEMORY_SPEC.md](MEMORY_SPEC.md). That document's principles still hold —
structured and deterministic, the model proposes rather than silently commits, forgetting is soft and
reversible, time is engineered in — but its **scope model was too blunt to be safe.**

---

## 1. Why the old model had to go

Facts carried one opaque scope string: `null` (shared), `user:U123` (one person), or a project slug. In a group
channel the code simply switched personal memory **off** (`PersonalMemoryEnabled = false`) and allowed only the
workspace-wide scope.

So for a private channel where three people are discussing whether to let Dave go, there was no correct option:

| what you could do | what happened |
|---|---|
| mark it shared | the whole workspace can recall it |
| mark it personal | the other two people in the room can't |
| leave it | Smarty learns nothing from the conversation |

There was no way to say **"these three people know this."** That's the gap this spec closes.

---

## 2. The one rule

Knowledge carries an **audience**: either `public` (anyone) or an explicit set of people. A conversation has
**participants**. Then:

> Knowledge with audience **S** may be recalled in a room with participants **P** — if and only if **P ⊆ S**.
> *Everyone present must already have been party to it.*

Note the direction. It is **not** `S ⊆ P`. A fact learnt alone with one person must not surface the moment
someone else walks in, and only `P ⊆ S` gives that.

| learned in | recalled in | | why |
|---|---|---|---|
| `{alex}` | `{alex}` | ✅ | your own DM |
| `{alex}` | `{alex, dave}` | ❌ | Dave was never party to it |
| `{alex, dave, mike}` | `{alex, dave, mike}` | ✅ | the thread picks back up |
| `{alex, dave, mike}` | `{alex, dave}` | ✅ | both already heard it |
| `{alex, dave, mike}` | `{alex, dave, sarah}` | ❌ | Sarah wasn't there |
| `public` | anywhere | ✅ | a private room can recall #general |

Executable as `BrainIsolationTests.The_recall_matrix`.

### 2.1 Two rooms that must not be trusted

A subset test against an unknown set is **vacuously true**, which would hand over everything. Both degenerate
rooms therefore recall **public knowledge only**:

- **A public channel** is the wildcard participant set. No finite audience can contain it — which is also just
  correct: a public channel is not where three people's private discussion should resurface.
- **An unknown room** (we could not establish who is present) gets nothing private, and **cannot be written
  to.** Refusing the write is deliberate: knowledge with no audience is knowledge nobody can ever reach, so it
  is better to say so than to file it in a hole.

### 2.2 Membership drift is free

The room is recomputed every turn, never snapshotted. Sarah joining a private channel narrows what can be
recalled there on the very next message; her leaving widens it again. There is no revocation step to get wrong.

### 2.3 Same slot, two audiences

A slot can be filled publicly *and* privately, and in the private room both are legitimately visible. **The
newest wins**, with the narrower audience breaking a tie. Specificity-first is tempting — "the private version
is the real one" — but it pins a stale private value forever: if three people said *slipping* in January and the
project was publicly declared back on track in February, February is the answer.

---

## 3. Identity: a person is an email address

Audiences need person ids that are stable **across surfaces**, or an email thread and a Slack channel with the
same humans would be two unrelated brains. So a person is their email address, and every other identity —
`slack:U123`, `discord:456`, `web:local` — is an alias resolving to it (`PeopleStore`). Hosts plug in their own
lookup (Slack's `users.info` returns an email); results are cached, because re-asking mid-turn would put a
network call in every message.

The local single-user app defaults to a real, stable private identity (`self@smarty.local`) rather than "no
audience" — otherwise a solo user's brain would silently degrade to public-only. Set `Me:Email` (or `SMARTY_ME`)
to your work address and this machine's brain joins up with your Slack identity.

**This is the mechanism behind "it should just work for one person and for an organisation."** There is no
personal mode and no org mode to configure. A solo chat is an audience of one; a public channel is the
wildcard; a private channel is the set of people in it. One predicate covers all three.

---

## 4. The graph

The unit is an **edge**: `(subject → relation → value)`.

```jsonc
{
  "id": "e42",
  "subjectKey": "project:atlas",   // person:dave@x.com | project:atlas | topic:headcount | org
  "relation": "status",            // THE SLOT — a new value here supersedes the old
  "value": "slipping",
  "context": "integration tests are the blocker",
  "audienceKey": "alex@x.com+dave@x.com+mike@x.com",   // or "*" for public
  "asserted": "2026-08-12T10:04:00Z",
  "expiry": null,
  "status": "active",              // active | superseded | retired
  "supersededBy": null,
  "assertedBy": "mike@x.com",      // provenance: traceable to a person and a moment
  "source": "said by mike@x.com"
}
```

The old store had **no subject** — every fact was implicitly about "the user", and the scope string did double
duty as *whose fact is this* and *who may see it*. Splitting those apart is what turns a list of facts into a
graph: edges hang off anything, so "everything known about project X" is a subgraph read rather than a keyword
search that happens to match a slug.

**The relation is the slot, not the value.** A new value for the same `(subject, relation, audience)` supersedes
the old one; the old edge is kept and marked, so a wrong update is traceable and reversible.

### 4.1 Operations

| | |
|---|---|
| `Search(query, room)` | keyword search of what this room may know, ranked, with the conversation's own subjects boosted |
| `RelevantTo(message, k, room)` | contextual recall for auto-surfacing — embeddings **rank** what the audience filter already allowed; they never widen it |
| `Overview(subject, room)` | the subgraph for one subject. **This is where "give me an overview of project X" will hang** |
| `Remember(...)` | update-or-insert on the slot, audience taken from the room |
| `ForgetMatching(what, room)` / `Restore(id)` | soft, reversible, and only for what the room can actually see — otherwise a conversation could delete knowledge it isn't allowed to know exists |

There is **no unscoped read** on `Brain`. A caller cannot accidentally get everything, because the method that
would let them does not exist. The single exception, `AllEdgesUnfiltered()`, is named so that using it by
accident reads wrong, and is wired only to the control centre.

---

## 5. Staleness

The brain's most dangerous failure is not forgetting — it is **stating something six months old as though it
were true this morning.** The model has no sense of elapsed time: a fact from March reads exactly as current as
one from an hour ago unless the system says otherwise.

So:

1. **Every read is stamped with an age** — "today", "3d ago", "5mo ago".
2. **Half-life is per relation**, because volatility belongs to the slot, not the sentence. A `status` goes
   stale in a week; a `deadline` in a month; a `role` in a year; `home`, `diet` and `birthday` essentially never.
   An unrecognised slot gets 120 days — the safer error is asking again.
3. **Past its half-life, knowledge is surfaced as needing confirmation**, in words, in the tool output:
   *"may be out of date — confirm before relying on it."* The model acts on what the tool says, so the
   instruction belongs in the text, not only in a flag.
4. **An explicit `expiry` that has passed drops out of recall entirely.** It was given a date for a reason.
5. **Re-asserting the same value re-stamps it.** The cheapest possible staleness fix: it was true just now.

---

## 6. Where this is going

### 6.1 From edges to a model

Today `Overview` returns the subgraph and the worker reasons over it. The intended end state is that asking the
brain *"give me an overview of project X"* is answered **by the brain** — a model reading the subgraph and
composing it, rather than the caller pasting edges into a prompt. The audience filter runs first either way, so
the sub-model can only ever see what the room is allowed to know.

### 6.2 Feeds — the next job

A **feed** pumps knowledge in without anyone talking to Smarty: a Slack channel, an email account, a calendar.
The shape follows from this spec — a feed supplies *items* plus **the audience derived from its own membership**
(an email thread's recipients, a channel's members), and an extraction step turns items into edges with
provenance.

**Feeds are not built yet, and deliberately so.** The requirement before writing one is *proof it works*: a feed
test that ingests a known transcript and then demonstrates the isolation matrix holds over ingested knowledge —
not just over hand-written facts. Building ingestion before that proof means discovering a leak with real data
in the store.

### 6.3 Discord

The next surface after this is Discord — chatting to Smarty the way you'd talk to a person. It needs nothing new
from the brain: a Discord user id becomes another alias, a channel's membership becomes participants, and the
same predicate applies. That is the test of whether this model is right.

---

## 7. Status

**Live.** The brain is what answers in chat. Both hosts are on it:

- **The orchestrator** recalls, records, forgets and gives overviews through the brain — including the
  auto-surfaced context each turn, the sticky working set, project state, and project resolution. Every one of
  those reads goes through the audience filter.
- **Slack** establishes the room per turn from `conversations.members`, resolving each id to a person via
  `users.info`. A DM is an audience of two, a private channel its membership, a public channel the wildcard.
- **The web app** is an audience of one — the local user — which is the same mechanism, not a special case.
- **A task inherits its conversation's room**, so a background worker recalls exactly what the people who asked
  were party to, and nothing wider.
- **The binary switch is gone.** `PersonalMemoryEnabled` no longer exists; a group channel has a correct
  audience instead of a blackout.

**Slack needs two scopes** it may not already have: `users:read.email` (to identify people) and
`conversations.members` (to see who's in the channel). Without them nobody resolves, the room is *unknown*, and
Slack recalls public knowledge only and records nothing — the safe failure, but a silent one, so it's logged.

**Left standing on purpose:** `MemoryStore` and `MemoryTools` remain in the tree, used only for the one-time
carry-over at startup. Nothing else references them, and they can be deleted once you're satisfied nothing was
lost in the migration.

**Tests:** 23 — the isolation matrix and staleness against the brain itself, plus the same rules asserted
through the tools the model actually calls (`BrainToolsIsolationTests`), which is what would catch the wiring
being wrong while the predicate is right.
