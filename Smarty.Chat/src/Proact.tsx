import { useEffect, useState } from 'react'
import {
  answerProposal,
  dismissProact,
  fetchProact,
  markRoundupSeen,
  pauseProact,
  type ProactAction,
  type ProactDoing,
  proactDoing,
  type ProactImage,
  type ProactLink,
  proactLookNow,
  type ProactState,
  type ProactStep,
  proactThread,
  sendMessage,
  setProactEvery,
  setProactOn,
  stepDetail,
  voteProact,
} from './api'
import { Markdown } from './Markdown'
import { Sheet, SheetButton, SheetSection } from './Sheet'

/**
 * Proact, where the person actually is.
 *
 * It lived in the control surface first, which was wrong: Control is for settings — what is switched on, how often,
 * which plugin — and this is the WORK. Somebody is not going to open an admin page to find out that two things in
 * their Thursday are a hundred miles apart, and a decision waiting on a tick belongs in front of them rather than
 * behind a tab they visit when something is broken.
 *
 * Three things, in the order they matter:
 *
 *  - the ROUNDUP, when there is an unread one. The only part of Proact that reaches out.
 *  - what it is WAITING ON. A tick or a cross, nothing else, because it is the only thing here the person owes.
 *  - what it DID. Quiet, below the fold of attention, there to be glanced at rather than worked through.
 */
export function useProact(refreshMs = 20000) {
  const [state, setState] = useState<ProactState | null>(null)

  const load = () => fetchProact().then(setState)

  useEffect(() => {
    void load()
    const timer = setInterval(() => void load(), refreshMs)
    return () => clearInterval(timer)
  }, [refreshMs])

  return { proact: state, reloadProact: load }
}


/**
 * The sources, as things to open.
 *
 * The next thing a person does with a recommendation is go and look at it, so a finding they have to search for again
 * has made work rather than saved it. Labelled rather than bare urls, because "soraya.london" tells you less than
 * "their own site" does.
 */
function Sources({ links }: { links?: ProactLink[] }) {
  if (!links || links.length === 0) return null
  return (
    <div className="mt-2 flex flex-wrap gap-1.5">
      {links.map((l) => (
        <a
          key={l.url}
          href={l.url}
          target="_blank"
          rel="noreferrer noopener"
          className="rounded-full border border-line px-2.5 py-1 text-[0.6875rem] font-medium text-ink-soft transition hover:border-accent hover:text-accent"
        >
          {l.label} ↗
        </a>
      ))}
    </div>
  )
}

/**
 * The pictures.
 *
 * Every one of these was fetched before it was stored, so they load — but onError still hides the frame, because a
 * url that worked an hour ago can stop working and a broken-image icon is the one thing this must never show.
 */
function Pictures({ images }: { images?: ProactImage[] }) {
  if (!images || images.length === 0) return null
  return (
    <div className="mt-2.5 flex gap-2 overflow-x-auto">
      {images.map((img) => (
        <figure key={img.url} className="min-w-0 shrink-0">
          <img
            src={img.url}
            alt={img.caption ?? ''}
            loading="lazy"
            onError={(e) => {
              const fig = (e.target as HTMLImageElement).closest('figure')
              if (fig) fig.style.display = 'none'
            }}
            className="h-28 w-auto max-w-[15rem] rounded-lg object-cover"
          />
          {img.caption && (
            <figcaption className="mt-1 max-w-[15rem] truncate text-[0.6875rem] text-ink-mute">{img.caption}</figcaption>
          )}
        </figure>
      ))}
    </div>
  )
}

/**
 * Thumbs.
 *
 * The only signal the user volunteers that GENERALISES. A cross on a proposal says "not this thing"; a thumb says
 * "not this kind of thing", which is what the next decision actually needs — so this outlives the 24-hour log and
 * goes into every later prompt as taste. Clicking the same thumb again clears it, because a mis-tap should be
 * undoable without a menu.
 */
