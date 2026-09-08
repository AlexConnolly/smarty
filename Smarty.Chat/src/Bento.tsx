import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import {
  adjustWidget,
  agoText,
  answerWidget,
  rebuildWidget,
  refreshWidget,
  answerTaskQuestion,
  fixWidget,
  removeWidget,
  reportWidgetBroken,
  reportWidgetFaults,
  updateWidget,
  type Widget,
} from './api'
import { WidgetBody } from './WidgetRuntime'
import { Icon } from './widgetKit'
import { Sheet, SheetButton, SheetSection, SizePicker } from './Sheet'

/**
 * The home page as a bento grid of panels the system built for itself.
 *
 * Three footprints in fixed grid units; the number of columns is what grows with the screen. The panel decides its
 * contents, the grid decides how many of them you see at once.
 *
 * Deliberately no title bar. A panel is a component written for its own subject, and it says what it is — a flight
 * card reads as a flight without a caption over it. A row of chrome above every box cost a third of the vertical
 * space in a small panel and repeated what was already underneath it. The name still exists; it lives in the menu,
 * where it is needed to identify the thing you are about to remove.
 */

/**
 * Three footprints, in grid units that never change.
 *
 * <p>
 * A panel's span is absolute — a 2×2 is a 2×2 on every device. What changes with the screen is how many COLUMNS there
 * are, so the panel keeps roughly the same physical size and a bigger screen simply fits more of them. Four columns on
 * a phone puts two of these side by side; eight on a tablet puts four; twelve on a desktop puts six.
 * </p>
 * <p>
 * The first attempt at this had it backwards — the spans flexed and the column count stayed put, which meant panels
 * grew and shrank instead of multiplying, and a headline written for a comfortable box ended up in a sliver.
 * </p>
 */
const SPAN: Record<Widget['size'], string> = {
  kpi: 'col-span-2 row-span-1',
  tall: 'col-span-2 row-span-2',
  wide: 'col-span-4 row-span-2',
}

export function BentoGrid({ widgets, onChanged }: { widgets: Widget[]; onChanged: () => void }) {
  const proposed = widgets.filter((w) => w.status === 'proposed')
  const rest = widgets.filter((w) => w.status !== 'proposed')

  return (
    <>
      {proposed.map((w) => (
        <Proposal key={w.id} w={w} onChanged={onChanged} />
      ))}

      {/* The column count is what responds: 4 on a phone, 8 on a tablet, 12 on a desktop. Row height stays put, so a
          panel is about the same physical size everywhere and the screen decides how many fit. */}
      {rest.length > 0 && (
        <div className="mt-3 grid auto-rows-[7rem] grid-cols-4 gap-2.5 sm:auto-rows-[7.75rem] sm:gap-3 md:grid-cols-8 xl:grid-cols-12">
          {rest.map((w, i) => (
            <Panel key={w.id} w={w} onChanged={onChanged} index={i} />
          ))}
        </div>
      )}
    </>
  )
}

/**
 * Something the system thought of, offered before it costs anything.
 *
 * Saying yes is what starts the build, and a build is minutes of a worker's time — so the offer is the cheapest moment
 * there will ever be to change your mind about it. It used to be a bare yes/no, which meant the only way to influence
 * the result was to refuse and ask again differently. Now the two things that are knowable before a line is written —
 * how big it should be and what it should actually show — are editable right here, and both go with the yes.
 */
