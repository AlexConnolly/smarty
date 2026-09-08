# Smarty — Proact Specification

An agent that decides for itself what would be useful, does the safe half of it, and keeps a record you can read.

Everything until now has been **pulled**. A panel refreshes when its timer says so, a task runs when it was
scheduled, a watcher fires when something arrives. Even the two things that look proactive are reactive underneath:
a watcher waits for an item, and the panel suggester only ever offers *a panel*. Nothing in the system has ever
woken up and asked **"what would help this person today?"**

That is what Proact is.

---

## 0. It is not "Always On"

`Always on` is already taken: it is the hands-free voice mode — wake word, giant captions, speaks its answer back
(`Smarty.Chat/src/AlwaysOn.tsx`). Reusing the name would collide in the UI, in the settings, and in every
conversation about either feature. **Proact** is the name.

---

## 1. The one rule

> **Proact may prepare. It may never commit.**
> It may write, never send. Gather, never spend. Draft, never dispatch.

Everything else in this document follows from that sentence, and it is the reason Proact is safe to leave running
unattended a hundred times a day.

The instinct is to write the rule as a list of banned actions — no bookings, no deletions, no emails. That list is
unbounded and always one item short of the thing that goes wrong. The rule above is bounded instead: it asks one
question of any candidate action, and the question has a checkable answer.

| question | answer | verdict |
|---|---|---|
| Does it change anything outside this machine? | yes | **refuse** |
| Does it cost money, or commit the user to anything? | yes | **refuse** |
| Would a reasonable person want a say before it happened? | yes | **propose it instead** |
| Is it reversible by closing a tab? | yes | allowed |

Note what this rule *permits*, because it is more than it sounds. Reading everything the user has. Doing real
research. Writing a document. Building a panel. Drafting a message and leaving it unsent. Working out that two
things in their week collide. Preparing the thing that makes the decision easy — and leaving the decision.

And note the taste problem it dissolves. "Don't do things outside the user's taste" cannot be enforced by asking a
model to have good taste; it will be confidently wrong eventually. Making the *category* safe rather than the
*judgement* safe means a lapse of taste costs a mediocre suggestion, never a booked restaurant.

**Booking a table is out. Finding three places, checking which have tables at 8pm on Friday, and saying which one
fits what they like — that is exactly the job.**

---

## 2. What an action is

The unit Proact produces, and the thing the record is made of. Not "a thought" and not "a message" — an action is
**something it did, with something to show for it**.

| field | meaning |
|---|---|
| `at` | when it happened |
| `kind` | `noticed` \| `prepared` \| `proposed` \| `looked` |
| `what` | one line, in the user's terms, not the system's |
| `why` | what about their situation prompted it |
| `produced` | the artefact: a file, a panel, a draft, a finding — or nothing |
| `cost` | tokens and wall-clock, because a loop that runs for ever has to be affordable |
| `seen` | has the user looked at it |

### Two directions, and the outward one is the point

Before the kinds, the thing that decides whether any of this is worth having. An action can face **inward** — at
what they already have — or **outward**, at what they do not.

| | |
|---|---|
| **Inward** | Two things on Thursday clash. A deadline is nearer than it looks. Two separate arrangements have a consequence nobody joined up. |
| **Outward** | They keep a list of places to eat around Fitzrovia — so what has *opened*? They have a project on a subject — so what is *new* about it? |

The first build got only the inward half, and it was a real design fault rather than a missing prompt line. Framing
Proact as watching for problems in their own data makes **"nothing has changed" a reason to do nothing** — which is
the normal state of a week, so it declined almost every time. It was a watchman when it needed to be an assistant.

> **A subject somebody keeps is a standing invitation to bring them something new about it.** Nothing has to have
> changed on their side; the world moved, or Proact simply had not looked yet.

Their own projects and lists are how it works out **what** to look for. They are almost never where the answer is.
Reading their material and handing it back is worth nothing — they can already see it.

The guard against this becoming a stream of generic recommendations is not caution, it is *specificity*: the obvious
answer to a standing interest does not count. Telling somebody who keeps a list of restaurants the name of the most
famous one in their city is a guess they could have made themselves. Recent, local, particular, and checked is
research.