function Thumbs({ action, onVoted }: { action: ProactAction; onVoted: () => void }) {
  const [busy, setBusy] = useState(false)

  const vote = async (v: 'up' | 'down') => {
    setBusy(true)
    await voteProact(action.id, action.verdict === v ? null : v)
    setBusy(false)
    onVoted()
  }

  return (
    <div className="flex shrink-0 items-center gap-0.5">
      <button
        onClick={() => void vote('up')}
        disabled={busy}
        title="Useful"
        className={cxLocal(
          'rounded px-1.5 py-0.5 text-[0.8125rem] transition',
          action.verdict === 'up' ? 'text-accent' : 'text-ink-mute hover:text-ink',
        )}
      >
        ↑
      </button>
      <button
        onClick={() => void vote('down')}
        disabled={busy}
        title="Not useful"
        className={cxLocal(
          'rounded px-1.5 py-0.5 text-[0.8125rem] transition',
          action.verdict === 'down' ? 'text-accent' : 'text-ink-mute hover:text-ink',
        )}
      >
        ↓
      </button>
    </div>
  )
}

/**
 * Markdown, flattened for a one-line preview.
 *
 * The body is written as markdown because the dialog renders it properly, and the preview is a single truncated line
 * where bold and bullets have nowhere to go. Rendering it there is not the answer — a line of mixed weights reads
 * as broken — but nor is showing the source, which is what it did: the front page carried a literal
 * "**Mareida** — Chilean fine-dining restaurant" with the asterisks in it.
 *
 * Deliberately not a markdown parser. It takes the syntax out of a sentence, and anything it does not recognise it
 * leaves alone, because the worst outcome here is a preview that eats a character it should have kept.
 */
function plain(markdown: string): string {
  return markdown
    .replace(/!\[[^\]]*]\([^)]*\)/g, '')
    .replace(/\[([^\]]*)]\([^)]*\)/g, '$1')
    .replace(/^\s{0,3}#{1,6}\s+/gm, '')
    .replace(/^\s{0,3}>\s?/gm, '')
    .replace(/^\s{0,3}[-*+]\s+/gm, '')
    .replace(/(\*\*|__)(.*?)\1/g, '$2')
    .replace(/(?<![*\w])[*_](?=\S)([^*_]+?)(?<=\S)[*_](?![*\w])/g, '$1')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/\s+/g, ' ')
    .trim()
}

function cxLocal(...parts: (string | false | undefined)[]) {
  return parts.filter(Boolean).join(' ')
}

/**
 * The day, summed up — shown only while it is unread.
 *
 * Dismissed by reading it, not by a timer: a roundup that vanished on its own would be the one piece of Proact that
 * could go unseen entirely, which is the opposite of why it exists. And only ever one, so this can never be the
 * thing that clutters the page.
 */
export function RoundupCard({ state, onRead }: { state: ProactState; onRead: () => void }) {
  const unread = state.roundups.find((r) => !r.seen)
  if (!unread) return null

  return (
    <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '45ms' }}>
      <div className="rounded-lg border border-line bg-surface px-4 py-3.5">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="text-[0.75rem] font-semibold uppercase tracking-wider text-ink-mute">
              {new Date(unread.at).toLocaleDateString(undefined, { weekday: 'long' })} so far
            </div>
            <p className="mt-1.5 whitespace-pre-line text-sm text-ink">{unread.text}</p>
          </div>
          <button
            onClick={async () => {
              await markRoundupSeen(unread.day)
              onRead()
            }}
            className="shrink-0 text-[0.6875rem] font-medium text-ink-mute hover:text-ink"
          >
            Got it
          </button>
        </div>
      </div>
    </section>
  )
}

/**
 * The tick and the cross.
 *
 * One click either way and no third option — no edit, no form, no reason required for a no. That is a promise about
 * the proposal rather than a shortcut in the UI: everything needed to decide has to be on the card, because there is
 * nowhere else to go and nothing to fill in.
 */