function Proposal({ w, onChanged }: { w: Widget; onChanged: () => void }) {
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [shows, setShows] = useState(w.why ?? w.title)
  const [size, setSize] = useState<Widget['size']>(w.size)

  async function decide(accept: boolean) {
    setBusy(true)
    await answerWidget(w.id, accept, accept ? { size, shows: shows.trim() } : undefined)
    setOpen(false)
    onChanged()
  }

  return (
    <>
      <div className="mt-3 rounded-xl border border-accent/25 bg-accent-soft/60 px-4 py-3 shadow-card">
        <div className="text-sm text-ink">
          Build <span className="font-medium">{w.title}</span> for your home page?
        </div>
        {w.why && <div className="mt-0.5 line-clamp-2 text-xs text-ink-soft">{w.why}</div>}
        <div className="mt-2.5 flex gap-2">
          <button
            onClick={() => setOpen(true)}
            className="rounded-md bg-accent px-3 py-1.5 text-xs font-medium text-on-accent transition"
          >
            Have a look
          </button>
          <button
            disabled={busy}
            onClick={() => decide(false)}
            className="rounded-md border border-line bg-surface px-3 py-1.5 text-xs text-ink-soft transition hover:bg-surface-low disabled:opacity-40"
          >
            No thanks
          </button>
        </div>
      </div>

      <Sheet
        open={open}
        title={w.title}
        subtitle="Roughly what will be built. Change anything here — it is free now and a rebuild later."
        onClose={() => setOpen(false)}
        icon={<Icon name="box" className="h-5 w-5" />}
        action={
          <SheetButton onClick={() => decide(true)} disabled={!shows.trim()} busy={busy}>
            Continue
          </SheetButton>
        }
      >
        <SheetSection label="What it shows" at={0}>
          <textarea
            value={shows}
            onChange={(e) => setShows(e.target.value)}
            rows={4}
            className="w-full resize-none rounded-xl border border-line bg-surface-low px-3.5 py-3 text-sm leading-relaxed text-ink outline-none transition focus:border-accent/60"
          />
        </SheetSection>

        <div className="mt-4">
          <SheetSection label="How big" at={1}>
            <SizePicker value={size} onChange={setSize} />
          </SheetSection>
        </div>

        <div className="mt-4">
          <SheetSection label="Then" at={2}>
            {/* Said as steps rather than a paragraph, because what happens next is a sequence and this is the one
                moment someone is deciding whether it is worth a few minutes of work. */}
            <div className="space-y-2">
              {[
                { icon: 'chart', text: 'Find a data source and check it actually answers' },
                { icon: 'box', text: 'Write the panel for the size you picked' },
                { icon: 'check', text: 'Put it on your home page, kept up to date' },
              ].map((step, i) => (
                <div
                  key={step.text}
                  className="animate-rise flex items-center gap-2.5"
                  style={{ animationDelay: `${260 + i * 70}ms` }}
                >
                  <div className="flex h-6 w-6 shrink-0 items-center justify-center rounded-lg bg-surface-low text-ink-mute">
                    <Icon name={step.icon} className="h-3.5 w-3.5" />
                  </div>
                  <div className="min-w-0 text-xs leading-snug text-ink-soft">{step.text}</div>
                </div>
              ))}
            </div>
            <div className="mt-2.5 text-[0.6875rem] leading-relaxed text-ink-mute">
              A few minutes, in the background. It says so on the page while it works, and asks you right here if it
              needs something only you have.
            </div>
          </SheetSection>
        </div>
      </Sheet>
    </>
  )
}