The four kinds are deliberately few:

- **noticed** — a fact about their situation they would want to know. *Two things on Thursday are in different
  cities.* No artefact, and often the most valuable one.
- **prepared** — did the work, left it ready. A document, a shortlist, a draft, a gathered set of numbers.
- **proposed** — something it will not do unattended, reduced to a tick or a cross. This is where bookings,
  purchases and anything irreversible land, always as a proposal, never as an act. It carries the most weight of
  the four, so it has its own section — see §3.
- **looked** — a deep dive that found nothing worth reporting. Recorded because a loop that only logs its wins
  reads as cleverer than it is, and because "I already read that" is what stops it reading the same thing weekly.

`seen` is what makes the surface honest and what stops repetition. An unseen action is not a licence to do it again
differently.

---

## 3. A proposal is the whole point

The other three kinds are Proact working. **A proposal is Proact reaching the edge of §1 and handing over.** It is
where everything it cannot do unattended goes, so it is the mechanism that decides whether §1 feels like safety or
like a straitjacket.

### The tick is the commit

The tension has to be named. §1 says Proact may never commit. A tick means the thing happens. Both are true, and
this is how:

> Proact prepares the commit. **The user's tick executes it. The assistant carries it out.**

§1 holds without qualification — Proact itself never commits, before or after the tick. What it does is reduce an
irreversible act to a single, fully-formed, one-click decision by the only party entitled to make it. Ticking a
proposal to book a table does not authorise Proact to book tables; it dispatches *this* booking, once, as a task, to
the assistant that already does that work when asked.

Which means acceptance is not a state change on the proposal. It is a handover, and a proposal is only worth
offering if that handover is genuinely ready to run.

### What a proposal has to contain

Two things, and they are not the same thing:

| | | |
|---|---|---|
| **What** | The outcome, one line, in their terms | *"Book Trullo, 8pm Friday, table for two"* |
| **Why now** | What about their situation prompted it | *"Friday is free, and it's the anniversary you have on your list"* |
| **The plan** | The steps that will actually run on a tick | *"Open Trullo's booking page, 8pm Friday, 2 covers, your name and number, confirm"* |
| **The catch** | What cannot be undone, when it must be decided by | *"Cancellation is free until Thursday. Tables at 8pm are gone by Wednesday"* |

**The plan is not explanatory copy.** It is the actual sequence, written so a person can read it in five seconds and
know what they are authorising. That distinction matters because the instinct is to write a paragraph about how
Proact works, and nobody needs that — the plan is there to make ticking *safe*, not to be interesting.

**The catch is mandatory on anything irreversible.** A tick on something that cannot be undone must show the
irreversibility before the click, not after. This is the one place a proposal is allowed to slow the user down.

### Tick or cross. Nothing else.

No edit, no "accept with edits", no form, no reason required for a cross. The panel proposal flow has an
accept-with-edits sheet; this deliberately does not.

The consequence, stated honestly: **no edit affordance means the plan has to be right.** A plan that is nearly right
gets a cross, and Proact has to have been good enough to avoid that. That raises the bar on proposing, which is the
correct direction — a proposal is a decision the user owes, and offering a vague one is asking them to do the
thinking Proact was supposed to do.

A cross needs no reason because requiring one makes people avoid the cross altogether, and a proposal nobody
answers is worse than one that is refused.

### A proposal must be executable before it is offered

The same lesson the panel gate just taught, applied here: **do not offer what you have not proved.** A proposal
whose plan cannot actually run is the silent-failure pattern again, except the user has now spent a click on it and
trusted it.

So the feasibility work happens *before* proposing, not after ticking:

- if it proposes a table at 8pm Friday, it has established there is a table at 8pm Friday
- if it proposes sending a draft, the draft exists and is attached
- if it names a price, it read the price
- if it cannot establish those things, **it proposes nothing** — an unverified proposal is a `noticed` at best

This is exactly the `feed_publish` / `widget_publish` bargain in a third place: the expensive check happens once, at
the moment of offering, so the cheap thing afterwards is trustworthy.

### What becomes of one