export function WaitingOnYou({
  state,
  onAnswered,
}: {
  state: ProactState
  onAnswered: () => void
}) {
  const [busy, setBusy] = useState<string | null>(null)
  const [wrong, setWrong] = useState<string | null>(null)

  const waiting = state.actions.filter((a) => a.answer === 'pending')
  if (waiting.length === 0) return null

  const answer = async (a: ProactAction, ticked: boolean) => {
    setBusy(a.id)
    setWrong(await answerProposal(a.id, ticked))
    setBusy(null)
    onAnswered()
  }

  return (
    <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '55ms' }}>
      <div className="text-[0.75rem] font-semibold uppercase tracking-wider text-ink-mute">Shall I?</div>
      <div className="mt-2.5 space-y-2.5">
        {waiting.map((a) => (
          <div key={a.id} className="rounded-lg border border-accent bg-accent-soft px-4 py-3.5">
            <p className="text-sm font-medium text-ink">{a.what}</p>
            <p className="mt-0.5 text-[0.8125rem] text-ink-soft">{a.why}</p>

            {/* What one click authorises. Not an explanation of how any of this works — the steps themselves. */}
            {a.plan && (
              <p className="mt-2.5 whitespace-pre-line rounded-md bg-surface/70 px-3 py-2 text-[0.8125rem] text-ink-soft">
                {a.plan}
              </p>
            )}

            <Pictures images={a.images} />
            <Sources links={a.links} />

            {/* The one place a proposal is allowed to slow somebody down. */}
            {a.risk && <p className="mt-2 text-[0.75rem] text-ink-mute">{a.risk}</p>}

            <div className="mt-3 flex items-center gap-2">
              <button
                onClick={() => void answer(a, true)}
                disabled={busy === a.id}
                className="rounded-md bg-accent px-3.5 py-1.5 text-[0.8125rem] font-medium text-white transition hover:opacity-90 disabled:opacity-50"
              >
                Yes, do it
              </button>
              <button
                onClick={() => void answer(a, false)}
                disabled={busy === a.id}
                className="rounded-md border border-line px-3.5 py-1.5 text-[0.8125rem] font-medium text-ink-soft transition hover:text-ink disabled:opacity-50"
              >
                No
              </button>
              {a.expires && (
                <span className="ml-auto text-[0.6875rem] text-ink-mute">
                  until {new Date(a.expires).toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' })}
                </span>
              )}
            </div>
          </div>
        ))}
      </div>
      {wrong && <p className="mt-2 text-[0.75rem] text-ink-mute">{wrong}</p>}
    </section>
  )
}

/**
 * What it got on with. Deliberately quiet.
 *
 * Only today, and only what it actually did — the looks that came to nothing are the overwhelming majority and they
 * belong in the diagnostics, not in front of somebody making a cup of tea. The count is the honest part: if this
 * list is long every day, that is the signal to turn the dial down.
 */
export function ProactDid({
  state,
  onOpenChat,
  onVoted,
}: {
  state: ProactState
  onOpenChat?: (id: string) => void
  onVoted: () => void
}) {
  const [open, setOpen] = useState<ProactAction | null>(null)

  const today = new Date().toDateString()
  const did = state.actions.filter(
    (a) =>
      a.answer !== 'pending' &&
      a.kind !== 'looked' &&
      !a.dismissed &&
      new Date(a.at).toDateString() === today,
  )
  if (did.length === 0) return null

  return (
    <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '90ms' }}>
      <div className="text-[0.75rem] font-semibold uppercase tracking-wider text-ink-mute">While you were out</div>
      <div className="mt-2.5 space-y-1.5">
        {did.map((a) => (
          <Row key={a.id} action={a} onOpen={() => setOpen(a)} onChanged={onVoted} />
        ))}
      </div>

      {open && (
        <Full
          action={open}
          onClose={() => setOpen(null)}
          onChanged={onVoted}
          onOpenChat={onOpenChat}
        />
      )}
    </section>
  )
}

/**
 * One row: a title and a body, one line each.
 *
 * Both truncate, which is the whole reason the action has a title AND a body rather than one field doing both jobs —
 * a paragraph in the title position becomes an ellipsis with its useful half hidden. The row is a handle, not the
 * content: everything worth reading is a click away.
 */
function Row({
  action,
  onOpen,
  onChanged,
}: {
  action: ProactAction
  onOpen: () => void
  onChanged: () => void
}) {
  const [busy, setBusy] = useState(false)

  const summary = action.body || action.outcome || action.produced || action.why

  return (
    <div className="group flex items-center gap-3 rounded-lg border border-line bg-surface px-4 py-2.5">
      {/* The whole row is the target, because a one-line summary is an invitation to read the rest. */}
      <button onClick={onOpen} className="min-w-0 flex-1 text-left">
        <p className="truncate text-[0.8125rem] font-medium text-ink">{action.what}</p>
        {summary && <p className="truncate text-[0.75rem] text-ink-mute">{plain(summary)}</p>}
      </button>

      <Thumbs action={action} onVoted={onChanged} />

      {/* Put away for good. Without it the only way this list gets shorter is for things to age out, and a surface
          that only accumulates is one that stops being opened. */}
      <button
        onClick={async () => {
          setBusy(true)
          await dismissProact(action.id)
          setBusy(false)
          onChanged()
        }}
        disabled={busy}
        title="Dismiss"
        className="shrink-0 px-1 text-[0.8125rem] text-ink-mute opacity-0 transition group-hover:opacity-100 hover:text-ink focus:opacity-100"
      >
        ✕
      </button>

      <span className="shrink-0 text-[0.6875rem] tabular-nums text-ink-mute">
        {new Date(action.at).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}
      </span>
    </div>
  )
}

