# Smarty — Memory Specification

How Smarty remembers you. The goal is that it feels like it genuinely **listens** — notices things
without being told, uses them later unprompted, and lets go gracefully — *without ever quietly getting
you wrong.*

This is **structured, deterministic memory**, not a semantic vector soup. The model proposes; it never
silently commits anything it can't undo. That's what makes it trustworthy on a small local model.

---

## 1. Principles (non-negotiable)

1. **Structured & deterministic.** Lookups and supersession are exact rules over typed facts — not
   fuzzy similarity. Memory has to work *every time*, not *usually*.
2. **The model proposes; it never silently commits the irreversible.** It can notice and suggest all
   day; anything expensive or destructive is surfaced and/or reversible.
3. **Honesty over confident-wrong.** Never assert as *current* something it can't confirm is still
   true. Same disease as everything else: stale-stated-as-fact. Same cure: ground in real time, hedge
   when unsure.
4. **Forgetting is asymmetric.** Wrongly *remembering* = mild clutter, one correction fixes it. Wrongly
   *forgetting* = losing something you cared about, feels like betrayal. So forgetting is **soft and
   reversible**, and held to a higher bar than remembering.
5. **Time is engineered in.** The model is timeless — it doesn't *feel* weeks pass. So the system
   stamps every fact and injects its age when surfacing it.

---

## 2. Data model

The unit of memory is a single **fact** (an edge): *the user → relation → value*, with its own
metadata. Crucially, **metadata lives on the fact, not on any group** — that's what lets facts about
the same thing age independently.

```jsonc
{
  "id": "f_12",
  "category": "location",          // broad grouping: location | food | person | work | ...
  "relation": "home",              // the specific role / slot (this is the "context tag")
  "value": "Edinburgh",            // the thing
  "occupancy": "single",           // single → new value supersedes; multi → accumulates
  "asserted": "2026-01-10T09:00Z", // when it became true / was last reaffirmed
  "expiry":   null,                // when it stops being assumed true (null = indefinite)
  "status":   "active",            // active | superseded | retired
  "supersededBy": null,            // link to the fact that replaced it
  "source":   "\"I live in Edinburgh\" — 10 Jan"   // provenance, for the confirm-gate
}
```

**Relation, not value, is the slot.** `(location, home)` is a slot; `(location, favourite-city)` is a
different slot. They can share a value (`London`) and still be different facts. *The value is not the
key — the relation is.*

**Occupancy** decides what happens on a same-relation collision:

- `single` (`home`, `current-city`, `diet`) → one live value; a new value **supersedes** the old.
- `multi` (`favourite-restaurant`, `language`, `pet`) → many live values; a new value **adds**.

**Two views over the same facts (no duplication):**

- **By relation** (attribute): *"where do they live?"* → `relation=home, status=active` → one answer.
- **By value** (entity): *"tell me about Edinburgh"* → every fact where `value=Edinburgh` → home (past),
  favourite-city (present). The co-located "everything about X" view is just a query; each fact still
  carries its own age and status.

---

## 3. Write rules (deterministic, inline, instant)

When a new fact is proposed, match it against existing facts by `(relation, value)`:

| New fact vs existing | Action |
|---|---|
| No existing `relation` | **Insert** (new slot). |
| Same `relation` + same `value` | **Merge / reaffirm** — bump `asserted`, refresh `source`, enrich. |
| Same `single` `relation`, **new** `value` | **Supersede** — old → `status: superseded`, `supersededBy` = new; insert new `active`. |
| Same `multi` `relation`, new `value` | **Add** — both stay `active`. |

This is cheap and must-be-right, so it runs **inline at write time** — never farmed to the LLM.

---

## 4. Time & staleness

- `asserted` (back end) powers sort-by-time and "how old is this." `expiry` (front end) is an explicit
  end (`"in Edinburgh till Friday"`). Both are needed.
- **Stateful facts age into "last known."** Past a freshness window, the system stops asserting and
  hedges — *"last I knew you were in Edinburgh — still there?"* — rather than stating it as current.
- When surfacing a fact, the system **injects its age** (*"asserted 23 days ago"*) so the timeless
  model can reason about staleness. Same mechanism as injecting the current date.