- **Ticked** → dispatched to the assistant as a task, and the timeline shows the proposal *and* the outcome. A tick
  that vanishes into "accepted" with no visible result is how trust in the whole feature dies.
- **Crossed** → recorded, and never re-offered, **not even reworded**. This is already the suggester's rule and it
  earned it. A cross is signal about this person, and the pattern of crosses is more informative than any single one.
- **Expired** → proposals go stale and must say so rather than sitting there. "Book for tonight" is meaningless
  tomorrow, and a timeline of dead proposals is a timeline nobody reads. Every proposal carries its own use-by,
  taken from the `catch`.

### How many can be outstanding

This revises the "one outstanding proposal at a time" rule that §5 inherited from the panel suggester. That rule
exists because an unanswered proposal means the last one was not worth making — but it was written for a flow with a
form in it. A tick is one click, so a small number of clear decisions is reasonable where a queue of forms was not.

**A low cap, not one — and they expire.** Outstanding proposals are decisions the user owes, and an unpaid pile of
decisions is a debt that makes the whole surface feel like work. Expiry does most of the job here; the cap is there
so a bad day cannot produce fifteen.

---

## 4. Two modes, and why one is not enough

The brief asked for a ten-minute loop *and* for deep dives into vaults. Those are different jobs at different
prices and they cannot be the same tick.

### Attend — cheap, every tick, usually silent

Runs on the user's chosen interval (§5). Looks at what has *changed* since last time: the lists, the projects, what
arrived on the feeds, what was said in chat. Decides whether anything obvious is worth doing. **The overwhelming
majority of Attend ticks must end with no action at all**, and that has to be an explicitly good answer, not a
failure — the panel suggester already learned this the hard way and says so in its own prompt.

Attend is gated *before* the model wherever possible: if nothing has changed since the last tick, there is nothing
to think about and the tick costs nothing. This is the same economics as `FeedTick`, which runs free filters first
and only reaches a model for an item that already passed them.

That gate matters more the faster the dial is set. Most five-minute windows in a person's day contain no change at
all, so at the fast end the great majority of wakeups should cost nothing whatsoever — not "a cheap model call",
nothing. If a five-minute setting is expensive, the gate is not working.

### Discover — expensive, rare, and NOT on the dial

One thing, properly. Read a whole project, a whole folder, the whole history of a topic, and come back with
something that is not on the surface. This is where "oh wow, that's cool" actually lives.

**Discover keeps its own pacing, independent of the Attend interval.** Someone who sets five minutes wants to be
attended to more closely; they do not want twelve times as many deep dives. There are only so many deep things to
find in one person's week, and a Discover that runs hourly becomes a Discover that reports shallow things hourly.
A few times a day at most, whatever the dial says, and never twice on the same thing without a reason.

So the dial governs attentiveness, not depth. Turning it to five minutes makes Proact notice sooner; it does not
make it dig more.

**The distinction is load-bearing.** Collapse the two and you get the worst of both: an expensive loop that says
obvious things often.

---

## 5. The loop, and what restrains it

**Decided: the user sets how often it goes out — every 5, 10, 30 or 60 minutes.**

Four choices, not free text. It stores as the same `"every N minutes"` string feeds and schedules already use, so
`ScheduleStore.TryParseRepeat` parses it and nothing new needs writing. Four fixed options also means nobody can
ask for every thirty seconds.

The dial matters more than it looks, because **it is the rate control**. There is no daily action cap (below), and
the honest consequence is that the interval is what stands in its place: sixty minutes instead of five is a twelve-
fold reduction in chances to act, chosen by the person who has to live with the output. That is a better place for
the decision than a constant in the source — the right cadence depends on how much is going on in someone's week,
which is not knowable from here.

At the fast end the numbers are large: five minutes is **288 wakeups a day**. It must not mean 288 pieces of work.
The heartbeat is frequent; the acting is rare, and two things follow from that which are dealt with below — Discover
does not scale with the dial (§4), and the tick log cannot be enumerated into the prompt (§8).

**Decided: no fixed daily cap. Proact acts whenever it judges something is clearly worth doing.**

That is a deliberate choice of the higher ceiling, and it means the restraint has to come from the quality gate
rather than from a counter. So the gate has to be real, and it is built from the four things that are *not* a cap:

- **"Nothing worth doing" is an explicitly good answer**, and most Attend ticks must give it. The panel suggester
  already carries this instruction and it is the single most important line in its prompt.
- **The 24-hour log is in front of it on every tick.** Repetition is the commonest way an uncapped loop turns into
  noise, and seeing "I did that at 08:40" is what prevents it.
- **One outstanding proposal at a time.** Not a rate limit — a coherence rule. Two unanswered proposals means the
  first one was not worth making.
- **It must be able to name the thing concretely.** If it cannot say what it did and why in the user's own terms,
  it does not act. This is what stops the generic-and-corny class of output at source.

Two ceilings remain, because neither is a cap on *actions*:

- **Cost per Discover run.** A deep dive that reads a thousand files is a bug regardless of how good the finding
  is. Bounded per run.
- **An action-rate cap exists in config and defaults to off.** Not to quietly reinstate what was declined, but
  because the honest position is that nobody knows the right number yet, and the day you want it is the day it is
  already annoying you. One setting, not a rebuild.

The reason to be careful here, stated plainly: the rule in §1 handles *danger*, so the live risk in this design is
**noise**, and noise is what kills the feature rather than breaking it. A Proact producing eleven mediocre things a
day gets switched off in a week and never switched back on. One genuinely good thing a day is a feature people
would pay for. With no cap, the timeline and the digest (§9) are the instruments that make over-production visible
early — so the actions-per-day figure is shown, prominently, from the first version.

---

## 6. What it knows

Proact's whole value is that it is not a chatbot with a clock — it knows this person. What is available today:

| source | what it gives | state |
|---|---|---|
| Brain | who they are, preferences, standing facts, audience-scoped | built |
| Lists & agenda | what they have to do, and when | built |
| Projects | what they are working on, and what is due | built |
| Location | where they are right now | built |
| Chats | what they have been talking about | built |
| Feeds & watchers | what has been arriving | built |
| Sources | granted local folders — the "vaults" | built, **none granted** |
| Plugins | devices and services (Roborock today) | built |
| **Calendar** | **their actual plans** | **missing** |

Two gaps matter and both are called out here rather than discovered later.

**There is no calendar integration.** Grepping for it finds only a prompt line telling a worker that "put it in my
calendar" means *open the site in the browser and use it as the user would*. That is fine for a task someone asked
for; it is too slow and too fragile to sit inside a ten-minute loop.

> **Decided: structured sources on every Attend tick; the calendar read only inside a Discover, through the
> browser.** Attend runs on lists, projects, agenda, location, feeds and the brain — all cheap and local. Discover
> is already rare and already expensive, so a slow, fragile, browser-driven calendar read is affordable there and
> nowhere else. A proper calendar integration stays on the table as its own piece of work; Proact does not wait for
> it.

The consequence to accept honestly: between Discovers, Proact's picture of the user's plans is as good as what is
in their lists and projects. Anything living only in the calendar is invisible on an Attend tick.

**No local sources are granted.** `sources.json` is empty, so there is no vault to dive into yet.

> **Decided: internal first, folder next.** Discover ships against projects, lists and chat history — the
> accumulated record of what they are working on and have talked about, which is richer than it sounds. Granting a
> real folder follows once the deep-dive mode has shown it earns its cost at all.

---

## 7. What it may touch

Derived from §1, made concrete against the tools that exist.

**Allowed**
- read anything the user has granted — brain, lists, projects, chats, files, feeds
- research: browse, fetch, read, cross-reference
- write files into its own workspace, and hand them over
- build or fix a panel (reversible, visible, already gated by the publish proof)
- draft anything at all, and leave it as a draft
- record a `noticed` or a `proposed`

**Refused, always, and refused by *construction* rather than by instruction**
- sending: no message, email, or post leaves the machine
- buying, booking, reserving, cancelling
- deleting or overwriting the user's own data
- anything on someone else's behalf, or to someone else
- changing settings, permissions, or grants
- starting anything that outlives the tick without saying so

The word *construction* is doing real work in that sentence. Proact must not be handed a sending tool and told to
be careful, in the same way an adjust task is not handed a browser and told not to go shopping — the toolset is the
boundary, and the prompt is only the explanation. A capability it does not hold cannot be talked into.