function Panel({ w, onChanged, index = 0 }: { w: Widget; onChanged: () => void; index?: number }) {
  const dots = useRef<HTMLButtonElement>(null)
  const [menu, setMenu] = useState(false)
  const paused = w.status === 'paused'

  /**
   * A panel that WORKS and whose repair has stopped to ask something.
   *
   * Replacing live content with a question box would be the wrong trade — the panel is doing its job, and the ask
   * is about doing it better. But saying nothing is how the ask went missing in the first place, so it gets a marker
   * in the footer, and opening it swaps the body until it is answered.
   */
  const [asking, setAsking] = useState(false)
  const asks = w.status === 'live' && !!w.question

  /** Saying what should change about this panel, in words. Lives on the panel so the menu can just open it. */
  const [adjusting, setAdjusting] = useState(false)

  /**
   * A throw is reported once per code version, and the server decides whether to send it back to be fixed. Guarded
   * by a ref rather than state: React re-renders a boundary that has caught, and reporting on each of those would
   * spend the panel's whole fix budget in a second.
   */
  const reported = useRef<string | null>(null)
  async function threw(error: string) {
    const key = `${w.code?.length}:${error}`
    if (reported.current === key) return
    reported.current = key
    if (await reportWidgetBroken(w.id, error)) onChanged()
  }

  /**
   * A resource the panel couldn't load. Reported from the real page as well as from the verification screenshot, so a
   * panel that breaks a week later — a camera that moves, a url that expires — says so from wherever it is being
   * looked at, without waiting for anything to come round and check.
   */
  const toldOf = useRef<string | null>(null)
  async function faults(list: string[]) {
    const key = list.join('|')
    if (toldOf.current === key) return
    toldOf.current = key
    await reportWidgetFaults(w.id, list)
  }

  return (
    <div
      // Staggered against its neighbours, capped so a page of twelve panels doesn't take a second and a half to
      // finish arriving.
      style={{ animationDelay: `${Math.min(index, 8) * 55}ms` }}
      className={`animate-rise group relative flex min-w-0 flex-col rounded-xl border border-line bg-surface p-3.5 shadow-card transition hover:shadow-ambient ${
        SPAN[w.size] ?? SPAN.kpi
      } ${paused ? 'opacity-55' : ''}`}
    >
      {/* Its own layer, above the content, so removing the title bar didn't cost the only way to reach the menu.
          Invisible until the panel is hovered, so it isn't chrome either. */}
      <button
        ref={dots}
        onClick={() => setMenu((v) => !v)}
        aria-label={`Options for ${w.title}`}
        className="absolute right-1.5 top-1.5 z-[1] rounded p-1 text-ink-mute opacity-0 transition group-hover:opacity-100 hover:bg-surface-low focus:opacity-100"
      >
        <svg viewBox="0 0 16 16" className="h-3.5 w-3.5" fill="currentColor" aria-hidden>
          <circle cx="8" cy="3" r="1.35" />
          <circle cx="8" cy="8" r="1.35" />
          <circle cx="8" cy="13" r="1.35" />
        </svg>
      </button>

      {w.pinned && <PinIcon />}

      {menu && (
        <Menu
          w={w}
          anchor={dots.current}
          onChanged={onChanged}
          onClose={() => setMenu(false)}
          onAdjust={() => {
            setMenu(false)
            setAdjusting(true)
          }}
        />
      )}

      {adjusting && (
        <Adjust
          w={w}
          onClose={() => setAdjusting(false)}
          onDone={() => {
            setAdjusting(false)
            onChanged()
          }}
        />
      )}

      {/*
        Scrollable rather than clipped.
        A fixed box and content that fits it perfectly is the ideal, and a generated component will sometimes miss —
        a list with one row more than expected, a longer place name. Clipping loses the rest silently, which is the
        worst of the options; growing the box breaks the grid. So it scrolls, with the bars hidden so a panel that
        happens to fit looks exactly as it did.
        The overflow lives here rather than on the panel because a menu is not contents: clipping THAT is what made
        it open inside the card and get cut off.
      */}
      <div className="widget-scroll min-h-0 flex-1 overflow-y-auto overflow-x-hidden">
        {asks && asking ? (
          <Asking w={w} onChanged={onChanged} />
        ) : (
          <Contents w={w} onThrew={threw} onFaults={faults} onChanged={onChanged} />
        )}
      </div>

      {(w.fetchedAt || w.error || asks) && (
        <div className="mt-1.5 flex items-center gap-1.5 truncate text-[0.625rem] text-ink-mute">
          {asks && (
            <button
              onClick={() => setAsking((v) => !v)}
              className="flex shrink-0 items-center gap-1 font-medium text-amber-600"
            >
              <Icon name="alert" className="h-3 w-3" />
              {asking ? 'back' : 'needs you'}
            </button>
          )}
          {/* Its age, always. A number that was right an hour ago looks exactly like one that's right now.
              "stale" means it TRIED and failed, not that nothing bothered — so the reason rides along in the title,
              because a bare red word next to an empty box tells you only that something is wrong. */}
          {w.error && (
            <span className="shrink-0 text-danger" title={w.error}>
              stale
            </span>
          )}
          <span className="truncate">{agoText(w.fetchedAt)}</span>
        </div>
      )}
    </div>
  )
}