/**
 * The whole finding, in the app's own sheet.
 *
 * The first version of this was a hand-rolled overlay, which was simply wrong: Sheet already exists, is portalled so
 * nothing can clip it, locks the page behind it, closes on escape, and rises from the bottom edge on a phone where
 * this is actually read. A second dialog implementation is a dialog that behaves differently from every other dialog
 * in the app for no reason anybody could name.
 */
function Full({
  action,
  onClose,
  onChanged,
  onOpenChat,
}: {
  action: ProactAction
  onClose: () => void
  onChanged: () => void
  onOpenChat?: (id: string) => void
}) {
  const [busy, setBusy] = useState(false)
  const [say, setSay] = useState('')
  const [sending, setSending] = useState(false)

  /**
   * Say something back.
   *
   * A notice with no way to reply is a dead end — the obvious thing to do with "a new place has opened near you" is
   * to say "add that to my list". One box, already there, and Send opens the conversation it went to so the reply is
   * somewhere you can watch rather than somewhere you have to go and find.
   */
  const follow = async () => {
    const text = say.trim()
    if (!text) return

    setSending(true)
    const session = await proactThread(action.id)
    if (session) {
      await sendMessage(session, text)
      setSay('')
      onChanged()
      onClose()
      onOpenChat?.(session)
    }
    setSending(false)
  }

  const put = async () => {
    setBusy(true)
    await dismissProact(action.id)
    setBusy(false)
    onChanged()
    onClose()
  }

  return (
    <Sheet
      open
      title={action.what}
      subtitle={
        new Date(action.at).toLocaleString() + (action.subject ? ` · ${action.subject}` : '')
      }
      onClose={onClose}
      // The one thing this sheet is for: having read it, be done with it.
      action={
        <SheetButton onClick={put} busy={busy}>
          Dismiss
        </SheetButton>
      }
    >
      <div className="space-y-4">
        {/* Rendered exactly as a chat message is — same links, same bounded pictures, one renderer. */}
        <SheetSection at={0}>
          {action.body ? <Markdown text={action.body} /> : <p className="text-sm text-ink-soft">{action.why}</p>}
          <Pictures images={action.images} />
          <Sources links={action.links} />
        </SheetSection>

        {(action.outcome || action.produced) && (
          <SheetSection label={action.outcome ? 'What happened' : 'What it left you'} at={1}>
            <p className="text-[0.8125rem] text-ink-soft">{action.outcome ?? action.produced}</p>
          </SheetSection>
        )}

        {/* Its reasoning, kept out of the way but present — a finding with no stated reason reads as a guess. */}
        {action.body && action.why && (
          <SheetSection label="Why it brought you this" at={2}>
            <p className="text-[0.75rem] text-ink-mute">{action.why}</p>
          </SheetSection>
        )}

        {/* One box, already open. Anything more than type-and-send is more than this is worth. */}
        <SheetSection label="Send a follow-up" at={3}>
          <div className="flex items-center gap-2">
            <input
              value={say}
              onChange={(e) => setSay(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault()
                  void follow()
                }
              }}
              placeholder="Add this to my list…"
              className="min-w-0 flex-1 rounded-xl border border-line bg-surface px-3 py-2 text-[0.875rem] text-ink placeholder:text-ink-mute focus:border-accent focus:outline-none"
            />
            <button
              onClick={() => void follow()}
              disabled={sending || say.trim().length === 0}
              className="shrink-0 rounded-xl border border-line px-3 py-2 text-[0.8125rem] font-medium text-ink-soft transition enabled:hover:text-ink disabled:opacity-40"
            >
              {sending ? 'Sending…' : 'Send'}
            </button>
          </div>
        </SheetSection>

        <SheetSection at={4}>
          <div className="flex items-center gap-3">
            <Thumbs action={action} onVoted={onChanged} />
            {action.session && onOpenChat && (
              <button
                onClick={() => {
                  onOpenChat(action.session!)
                  onClose()
                }}
                className="ml-auto text-[0.75rem] font-medium text-accent hover:underline"
              >
                Open the conversation
              </button>
            )}
          </div>
        </SheetSection>
      </div>
    </Sheet>
  )
}