- "How does it know I left Edinburgh?" — **it doesn't, and must not pretend to.** It relies on
  supersession (a newer `home`) plus staleness hedging, never on silent assumption.

---

## 5. Reading / surfacing (the "wow")

- **Inject "what you know about the user"** into the orchestrator's prompt every turn (like the date /
  running-tasks blocks). This is the magic: it *uses* what it knows, unprompted.
- **Transparency:** when it captures something, it says so (*"noting you're vegetarian"*), so you see
  it listening and can correct it.
- **Conflict → ask, don't guess.** If facts disagree (`favourite` = Edinburgh, `home` = London), it
  asks — *"your old favourite's up in Edinburgh but you're in London now — somewhere local, or the old
  one?"* That question *is* the secretary feeling.

---

## 6. Forgetting (soft, reversible)

- `forget` sets `status: retired` — **never a hard delete.** Recoverable.
- Triggered by explicit (*"forget that"*) or implicit signals (*"I don't really do that anymore"*,
  contradicting an old fact). Because retire is reversible, it's *safe* to act on a soft hint: if it
  over-reads you, nothing's lost — you say "no, I still do" and it's back.
- For a single-occupancy relation, reassigning the value (new `home`) implicitly retires the old —
  the slot moves, the history stays.

---

## 7. The async janitor ("gremlin")

Keeps memory work **off the conversation's critical path** — you never wait on it — and handles only
the *fuzzy, judgemental* reconciliation that shouldn't block (or be done by a rigid rule):

- ✅ May: propose merges, flag likely contradictions, mark facts stale, suggest enrichments, decay
  confidence on aged stateful facts.
- ❌ May **not**: silently delete, rewrite, or merge anything. It is a **proposer**, not an editor.

> An unreliable model invisibly rewriting your life-facts in the background is the single scariest
> failure mode — it's the "808 MB" bug, but for who you are, where you can't see it. The gremlin
> proposes; commits stay deterministic (inline) or confirmed (surfaced).

---

## 8. Provenance & memory-driven actions

- Every fact carries `source` (the quote + when). Decisions that lean on memory **cite it**.
- **Cheap & reversible** (suggest a restaurant) → the model reasons from memory freely.
- **Expensive & real-world** (book / send / buy) → a **confirmation gate** that surfaces the
  *specifics* + the memories used + their age:

  > *"Booking **The Witchery, Rose Street, Edinburgh** — that right? You mentioned it months ago,
  > before you moved to London."*

  Naming the specific place + city + provenance is what makes a stale assumption obvious *before* it's
  irreversible. The model is allowed to be wrong because the gate makes wrongness visible in time.

---

## 9. Worked example — moving house

```
Jan:  "I live in Edinburgh"           → f1 {location, home,           Edinburgh, single, active}
Jan:  "Edinburgh's my favourite city" → f2 {location, favourite-city, Edinburgh, multi,  active}
Jun:  "I've moved to London"          → f3 {location, home,           London,    single, active}
                                         f1 → status: superseded, supersededBy: f3
```

- *"Where do I live?"* → `home, active` → **London.** ✅
- *"Tell me about Edinburgh"* → `value=Edinburgh` → *"your favourite city (still); was home until June."* ✅
- *"Book my favourite restaurant"* (favourite is an old Edinburgh spot) → suggestion is fine; **booking**
  surfaces *"The Witchery, Edinburgh — you're in London now, still that one?"* → you catch it. ✅

---

## 10. Build phases

**Phase 1 — the loop that makes it feel like it listens** *(build first):*
- Persistent fact store + the schema + inline deterministic write rules (§3) + soft-retire.
- `remember` / `recall` / `forget` tools on the orchestrator (reusing the existing tool system).
- Surface "what you know about the user" into the prompt each turn (§5).
- Transparency on capture.

**Phase 2 — getting smarter & staying honest:**
- The async janitor (§7) — propose contradictions / merges / stale flags.
- Time-decay and "last known" hedging (§4).

**Phase 3 — memory that acts:**
- Confirmation gates on memory-driven irreversible actions (§8).
- Contacts / relationships as an application of the same store (people are just entities with facts).

---

*The through-line: the model proposes, deterministic rules own what must be right, everything fuzzy is
surfaced and reversible, and time is always grounded. That's how memory feels like a person instead of
a database — without ever quietly getting you wrong.*