function Contents({
  w,
  onThrew,
  onFaults,
  onChanged,
}: {
  w: Widget
  onThrew: (error: string) => void
  onFaults: (faults: string[]) => void
  onChanged: () => void
}) {
  // A build that has stopped to ask something. Shown here because here is where the user is looking — the question
  // was going into a conversation they may never open, and the panel just said "Building…" forever.
  //
  // Failed counts as well as building. A question outlives the process that asked it, so a restart leaves the panel
  // marked failed while its question is still open and still answerable — and the failure it would otherwise show
  // ("interrupted") is the less useful of the two things it knows.
  if (w.status !== 'live' && w.question) return <Asking w={w} onChanged={onChanged} />

  // Being made, and far enough along to be worth looking at: the design is here, rendering values its builder made
  // up. Shown behind what is happening to it rather than hidden until it works, because a panel you can watch being
  // built is a different experience from a grey line and the word "Building" for eight minutes. Dimmed, and never
  // reporting faults — the numbers are invented, and a made-up value that trips the component is the builder's to
  // find, not a repair job to start while the build is still running.
  if (w.design && w.stage)
    return (
      <div className="relative h-full">
        <div className="h-full" aria-hidden>
          <WidgetBody code={w.design} data={w.sample} params={w.parameters} />
        </div>
        {/* Over it rather than instead of it: enough of the design shows through to see what is coming, and the
            words stay readable whatever is underneath them. */}
        <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 bg-surface/70 backdrop-blur-[1px]">
          <span
            className="h-3.5 w-3.5 animate-spin rounded-full border-[1.5px] border-accent border-t-transparent"
            aria-hidden
          />
          <div className="px-2 text-center text-[0.6875rem] font-medium leading-tight text-ink-soft">{w.stage}…</div>
        </div>
      </div>
    )

  // The build stopped and there is a design. It stays: the user watched this panel being drawn, and replacing it
  // with two lines of red text takes away the only part that worked and leaves them nothing to react to. A build
  // that couldn't find a fixtures feed still knows exactly what the panel was going to look like.
  //
  // No spinner and no stage — nothing is happening, and the difference between this and the state above is exactly
  // that. Dimmed, with what it still needs written across it.
  if (w.design && w.status === 'failed')
    return (
      <div className="relative h-full">
        <div className="h-full" aria-hidden>
          <WidgetBody code={w.design} data={w.sample} params={w.parameters} />
        </div>
        <div className="absolute inset-0 flex flex-col justify-center gap-1 bg-surface/80 px-2 backdrop-blur-[1px]">
          <div className="line-clamp-4 text-[0.6875rem] leading-relaxed text-ink-soft">{w.error}</div>
        </div>
      </div>
    )

  if (w.status === 'building')
    return (
      <div className="flex h-full flex-col justify-center gap-2">
        <div className="progress-line h-0.5 w-full rounded-full" />
        <div className="text-xs text-ink-soft">
          {/* The stage first, because it is the specific truth and the count of past repairs is not. It used to say
              "Fixing…" whenever a panel had ever been repaired, which read as a panel repairing itself again — and
              nothing repairs itself any more, so the label would have been a lie as well as a worry. */}
          {w.stage ? `${w.stage}…` : `Building ${w.title}…`}
        </div>
      </div>
    )

  if (w.status === 'failed')
    return (
      <div className="flex h-full flex-col justify-center gap-1">
        <div className="text-xs font-medium text-danger">{w.title}</div>
        <div className="line-clamp-3 text-[0.6875rem] leading-relaxed text-ink-mute">{w.error}</div>
      </div>
    )

  if (!w.code) return <div className="flex h-full items-center text-sm text-ink-mute">{w.title}</div>

  // The id goes in so the panel's own component can act as this panel.
  return (
    <WidgetBody
      code={w.code}
      id={w.id}
      data={w.data}
      params={w.parameters}
      onThrew={onThrew}
      onFaults={onFaults}
    />
  )
}