/**
 * How long until it goes out again, in as few characters as will carry it.
 *
 * Seconds are deliberately not shown. A number moving once a second asks to be watched, and this is a thing that
 * happens on its own every half hour — the pill exists to answer "is it still running" at a glance, not to be
 * counted down.
 */
function untilText(nextAt: string | null | undefined, on: boolean, paused: boolean, now: number): string {
  if (!on) return 'off'
  if (!nextAt) return 'soon'

  const left = new Date(nextAt).getTime() - now
  if (left <= 30_000) return paused ? 'held' : 'now'

  const mins = Math.round(left / 60_000)
  if (paused) return mins >= 60 ? `held ${Math.round(mins / 60)}h` : `held ${mins}m`
  return mins >= 60 ? `${Math.round(mins / 60)}h` : `${mins}m`
}

/**
 * What it is doing, while it does it.
 *
 * The button used to answer with a sentence and nothing else, which is the least interesting thing about having
 * pressed it: the run takes minutes, and all of it happened behind a door with no handle — a Proact conversation
 * is deliberately hidden from the chat list, so there was nowhere to go and look.
 *
 * Newest step at the top, because the interesting one is the one happening now, and the list is capped at what fits
 * without turning a glance into a read.
 */
function Doing({ live }: { live: ProactDoing }) {
  const steps = live.steps.slice(-7).reverse()

  return (
    <div className="space-y-2">
      <div className="flex items-center gap-2">
        <span className="relative flex h-1.5 w-1.5">
          <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-accent opacity-75" />
          <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-accent" />
        </span>
        <span className="truncate text-xs font-medium text-ink">{live.note || 'Working on it'}</span>
      </div>

      {steps.length > 0 && (
        <ol className="space-y-1 border-l border-line pl-3">
          {steps.map((st, i) => (
            <li key={i} className={cxLocal('truncate text-[0.6875rem]', i === 0 ? 'text-ink-soft' : 'text-ink-mute')}>
              <StepText step={st} />
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

/** A step as one line: what it reached for, and the one argument worth naming. */
function StepText({ step }: { step: ProactStep }) {
  if (step.kind === 'tool' && step.tool) {
    const detail = stepDetail(step.args)
    // The underscores are an implementation detail nobody outside the code needs to read.
    const name = step.tool.replace(/_/g, ' ')
    return (
      <>
        <span className="font-medium">{name}</span>
        {detail && <span className="text-ink-mute"> {detail}</span>}
      </>
    )
  }

  return <span className="italic">{step.text || step.kind}</span>
}

/**
 * Proact, in the header, next to the ear.
 *
 * It sits beside always-on listening because they are the same kind of thing: two ways the assistant does something
 * without being spoken to first, and the pair of them are the only controls on this page whose state matters when
 * you are not looking at it. Hence a pill rather than a plain icon — "on" is not the useful fact, "four minutes" is.
 *
 * Polls on its own rather than taking the home page's copy. The header outlives every view under it, and a control
 * that only knows the truth on one screen is a control that lies on the others.
 */
export function ProactButton() {
  const { proact, reloadProact } = useProact(20000)
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [sent, setSent] = useState<string | null>(null)
  const [live, setLive] = useState<ProactDoing | null>(null)
  const [now, setNow] = useState(() => Date.now())

  // Once every fifteen seconds, which is enough for a figure quoted in whole minutes and is not a stopwatch.
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 15000)
    return () => clearInterval(timer)
  }, [])

  // Only while the sheet is open and only while something is out: nobody needs a second-by-second poll behind a
  // closed panel, and the run carries on regardless of whether anyone is watching it.
  const watching = open && (!!proact?.doing || !!live?.running)
  useEffect(() => {
    if (!watching) return

    let alive = true
    const tick = async () => {
      const d = await proactDoing()
      if (!alive) return
      setLive(d)
      // It came back. Pick up what it filed, which is the thing they actually wanted.
      if (!d.running) await reloadProact()
    }

    void tick()
    const timer = setInterval(() => void tick(), 2000)
    return () => {
      alive = false
      clearInterval(timer)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [watching])

  if (!proact) return null

  const paused = !!proact.pausedUntil && new Date(proact.pausedUntil).getTime() > now
  const until = untilText(proact.nextAt, proact.on, paused, now)

  const kick = async () => {
    setBusy(true)
    setSent(null)
    setLive(null)
    // Deep, because a person reaching for this button is asking for something to happen, not for a check.
    const went = await proactLookNow(true)
    setBusy(false)

    // Nothing to say when it went: the step list says it, and better. Only the two dead ends need words.
    setSent(
      went === null ? "Couldn't go just now." : went.acted ? null : 'Had a look and there was nothing worth doing.',
    )
    await reloadProact()
  }

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        aria-label="Proact"
        title={proact.on ? `Proact — next in ${until}` : 'Proact — off'}
        className={cxLocal(
          'flex h-9 shrink-0 items-center gap-1.5 rounded-md pl-1.5 pr-2 transition',
          proact.on && !paused
            ? 'bg-accent-soft text-accent'
            : 'text-ink-soft hover:bg-surface-mid hover:text-ink',
        )}
      >
        <ProactIcon on={proact.on && !paused} />
        <span className="text-[0.6875rem] font-semibold tabular-nums">{until}</span>
      </button>

      <Sheet
        open={open}
        title="Proact"
        subtitle={proact.on ? `Next in ${until}` : 'Not running'}
        icon={<ProactIcon on={proact.on && !paused} />}
        onClose={() => {
          setOpen(false)
          setSent(null)
        }}
        action={
          <button
            onClick={() => void kick()}
            disabled={busy || live?.running === true}
            className="w-full rounded-lg bg-accent px-4 py-2.5 text-sm font-semibold text-on-accent transition hover:brightness-110 disabled:opacity-50"
          >
            {busy ? 'Going…' : live?.running ? 'Out now…' : 'Do something new'}
          </button>
        }
      >
        <div className="space-y-5">
          {sent && <div className="rounded-lg bg-surface-low px-3 py-2 text-xs text-ink-soft">{sent}</div>}

          {/* The work, while it happens. First thing in the sheet, because while it is running it is the only
              thing anyone opened the sheet for. */}
          {live?.running && (
            <div className="rounded-lg bg-surface-low px-3 py-2.5">
              <Doing live={live} />
            </div>
          )}

          <SheetSection label="Goes out" at={0}>
            <div className="flex flex-wrap gap-1.5">
              {proact.intervals.map((i) => (
                <button
                  key={i}
                  onClick={async () => {
                    await setProactEvery(i)
                    await reloadProact()
                  }}
                  className={cxLocal(
                    'rounded-full px-3 py-1.5 text-xs font-medium transition',
                    i === proact.every
                      ? 'bg-accent text-on-accent'
                      : 'bg-surface-low text-ink-soft hover:text-ink',
                  )}
                >
                  {i.replace('every ', '')}
                </button>
              ))}
            </div>
          </SheetSection>

          <SheetSection at={1}>
            <div className="flex items-center gap-2">
              <button
                onClick={async () => {
                  await setProactOn(!proact.on)
                  await reloadProact()
                }}
                className="flex-1 rounded-lg bg-surface-low px-3 py-2 text-xs font-medium text-ink transition hover:bg-surface-mid"
              >
                {proact.on ? 'Turn off' : 'Turn on'}
              </button>
              {proact.on && (
                <button
                  onClick={async () => {
                    await pauseProact(paused ? 0 : 12)
                    await reloadProact()
                  }}
                  className="flex-1 rounded-lg bg-surface-low px-3 py-2 text-xs font-medium text-ink-soft transition hover:bg-surface-mid hover:text-ink"
                >
                  {paused ? 'Resume' : 'Not today'}
                </button>
              )}
            </div>
          </SheetSection>
        </div>
      </Sheet>
    </>
  )
}

/**
 * Proact, as a shape: something going out and coming back.
 *
 * Not a bell and not a robot. A bell is a notification, which is the one thing this is not … it does things and
 * tells you afterwards. Filled when it is running, hollow when it is not, so the state reads before the shape does.
 */
function ProactIcon({ on }: { on: boolean }) {
  return (
    <svg viewBox="0 0 24 24" className="h-[1.125rem] w-[1.125rem]" fill="none" aria-hidden="true">
      <circle
        cx="12"
        cy="12"
        r="3"
        fill={on ? 'currentColor' : 'none'}
        stroke="currentColor"
        strokeWidth="1.8"
      />
      <path
        d="M12 3.5v2.2M12 18.3v2.2M3.5 12h2.2M18.3 12h2.2M6.2 6.2l1.6 1.6M16.2 16.2l1.6 1.6M17.8 6.2l-1.6 1.6M7.8 16.2l-1.6 1.6"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        opacity={on ? 1 : 0.55}
      />
    </svg>
  )
}