---

## 8. The record

Fully traceable, per the brief. For every tick, whether it acted or not:

- when it woke, which mode, what it looked at
- what it decided, and *why it decided to do nothing* when it did nothing
- every action, in the shape of §2
- tokens and time spent
- errors, kept — a Proact that quietly stopped is the worst outcome, and this system has already been bitten by
  exactly that on both feeds and panels

### Ticks are recorded; actions are what reach the prompt

The 24-hour window in the next tick's prompt is the brief's requirement and the main defence against repetition —
but it cannot be *every tick*. At a five-minute interval that is 288 entries a day, nearly all of them "looked,
nothing had changed", and pasting them in would crowd out the very thing the window is for while growing the prompt
twelvefold for no information.

So the two are separated:

- **Actions** (§2) go into the prompt in full, with times. This is the "so far today I have done X at Y" the brief
  asks for, and it is what stops Proact noticing the same thing twice.
- **Ticks** are recorded for the timeline and for liveness, and reach the prompt only as a count: *"41 ticks since
  08:00, 2 actions."* Enough for it to know whether it has been busy or quiet, at a fixed cost whatever the dial is
  set to.

The count is not padding. A model that can see it has acted twice in forty-one looks behaves differently from one
that thinks it has just woken up for the first time.

Retention beyond 24 hours is for the user's benefit rather than the model's: a rolling window of a few weeks makes
"is this thing actually any good?" an answerable question. Which it must be, because the honest answer might be no.

---

## 9. Its own area

Proact gets its own surface, not a corner of an existing one. It is a **timeline**: what it did, newest first,
grouped by day, each entry expandable to the reasoning and the artefact.

Why not the existing surfaces:

- **not chat** — a hundred ticks a day would bury the conversation, and most ticks have nothing to say
- **not nudges** — those belong to watchers, and mean "something happened that you asked to be told about"
- **not the home page** — panels are live state; this is a history

The timeline answers four questions at a glance: what has it done today, **how much** it has done today, was any of
it useful, and is it still running.

The third and fourth matter most. A silent Proact and a broken Proact look identical from outside, and this system
has made that mistake twice already. And with no action cap (§5), the actions-per-day figure is not a statistic —
it is the instrument that tells you whether the quality gate is holding.

### The daily digest

> **Decided: quiet timeline, plus a daily digest.** Nothing interrupts in the moment. Once a day, one summary:
> *here is what I did, and here is what is waiting for you.*

This is the combination that fits the brief's own framing — "so far today I have done X at Y time" is a digest
sentence, not a notification. It also has a property the alternatives lack: it is **impossible to spam**. One
message a day is one message a day whether Proact did two things or twenty, so the digest never becomes the noise
it is meant to summarise.

The cost is that something only useful in the moment can go stale in the timeline. That is accepted for the first
version: an in-the-moment channel already exists for things the user explicitly asked to be told about, and it is
called a watcher. If Proact turns out to need urgency, that is a later addition with evidence behind it rather than
a guess now.

The digest's other job is honesty. It reports what Proact *did not* do as well as what it did — the ticks that ended
in nothing, the deep dive that found nothing worth saying. A digest of only wins reads as cleverer than the thing
actually is.

---

## 10. The toggle and the dial

Two controls, and they are separate things.

**The toggle.** Off by default. On is a deliberate act, and while it is on the user can see that it is on. Off means
the loop does not run — not that it runs and discards. The record survives being switched off, so turning it on
again resumes with its history rather than as an amnesiac.

**The dial.** How often it goes out: **5, 10, 30 or 60 minutes**. Changing it takes effect on the next tick rather
than at the far end of the old interval — the same courtesy a feed gets when its cadence is edited, and for the same
reason: someone who has just turned it up wants to see that it did something.

Kept separate rather than folded into one control with an "off" position, because they answer different questions.
The toggle is "is this running at all", which is a trust decision. The dial is "how attentive should it be", which
is a taste decision, and the reason to change one is almost never the reason to change the other.

A pause-for-today is worth having on top of both, because the realistic reason for reaching for a switch is "not
now" rather than "never".