/**
 * Telling a panel what to change, in a sentence.
 *
 * A panel IS its component, so "adjust" means writing it again — but the interesting part is that a person knows
 * exactly what is wrong with it and had no way to say so. "Rebuild" threw the whole thing away and hoped for better;
 * this hands the complaint to the builder, which is a far better brief than the original ask ever was. The size is
 * here too, because half of what looks wrong with a panel is that it is in the wrong box.
 */
function Adjust({ w, onClose, onDone }: { w: Widget; onClose: () => void; onDone: () => void }) {
  const [text, setText] = useState('')
  const [size, setSize] = useState<Widget['size']>(w.size)
  const [busy, setBusy] = useState(false)

  const sizeChanged = size !== w.size
  const ready = text.trim().length > 0 || sizeChanged

  async function submit() {
    if (!ready || busy) return
    setBusy(true)

    // Size first, so the edit is briefed with the box it is going to end up in rather than being resized after the
    // component was written for something else.
    if (sizeChanged) await updateWidget(w.id, { size })
    // ADJUST, not rebuild: the worker is handed the current component and asked to change the one thing named.
    if (text.trim()) await adjustWidget(w.id, text.trim())
    onDone()
  }

  return (
    <Sheet
      open
      title={`Adjust ${w.title}`}
      subtitle="Say what should be different. The panel is edited in place — its data feed, and anything you don’t mention, stay exactly as they are."
      onClose={onClose}
      icon={<Icon name="chart" className="h-5 w-5" />}
      action={
        <SheetButton onClick={submit} disabled={!ready} busy={busy}>
          {/* Names what will actually happen. "Change the size" while nothing has changed reads as an instruction
              rather than a button, so the resting label is the common case. */}
          {!text.trim() && sizeChanged ? 'Change the size' : 'Make the change'}
        </SheetButton>
      }
    >
      <SheetSection at={0}>
        <textarea
          value={text}
          onChange={(e) => setText(e.target.value)}
          rows={4}
          placeholder="Show the high and low, not the hourly list. And link it to the forecast."
          className="w-full resize-none rounded-xl border border-line bg-surface-low px-3.5 py-3 text-sm leading-relaxed text-ink outline-none transition placeholder:text-ink-mute focus:border-accent/60"
        />
      </SheetSection>

      <div className="mt-4">
        <SheetSection label="Size" at={1}>
          <SizePicker value={size} onChange={setSize} />
        </SheetSection>
      </div>

      {w.error && (
        <div className="mt-4">
          <SheetSection label="Last problem" at={2}>
            <div className="flex items-start gap-2 rounded-xl border border-line bg-surface-low px-3.5 py-3">
              <Icon name="alert" className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-600" />
              <div className="break-words text-xs leading-relaxed text-ink-soft">{w.error}</div>
            </div>
          </SheetSection>
        </div>
      )}
    </Sheet>
  )
}

/**
 * What the build stopped to ask, answerable in place.
 *
 * The answer goes through the same endpoint the chat's own question cards use, so a worker cannot tell where the
 * reply came from — there is one way to answer a question, not two.
 */
function Asking({ w, onChanged }: { w: Widget; onChanged: () => void }) {
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [sent, setSent] = useState(false)
  const [failed, setFailed] = useState(false)

  /**
   * Send it, and say what happened.
   *
   * The first version left the button on "…" and waited for the question to disappear from the next poll. It went
   * through, the worker picked it up and carried on — and the panel showed a stuck ellipsis, because the ask had not
   * been cleared server-side yet. A send has to report itself, not infer itself from a later refresh.
   */
  async function send() {
    if (!text.trim() || busy || !w.session || !w.taskId) return
    setBusy(true)
    setFailed(false)
    const ok = await answerTaskQuestion(w.session, w.taskId, text.trim())
    setBusy(false)
    if (!ok) {
      setFailed(true)
      return
    }
    setSent(true)
    setText('')
    onChanged()
  }

  if (sent)
    return (
      <div className="flex h-full flex-col justify-center gap-2">
        <div className="progress-line h-0.5 w-full rounded-full" />
        <div className="text-xs text-ink-soft">Thanks — carrying on with {w.title}…</div>
      </div>
    )

  return (
    <div className="flex h-full min-h-0 flex-col gap-2">
      {/* Scrolls rather than clamps: a question you can only read half of is a question you cannot answer, and
          these run long — the camera's asked for two things and explained why it needed both. */}
      <div className="widget-scroll flex min-h-0 flex-1 items-start gap-2 overflow-y-auto">
        <Icon name="alert" className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-600" />
        {/* break-words, because a question can contain a url. eBay's sign-in link is 200 unbroken characters and
            ran straight off the right edge of the panel, taking the end of the sentence with it. */}
        <div className="whitespace-pre-wrap break-words text-xs leading-relaxed text-ink">{w.question}</div>
      </div>
      <div className="flex shrink-0 items-center gap-2">
        <input
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && send()}
          placeholder="Answer…"
          className="min-w-0 flex-1 rounded-md border border-line bg-surface-low px-2 py-1 text-xs text-ink placeholder:text-ink-mute focus:border-accent/50 focus:outline-none"
        />
        <button
          onClick={send}
          disabled={!text.trim() || busy}
          className="shrink-0 rounded-md bg-accent px-2.5 py-1 text-xs font-medium text-on-accent transition disabled:opacity-40"
        >
          {busy ? 'Sending…' : 'Send'}
        </button>
      </div>
      {failed && (
        <div className="shrink-0 text-[0.625rem] text-danger">
          That didn't go through — the job may have moved on. Try the thread.
        </div>
      )}
    </div>
  )
}

/**
 * The menu, in a portal.
 *
 * It used to be absolutely positioned inside the panel, which has to clip its contents to stay a fixed box — so the
 * menu opened inside the card and was cut off by it. Positioned against the button's rect in the viewport instead,
 * so it overlays the page the way a menu should.
 */