---

## 11. What would make this fail

Written down first, on the grounds that every fault in this system so far has been something nobody wrote down.

1. **Noise.** The dominant risk. Eleven mediocre things a day and it gets switched off. §5 is the answer.
2. **Corniness.** "Hey, have you thought about…" with no knowledge behind it. The test the panel suggester
   already uses is the right one: *whose* is it? A thing drawn from their actual week beats anything generic, and
   if it cannot be named concretely it should not be said at all.
3. **Silent death.** It stops and nothing says so. Feeds and panels both did this. The timeline has to show
   liveness, not just output.
4. **Cost drift.** A deep dive that reads a thousand files, every day, for ever. Discover needs a ceiling per run,
   not just per day.
5. **Repetition.** Noticing the same thing on tick 40 that it noticed on tick 12. The 24-hour log is the defence
   and it only works if it is genuinely in the prompt.
6. **Doing something it shouldn't.** Lowest probability, highest cost, and the only one already fully answered —
   by §1 and by §7's insistence that the toolset, not the prompt, is the boundary.
7. **The tick that went nowhere.** A proposal is accepted and nothing visibly happens, or it half-happens, or it
   fails quietly. This is the most expensive failure in the document that is not a safety failure: the user spent
   trust on one click, and a click that does nothing teaches them never to click again. §3 requires the outcome back
   on the timeline for exactly this reason — and it is why a proposal has to be proved executable *before* it is
   offered rather than discovered to be impossible after acceptance.
8. **Vague proposals.** With no edit affordance, a nearly-right plan can only be crossed. Enough of those and the
   tick/cross surface becomes a chore rather than a convenience — which is the same noise failure as §11.1, arriving
   through the one interaction that most needed to stay effortless.

---

## 12. Decisions taken

| | decision | where |
|---|---|---|
| Proposals | **Tick or cross, nothing else.** Must carry what/why/plan/catch, must be proved executable before it is offered, and a tick dispatches it to the assistant as a task | §3 |
| Cadence | **User's choice of 5, 10, 30 or 60 minutes.** Governs Attend only | §5, §10 |
| Action budget | **No fixed cap.** Quality-gated, rate made visible, dormant cap in config | §5 |
| Calendar | Structured sources on every Attend tick; calendar read only inside a Discover, via the browser | §6 |
| Notification | Quiet timeline, plus one daily digest. Nothing interrupts in the moment | §9 |
| Discover targets | Projects, lists and chat history first; grant a real folder afterwards | §6 |

The uncapped rate is the one deliberate bet in this document. It buys the higher ceiling on usefulness and it
spends the main safeguard against the failure mode most likely to occur (§11.1).

The dial is what makes that bet reasonable. With no daily cap, the interval is the rate control — and it sits with
the person who has to read the output rather than in a constant here. Someone finding Proact too talkative has an
obvious first move that does not require anyone to change any code. Everything in §5 that is not a cap carries the
rest of the weight, and the actions-per-day figure is on the timeline from day one so the bet stays observable
rather than assumed.

---

## 13. Still to settle before building

Not blocking the definition, but each needs an answer during the build:

1. **Which model runs a tick.** Attend must be cheap enough to run 144 times a day; Discover wants the best
   available. Almost certainly not the same model.
2. **What the low cap on outstanding proposals actually is** (§3). Expiry does most of the work; the number is
   there only so a bad day cannot produce fifteen, and it wants a real answer rather than a guess.
3. **Where a ticked proposal's task shows up.** It is dispatched to the assistant (§3), and the question is whether
   that appears in the Proact timeline, in chat, or in both. Both, probably — but they are different records.
4. **Whether Discover is scheduled or opportunistic.** A fixed few-times-a-day slot, or triggered when Attend
   notices something worth going deep on. Either way it does not follow the dial (§4).
5. **Digest timing.** A fixed hour, or the end of the user's day inferred from their own activity.
6. **Whether the dial should also govern quiet hours.** Five-minute attentiveness at 03:00 is 36 wakeups nobody
   wanted. Probably a separate "don't between these hours" rather than another cadence option, but it needs
   deciding — and it is the sort of thing that is obvious in week two and irritating in week one.