function Menu({
  w,
  anchor,
  onChanged,
  onClose,
  onAdjust,
}: {
  w: Widget
  anchor: HTMLElement | null
  onChanged: () => void
  onClose: () => void
  onAdjust: () => void
}) {
  const [at, setAt] = useState<{ top: number; left: number } | null>(null)
  const box = useRef<HTMLDivElement>(null)

  useLayoutEffect(() => {
    if (!anchor) return
    const r = anchor.getBoundingClientRect()
    const width = 184
    // Flipped rather than allowed off-screen: a panel in the right-hand column would otherwise open past the edge.
    const left = Math.min(Math.max(8, r.right - width), window.innerWidth - width - 8)
    const below = r.bottom + 6
    const flip = below + 210 > window.innerHeight
    setAt({ top: flip ? Math.max(8, r.top - 210) : below, left })
  }, [anchor])

  useEffect(() => {
    const away = (e: MouseEvent) => {
      if (!box.current?.contains(e.target as Node) && !anchor?.contains(e.target as Node)) onClose()
    }
    const key = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    document.addEventListener('mousedown', away)
    document.addEventListener('keydown', key)
    // Scrolling moves the panel out from under a fixed menu, so the menu goes rather than floating loose.
    window.addEventListener('scroll', onClose, true)
    return () => {
      document.removeEventListener('mousedown', away)
      document.removeEventListener('keydown', key)
      window.removeEventListener('scroll', onClose, true)
    }
  }, [anchor, onClose])

  async function act(fn: () => Promise<unknown>) {
    await fn()
    onClose()
    onChanged()
  }

  if (!at) return null

  return createPortal(
    <div
      ref={box}
      style={{ top: at.top, left: at.left, width: 184 }}
      className="fixed z-50 overflow-hidden rounded-lg border border-line bg-surface py-1 shadow-ambient"
    >
      <div className="truncate px-3 py-1 text-[0.6875rem] font-medium text-ink">{w.title}</div>
      {w.loader && <div className="truncate px-3 pb-1 text-[0.625rem] text-ink-mute">via {w.loader}</div>}

      {/*
        Something was noticed about it, and nothing has been done about it.
        This is the whole of what used to happen by itself. A panel that threw once, or loaded oddly once, or
        photographed badly once, would send itself off to be rewritten — and the rewrite is a gamble against something
        that was usually working a minute later. So the finding is shown, with what was seen, and mending it is a
        choice. First in the menu, because when it is there it is the thing you came for.
      */}
      {w.ailing && (
        <>
          <div className="border-b border-line px-3 pb-1.5 pt-0.5">
            <div className="text-[0.625rem] font-medium text-amber-600">Something looks wrong</div>
            {w.wrong && <div className="mt-0.5 line-clamp-3 text-[0.625rem] leading-snug text-ink-mute">{w.wrong}</div>}
          </div>
          <Item onClick={() => act(() => fixWidget(w.id))}>Fix what's wrong</Item>
        </>
      )}

      {w.fetches && <Item onClick={() => act(() => refreshWidget(w.id))}>Refresh now</Item>}
      {/* Adjust before Rebuild, and deliberately first: a rebuild with no note is the same gamble that produced the
          panel you are unhappy with, while a sentence about what is wrong is the best brief there is. */}
      <Item onClick={onAdjust}>Adjust…</Item>
      <Item onClick={() => act(() => rebuildWidget(w.id))}>Rebuild from scratch</Item>
      <Item onClick={() => act(() => updateWidget(w.id, { pinned: !w.pinned }))}>
        {w.pinned ? 'Unpin' : 'Pin to top'}
      </Item>
      <Item onClick={() => act(() => updateWidget(w.id, { size: nextSize(w.size) }))}>Resize</Item>
      {w.loader && (
        <Item onClick={() => act(() => updateWidget(w.id, { status: w.status === 'paused' ? 'live' : 'paused' }))}>
          {w.status === 'paused' ? 'Resume' : 'Pause updates'}
        </Item>
      )}
      <Item danger onClick={() => act(() => removeWidget(w.id))}>
        Remove
      </Item>
    </div>,
    document.body,
  )
}

function Item({ children, onClick, danger }: { children: ReactNode; onClick: () => void; danger?: boolean }) {
  return (
    <button
      onClick={onClick}
      className={`block w-full px-3 py-1.5 text-left text-xs transition hover:bg-surface-low ${
        danger ? 'text-danger' : 'text-ink-soft'
      }`}
    >
      {children}
    </button>
  )
}

function PinIcon() {
  return (
    <svg
      viewBox="0 0 16 16"
      className="absolute left-1.5 top-1.5 h-3 w-3 text-ink-mute opacity-60"
      fill="currentColor"
      aria-hidden
    >
      <path d="M9.5 1.5l5 5-1.8.4-2.6 2.6.5 3.4-1.1 1.1-3-3-3.2 3.2-.7-.7L6.8 10.4l-3-3L4.9 6.3l3.4.5L10.9 4.2z" />
    </svg>
  )
}

/** Cycling rather than a submenu: three footprints, and one tap beats opening a picker. */
function nextSize(size: Widget['size']): Widget['size'] {
  return size === 'kpi' ? 'tall' : size === 'tall' ? 'wide' : 'kpi'
}
