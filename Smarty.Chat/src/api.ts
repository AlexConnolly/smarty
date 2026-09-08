// Fetch the list of model names from the API (proxied to Ollama).
export async function fetchModels(): Promise<string[]> {
  try {
    const res = await fetch('/api/models')
    if (!res.ok) return []
    const data = await res.json()
    return (data.models ?? []).map((m: { name: string }) => m.name)
  } catch {
    return []
  }
}

// Send a WAV voice note to the API and get back the transcribed text (local Whisper).
/**
 * Speech to text, on the server.
 *
 * Takes a WAV or anything else the browser can record — the server converts whatever arrives into what Whisper wants.
 * That matters for always-on listening from a phone: compressed audio is a twentieth of the bytes, and the bytes are
 * the whole cost when the microphone and the transcriber are in different buildings.
 */
export async function transcribe(audio: Blob, expecting?: 'name'): Promise<string> {
  const form = new FormData()
  // Named from the type it actually is: ffmpeg sniffs the content, but a truthful name costs nothing and a lying one
  // is the sort of thing that wastes an afternoon.
  const extension = (audio.type.split('/')[1] ?? 'wav').split(';')[0]
  form.append('audio', audio, `note.${extension || 'wav'}`)

  // "name" tells the server to expect the assistant's own name — the hardest thing for a speech model to catch out of
  // context, and the thing always-on listening is mostly waiting for.
  const where = expecting ? '/api/transcribe?expecting=name' : '/api/transcribe'
  const res = await fetch(where, { method: 'POST', body: form })
  const data = await res.json().catch(() => ({}))
  if (!res.ok || data.error) throw new Error(data.error ?? `HTTP ${res.status}`)
  return (data.text ?? '').trim()
}

// ---- Projects ----

export interface ProjectSummary {
  slug: string
  title: string
  description: string
  /**
   * Which of the two this is. A project is being driven towards a stated outcome; a topic is somewhere to file what
   * accumulates about a subject, with nothing to finish.
   */
  sort: 'project' | 'topic'
  /** What finishing it would mean. Always set for a project, always empty for a topic. */
  goal: string
  /** What period it covers and where that sits relative to today. Empty when it isn't tied to dates, which is most. */
  window: string
  runs: number
  facts: number
}

export interface ProjectMemory {
  /** The brain edge's id — present when this memory can be forgotten. */
  id?: string
  type: string
  key: string
  value: string
  context?: string
  asserted: string
}

export interface RunStep {
  kind: 'thinking' | 'tool' | 'answer'
  text?: string
  tool?: string
  args?: string
  result?: string
}

export interface ProjectRun {
  id: string
  task: string
  title?: string | null
  status: string
  startedAt: string
  endedAt: string
  steps: RunStep[]
  result?: string
}

/** A list a project keeps — preferred amenities, must-sees. Edited an item at a time, by either side. */
export interface ProjectList {
  id: string
  title: string
  items: string[]
  updated: string
  /** Items to work THROUGH rather than just hold — shows tick boxes and progress. */
  checklist?: boolean
  /** Which items are ticked, by their text. Absent on lists written before checklists existed. */
  done?: string[]
}

/** Something a run produced and handed over, addressable long after the chat that made it. */
export interface ProjectFile {
  name: string
  run: string
  producedAt: string
  url: string
}

export interface ProjectDetail {
  slug: string
  title: string
  description: string
  status: string
  startsOn?: string | null
  endsOn?: string | null
  /** The window in words, e.g. "covers 18 – 24 Aug 2026 — ENDED 3 days ago". */
  window?: string
  /** Its end date has passed: still readable, no longer current. */
  ended?: boolean
  summary?: string | null
  memories: ProjectMemory[]
  runs: ProjectRun[]
  lists?: ProjectList[]
  files?: ProjectFile[]
}

/** Start a list on a project. Returns it, or null if that didn't work. */
export async function createList(slug: string, title: string): Promise<ProjectList | null> {
  try {
    const res = await fetch(`/api/projects/${encodeURIComponent(slug)}/lists`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title }),
    })
    return res.ok ? ((await res.json()) as ProjectList) : null
  } catch {
    return null
  }
}

/** Add and/or remove items in one call — the same shape the assistant's own tool takes. */
export async function updateList(
  id: string,
  change: { add?: string[]; remove?: string[]; title?: string; done?: string[]; undone?: string[] },
): Promise<ProjectList | null> {
  try {
    const res = await fetch(`/api/lists/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(change),
    })
    return res.ok ? ((await res.json()) as ProjectList) : null
  } catch {
    return null
  }
}

/** Take a file off a project. The conversation that made it keeps its own copy. */
export async function deleteProjectFile(slug: string, name: string): Promise<boolean> {
  try {
    const res = await fetch(
      `/api/projects/${encodeURIComponent(slug)}/files/${encodeURIComponent(name)}`,
      { method: 'DELETE' },
    )
    return res.ok
  } catch {
    return false
  }
}

export async function deleteList(id: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/lists/${encodeURIComponent(id)}`, { method: 'DELETE' })
    return res.ok
  } catch {
    return false
  }
}

/**
 * What deleting one would take with it, asked before anything is removed.
 *
 * The facts are named rather than counted on purpose: "3 facts" tells you nothing about whether you mind losing them,
 * and reading them back tells you everything.
 */
export interface Removal {
  slug: string
  title: string
  sort: 'project' | 'topic'
  goal: string
  lists: number
  runs: number
  /** Omitted entirely when the brain has no node for it — nulls are dropped on the wire. */
  node?: string | null
  facts: string[]
  files: number
  /** Panels built on it. Not deleted — a panel is somebody's home page — but worth knowing about before you decide. */
  panels: string[]
}

export async function fetchRemoval(slug: string): Promise<Removal | null> {
  try {
    const res = await fetch(`/api/projects/${encodeURIComponent(slug)}/removal`)
    if (!res.ok) return null
    return (await res.json()) as Removal
  } catch {
    return null
  }
}

/** Delete it and everything keyed to it. The confirm phrase is required by the server. */
export async function deleteProject(slug: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/projects/${encodeURIComponent(slug)}?confirm=delete`, { method: 'DELETE' })
    return res.ok
  } catch {
    return false
  }
}

/** The active projects, for the slide-out bar. */
export async function fetchProjects(): Promise<ProjectSummary[]> {
  try {
    const res = await fetch('/api/projects')
    if (!res.ok) return []
    return (await res.json()) as ProjectSummary[]
  } catch {
    return []
  }
}

/**
 * Standing work: something to be done later, or on a rhythm.
 *
 * `dueInSeconds` is computed by the server and is null for anything not currently scheduled. It has to come from
 * there — "next in 6 hours" worked out from the browser's clock against a stored instant is wrong by however much
 * the two disagree, which on a phone that has been asleep is a lot.
 */
export interface Schedule {
  id: string
  session: string
  /** The conversation it fires into, when that conversation has been spoken in. */
  title: string | null
  task: string
  status: 'pending' | 'paused' | 'firing' | 'done' | 'failed' | 'cancelled'
  /** "daily at 08:00", "every weekday at 07:30", "every 2 hours" — empty for a one-off. */
  repeat: string
  recurring: boolean
  nextAt: string
  // Absent, not null, when there is no next run — the server's JSON drops null fields.
  dueInSeconds?: number | null
  runs: number
  lastFiredAt?: string | null
  lastResult?: string | null
  createdAt: string
}

export async function fetchSchedules(): Promise<Schedule[]> {
  try {
    const res = await fetch('/api/schedules')
    if (!res.ok) return []
    return (await res.json()) as Schedule[]
  } catch {
    return []
  }
}

/**
 * Book something. `session` attaches it to a conversation so its answers land there; leave it off and it gets a
 * thread of its own, which is what an ad-hoc "every day, check this" from the tasks page wants.
 *
 * Returns the server's complaint on a recurrence it can't read, so the box can say what was wrong with it.
 */
export async function createSchedule(body: {
  task: string
  when?: string
  repeat?: string
  session?: string
}): Promise<{ ok: true } | { ok: false; error: string }> {
  try {
    const res = await fetch('/api/schedules', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    if (res.ok) return { ok: true }
    const err = (await res.json().catch(() => null)) as { error?: string } | null
    return { ok: false, error: err?.error ?? 'Could not schedule that.' }
  } catch {
    return { ok: false, error: 'Could not reach the server.' }
  }
}

/** Change when, how often, what, or whether it's paused. An empty `repeat` string means "stop repeating". */
export async function updateSchedule(
  id: string,
  body: { task?: string; when?: string; repeat?: string; paused?: boolean },
): Promise<{ ok: true } | { ok: false; error: string }> {
  try {
    const res = await fetch(`/api/schedules/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    if (res.ok) return { ok: true }
    const err = (await res.json().catch(() => null)) as { error?: string } | null
    return { ok: false, error: err?.error ?? 'Could not change that.' }
  } catch {
    return { ok: false, error: 'Could not reach the server.' }
  }
}

export async function cancelSchedule(id: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/schedules/${encodeURIComponent(id)}`, { method: 'DELETE' })
    return res.ok
  } catch {
    return false
  }
}

/**
 * "in 6h 20m", "in 3 days", "due now". What the list actually wants to say about a next run.
 *
 * Undefined as well as null, because the server's JSON omits null fields entirely — so a task with no next run
 * arrives with no `dueInSeconds` at all, and a null-only check let that through to render "in NaNm".
 */
export function untilText(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || Number.isNaN(seconds)) return ''
  if (seconds < 60) return 'due now'
  const m = Math.floor(seconds / 60)
  if (m < 60) return `in ${m}m`
  const h = Math.floor(m / 60)
  if (h < 24) return m % 60 === 0 ? `in ${h}h` : `in ${h}h ${m % 60}m`
  const d = Math.floor(h / 24)
  return d === 1 ? 'tomorrow' : `in ${d} days`
}

/**
 * One panel on the home page: a component the system wrote, plus whatever its data feed last returned.
 *
 * `code` is JSX, transpiled and rendered in the browser. `data` is the feed's response verbatim — not reshaped on
 * the way past, because the component was written against what the feed actually says.
 */
export interface Widget {
  id: string
  title: string
  size: 'kpi' | 'tall' | 'wide'
  priority: number
  pinned: boolean
  status: 'building' | 'live' | 'proposed' | 'paused' | 'failed'
  why?: string | null
  error?: string | null
  taskId?: string | null
  /** What its build has stopped to ask, when it has. Answered from the panel itself. */
  question?: string | null
  /** How many times a throwing component has been sent back to be fixed. */
  fixes?: number
  refresh: string
  fetchedAt?: string | null
  code?: string | null
  /** Which library kind this panel is an instance of. */
  kind?: string | null
  /** How its data is loaded — internal, http or browser. The mode only; the url never leaves the server. */
  loader?: string | null
  /** Whether it has a feed at all. A client-mode panel fetches in the page, so there is nothing to refresh. */
  fetches?: boolean
  /** The conversation the panel came from — where its build's question lives. */
  session?: string | null
  /** The values that make it about a particular thing — handed to the component as `params`. */
  parameters?: Record<string, string>
  /** Already in the kind's declared shape — the object the component renders, not a raw source response. */
  data?: unknown
  /** The component as first designed, to show while the rest of it is still being built. */
  design?: string | null
  /** Made-up values in the shape the design expects, so there is something in it to look at. */
  sample?: unknown
  /** What is being done to it right now, in words for the panel. Absent once it works. */
  stage?: string | null
  /**
   * Something has been noticed about it that a repair could address, and nothing has been started about it.
   *
   * Every check still runs — a component that throws, a picture that won't load, a load that fails, a photograph that
   * shows the wrong thing — and none of them acts on what it finds any more. This is what puts the fix in the panel's
   * menu instead: a panel that had just been made to look right rewrote itself twice in a row, once over a load that
   * came back oddly and once over an error that existed only in a tab running the previous bundle.
   */
  ailing?: boolean
  /** What was noticed, in the words to show someone deciding whether to mend it. */
  wrong?: string | null
}

export async function fetchWidgets(): Promise<Widget[]> {
  try {
    const res = await fetch('/api/widgets')
    if (!res.ok) return []
    return (await res.json()) as Widget[]
  } catch {
    return []
  }
}

/** Yes or no to a panel the system offered. Yes is what starts the build; no removes it. */
export async function answerWidget(
  id: string,
  accept: boolean,
  edits?: { size?: Widget['size']; shows?: string },
): Promise<boolean> {
  try {
    const res = await fetch(`/api/widgets/${encodeURIComponent(id)}/answer`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      // The size goes with the yes, not after it: the builder is told the real dimensions of the box, so it has to
      // know them before it writes a line.
      body: JSON.stringify({ accept, size: edits?.size, shows: edits?.shows }),
    })
    return res.ok
  } catch {
    return false
  }
}

export async function updateWidget(
  id: string,
  body: { pinned?: boolean; size?: string; priority?: number; status?: string; title?: string },
): Promise<boolean> {
  try {
    const res = await fetch(`/api/widgets/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    return res.ok
  } catch {
    return false
  }
}

export async function removeWidget(id: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/widgets/${encodeURIComponent(id)}`, { method: 'DELETE' })
    return res.ok
  } catch {
    return false
  }
}

/** Something waiting on the user, in a conversation they may not have open. */
export type OutstandingQuestion = {
  session: string
  taskId: string
  task: string
  question: string
  surface: string
  since?: string | null
}

/**
 * Everything waiting on an answer, from every conversation.
 *
 * The home page's own list of questions comes off the open conversation's event stream, so anything asked in a
 * thread that isn't open was invisible — which is indistinguishable from the system having gone quiet.
 */
/**
 * A conversation the assistant started by itself, that nobody has read.
 *
 * Something it was asked to watch for has happened, and it has already acted on it. Which is no use at all unless the
 * page says so: a chat started in the background is one nobody would ever think to open.
 */
export interface Nudge {
  session: string
  /** What it was watching for, in the words it was set up with. */
  watcher: string
  /** What arrived. */
  title: string
  topic: string
  at: string
}

export async function fetchNudges(): Promise<Nudge[]> {
  try {
    const res = await fetch('/api/nudges')
    if (!res.ok) return []
    return (await res.json()) as Nudge[]
  } catch {
    return []
  }
}

/** Opening it is what clears the nudge. */
export async function markChatOpened(id: string): Promise<void> {
  try {
    await fetch(`/api/chats/${encodeURIComponent(id)}/opened`, { method: 'POST' })
  } catch {
    /* a nudge that stays up one more minute is not worth a failure path */
  }
}

export async function fetchQuestions(): Promise<OutstandingQuestion[]> {
  try {
    const res = await fetch('/api/questions')
    if (!res.ok) return []
    return (await res.json()) as OutstandingQuestion[]
  } catch {
    return []
  }
}

/**
 * Answer the question a panel's build stopped on, from the panel.
 *
 * Uses the same endpoint the chat's own question cards use, so an answer given here reaches the waiting worker by
 * exactly the same route — there is one way to answer a question, not two.
 */
export async function answerTaskQuestion(sessionId: string, taskId: string, text: string): Promise<boolean> {
  try {
    const res = await fetch(
      `/api/session/${encodeURIComponent(sessionId)}/task/${encodeURIComponent(taskId)}/answer`,
      { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ content: text }) },
    )
    return res.ok
  } catch {
    return false
  }
}

/**
 * Change one thing about a panel that already works.
 *
 * Not the same call as a rebuild, and the difference is the whole point: a rebuild is briefed to go and find a source
 * and write a panel, while this hands the worker the component that is already rendering real data and asks for the
 * smallest edit that satisfies the note. Sending an adjustment down the rebuild path threw away a working loader to
 * re-derive it from a sentence.
 */
export async function adjustWidget(id: string, note: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/widgets/${encodeURIComponent(id)}/adjust`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ shows: note }),
    })
    return res.ok
  } catch {
    return false
  }
}

/** Fetch the feed again now. */
export async function refreshWidget(id: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/widgets/${encodeURIComponent(id)}/refresh`, { method: 'POST' })
    return res.ok
  } catch {
    return false
  }
}

/**
 * The component threw. The server decides whether to send it back to be fixed, and says so.
 *
 * Reported rather than only shown, because a panel that has broken is a fault with a message attached and the thing
 * that can read the message is the worker that wrote the code. Returns true when a repair actually started, so the
 * page can redraw and show that it is being worked on.
 */
export async function reportWidgetBroken(id: string, error: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/widgets/${encodeURIComponent(id)}/broken`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ error }),
    })
    if (!res.ok) return false
    const body = (await res.json().catch(() => null)) as { fixing?: boolean } | null
    return body?.fixing === true
  } catch {
    return false
  }
}

/**
 * Resources the panel tried to load and couldn't, reported so the server has the exact url rather than a description
 * of a screenshot. This is the precise version of the same fault the vision check finds by looking.
 */
export async function reportWidgetFaults(id: string, faults: string[]): Promise<boolean> {
  try {
    const res = await fetch(`/api/widgets/${encodeURIComponent(id)}/faults`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ faults }),
    })
    return res.ok
  } catch {
    return false
  }
}

/** Send a worker to find the feed and write the component again — for a broken feed or a change of mind. */
/**
 * Mend what was noticed about a panel — the deliberate version of what used to happen by itself.
 *
 * An adjustment rather than a rebuild: the loader and the component are kept and the smallest thing that answers the
 * fault is changed. The automatic path went through the build brief, which is told to go and find a source and write a
 * panel, so one transient fault cost the whole thing.
 */
export async function fixWidget(id: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/widgets/${encodeURIComponent(id)}/fix`, { method: 'POST' })
    return res.ok
  } catch {
    return false
  }
}

export async function rebuildWidget(id: string, shows?: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/widgets/${encodeURIComponent(id)}/rebuild`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ shows: shows ?? null }),
    })
    return res.ok
  } catch {
    return false
  }
}

/** "just now", "6m ago", "yesterday" — how old what you're looking at is. */
export function agoText(iso?: string | null): string {
  if (!iso) return ''
  const secs = (Date.now() - new Date(iso).getTime()) / 1000
  if (secs < 90) return 'just now'
  const m = Math.floor(secs / 60)
  if (m < 60) return `${m}m ago`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ago`
  const d = Math.floor(h / 24)
  return d === 1 ? 'yesterday' : `${d}d ago`
}

/**
 * One dated thing on a list, and where it came from.
 *
 * `offset` is days from today, computed by the server — 0 is today, 1 tomorrow. It has to come from there, or a
 * phone whose clock has drifted decides for itself which dinner is tonight.
 */
export interface AgendaEntry {
  listId: string
  list: string
  project: string
  projectTitle: string
  item: string
  on: string
  offset: number
  done: boolean
}

export async function fetchAgenda(days = 2): Promise<AgendaEntry[]> {
  try {
    const res = await fetch(`/api/agenda?days=${days}`)
    if (!res.ok) return []
    return (await res.json()) as AgendaEntry[]
  } catch {
    return []
  }
}

/** Tick or untick one dated item, straight from the day's view. */
export async function checkItem(listId: string, item: string, done: boolean): Promise<boolean> {
  try {
    const res = await fetch(`/api/lists/${encodeURIComponent(listId)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(done ? { done: [item] } : { undone: [item] }),
    })
    return res.ok
  } catch {
    return false
  }
}

/** Forget one memory. Returns true when it went. */
export async function forgetMemory(id: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/memory/${encodeURIComponent(id)}`, { method: 'DELETE' })
    return res.ok
  } catch {
    return false
  }
}

/** One project's overview — memories + what its workers did. */
export async function fetchProject(slug: string): Promise<ProjectDetail | null> {
  try {
    const res = await fetch(`/api/projects/${encodeURIComponent(slug)}`)
    if (!res.ok) return null
    return (await res.json()) as ProjectDetail
  } catch {
    return null
  }
}

/** A worker has paused mid-task to ask the user something, with a few precomputed answers to pick from. */
export interface WorkerQuestion {
  taskId: string
  question: string
  options: string[]
  project?: string | null
  /** What shape of answer it wants: text | choice | number | place | confirm. Defaults to text. */
  kind?: string
  /** For a number question — what's being counted, and any sensible bounds. */
  number?: { unit?: string; min?: number; max?: number; suggested?: number } | null
  /** For a place question — where to drop the pin, to be confirmed or corrected. */
  place?: { latitude: number; longitude: number; label?: string } | null
}

export interface SessionHandlers {
  onMsgStart?: (id: number, role: string) => void
  onContent?: (id: number, text: string) => void
  onReasoning?: (id: number, text: string) => void
  onMsgEnd?: (id: number, text?: string) => void
  /** `at` is when the task actually started (server clock) — absent on older recordings. */
  onWorking?: (id: string, task: string, msgId?: number, at?: number) => void
  onWorkingDone?: (id: string, status?: string, at?: number) => void
  /** A running task called a tool (or finished one) — live "it's actually doing something" feedback. */
  onTool?: (taskId: string, name: string, detail: string, done: boolean) => void
  /** A running task is reasoning rather than calling tools — so the pill can say so instead of going quiet. */
  onThinking?: (taskId: string, text: string) => void
  /** A file a task produced and delivered — a document, or a presentation to open full-screen. */
  onFile?: (name: string, caption: string | undefined, msgId: number | undefined) => void
  /** Cards for the links in a finished message — they arrive after it, since they need fetching. */
  onLinks?: (msgId: number, links: LinkCard[]) => void
  onQuestion?: (q: WorkerQuestion) => void
}

/**
 * An image with something written under it — a link's preview, or a picture handed over deliberately.
 *
 * `subtitle` is the line that says what the picture IS, which matters more than where it came from when the pictures
 * were the answer rather than decoration on one.
 */
export type LinkCard = {
  url: string
  title?: string
  subtitle?: string
  image?: string
  site?: string
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms))

/** Rate an assistant message (thumbs up/down) — labels the logged interaction for the fine-tune dataset. */
export async function sendFeedback(
  sessionId: string,
  messageId: number,
  rating: 'up' | 'down',
  note?: string,
): Promise<void> {
  try {
    await fetch(`/api/session/${sessionId}/feedback`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ messageId, rating, note }),
    })
  } catch {
    /* feedback is best-effort */
  }
}

/** Pin a session to a project so it becomes that project's dedicated, scoped chat. */
export async function pinSessionToProject(sessionId: string, slug: string): Promise<void> {
  try {
    await fetch(`/api/session/${sessionId}/project`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ slug }),
    })
  } catch {
    /* best-effort */
  }
}

// ---- Retained conversations ----

/**
 * Tell the server where we are, if the browser will say.
 *
 * The browser's own permission prompt is the consent gate — no grant, nothing is sent, and revoking it stops this
 * at source. Silent on refusal: a denied location is a choice, not an error to report.
 */
export async function reportLocation(): Promise<void> {
  if (!('geolocation' in navigator)) return

  // Asks on first load, deliberately. This is the user's own assistant on their own machine, and the whole point
  // is that it knows where they are — deferring the prompt to some later "right moment" just means it never has a
  // location on the one occasion it would have mattered. Chrome asks once and remembers the answer; a refusal is
  // silent and permanent until they change it themselves.
  try {
    const status = await navigator.permissions?.query({ name: 'geolocation' as PermissionName })
    if (status?.state === 'denied') return // already said no — never nag
  } catch {
    /* no permissions API — let getCurrentPosition ask */
  }

  await new Promise<void>((resolve) => {
    navigator.geolocation.getCurrentPosition(
      async (pos) => {
        try {
          await fetch('/api/location', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              latitude: pos.coords.latitude,
              longitude: pos.coords.longitude,
              accuracy: pos.coords.accuracy,
            }),
          })
        } catch {
          /* best-effort */
        }
        resolve()
      },
      () => resolve(), // denied, unavailable, timed out — all the same to us
      { enableHighAccuracy: false, timeout: 8000, maximumAge: 300_000 },
    )
  })
}

/** Ask for the permission, at a moment the user chose. Returns whether a fix reached the server. */
export async function enableLocation(): Promise<boolean> {
  if (!('geolocation' in navigator)) return false
  return new Promise<boolean>((resolve) => {
    navigator.geolocation.getCurrentPosition(
      async (pos) => {
        try {
          const res = await fetch('/api/location', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              latitude: pos.coords.latitude,
              longitude: pos.coords.longitude,
              accuracy: pos.coords.accuracy,
            }),
          })
          resolve(res.ok)
        } catch {
          resolve(false)
        }
      },
      () => resolve(false),
      { enableHighAccuracy: true, timeout: 10_000 },
    )
  })
}

export interface KnownLocation {
  place?: string
  latitude?: number
  longitude?: number
  at?: string
  note: string
}

export async function fetchLocation(): Promise<KnownLocation | null> {
  try {
    const res = await fetch('/api/location')
    return res.ok ? ((await res.json()) as KnownLocation) : null
  } catch {
    return null
  }
}

export async function clearLocation(): Promise<void> {
  try {
    await fetch('/api/location', { method: 'DELETE' })
  } catch {
    /* best-effort */
  }
}

// ---- A conversation, in one request ----

export interface SnapshotMessage {
  id: number
  role: 'user' | 'assistant'
  text: string
  files: { name: string; caption?: string }[]
  links: LinkCard[]
}

export interface SnapshotTask {
  id: string
  task: string
  msgId?: number
  /** Absent while it is still running. */
  status?: string
  startedAt?: string
  endedAt?: string
}

export interface ConversationSnapshot {
  /** How many events this accounts for — the live stream picks up from exactly here. */
  next: number
  messages: SnapshotMessage[]
  tasks: SnapshotTask[]
  questions: {
    id: string
    question: string
    options: string[]
    project?: string | null
    kind?: string
    number?: WorkerQuestion['number']
    place?: WorkerQuestion['place']
  }[]
}

/**
 * The conversation as it stands. Rendered as history in one go, rather than rebuilt from its own event stream on
 * every reload — which is what made reopening a chat retype itself and re-derive every task's state.
 */
export async function fetchSnapshot(sessionId: string): Promise<ConversationSnapshot | null> {
  try {
    const res = await fetch(`/api/session/${sessionId}/snapshot`)
    if (!res.ok) return null
    return (await res.json()) as ConversationSnapshot
  } catch {
    return null
  }
}

/** A conversation kept on disk, as the sidebar needs it. */
export interface ChatSummary {
  id: string
  title: string
  /** Every project this chat's work touched — one pill each. */
  projects: { slug: string; title: string }[]
  messageCount: number
  lastActivityAt: string
  /** Still in memory on the server, i.e. this is the one you're in or one with work running. */
  live: boolean
}

export async function fetchChats(): Promise<ChatSummary[]> {
  try {
    const res = await fetch('/api/chats')
    if (!res.ok) return []
    return (await res.json()) as ChatSummary[]
  } catch {
    return []
  }
}

/** Forget a conversation — its history goes from disk. */
export async function deleteChat(id: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/chats/${encodeURIComponent(id)}`, { method: 'DELETE' })
    return res.ok
  } catch {
    return false
  }
}

/** A file the user attached, now sitting in the conversation's own library. */
export interface UploadedFile {
  name: string
  url: string
}

/** Carries the status code, so the UI can tell "this server is too old" from "that didn't work". */
export class UploadFailed extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message)
    this.name = 'UploadFailed'
  }
}

/**
 * Hand files to the session. They land in its library — the same folder a worker writes to, so they can be
 * read by name — and the NEXT message carries them.
 *
 * That ordering is the whole contract: upload, then send. Throws rather than resolving empty, because a
 * silent failure would post the message anyway and leave the assistant talking about a file that isn't there.
 */
export async function uploadFiles(sessionId: string, files: File[]): Promise<UploadedFile[]> {
  const form = new FormData()
  for (const f of files) form.append('files', f, f.name)
  const res = await fetch(`/api/session/${sessionId}/upload`, { method: 'POST', body: form })
  const data = await res.json().catch(() => ({}))
  if (!res.ok || data.error) throw new UploadFailed(data.error ?? `HTTP ${res.status}`, res.status)
  return (data.files ?? []) as UploadedFile[]
}

/** Post a user message to the session. The reply (and any later results) arrive on the stream. */
export async function sendMessage(sessionId: string, content: string): Promise<void> {
  await fetch(`/api/session/${sessionId}/message`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ content }),
  })
}

export async function cancelTask(sessionId: string, taskId: string): Promise<void> {
  const res = await fetch(`/api/session/${sessionId}/task/${encodeURIComponent(taskId)}`, {
    method: 'DELETE',
  })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
}

/** Answer a worker that paused to ask a question — it resumes from where it left off. */
/** Put a question down without answering it — it stopped mattering, rather than getting a reply. */
export async function dismissQuestion(sessionId: string, taskId: string): Promise<boolean> {
  try {
    const res = await fetch(
      `/api/session/${sessionId}/task/${encodeURIComponent(taskId)}/question`,
      { method: 'DELETE' },
    )
    return res.ok
  } catch {
    return false
  }
}

export async function answerTask(sessionId: string, taskId: string, content: string): Promise<void> {
  const res = await fetch(`/api/session/${sessionId}/task/${encodeURIComponent(taskId)}/answer`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ content }),
  })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
}

/**
 * Open the session's persistent event stream and keep it open, transparently reconnecting on drops.
 * Everything the assistant says — instant acks AND results pushed back asynchronously from background
 * workers — arrives here. The stream never ends on its own; it runs until `signal` is aborted.
 */
export async function openSessionStream(
  sessionId: string,
  handlers: SessionHandlers,
  signal: AbortSignal,
  /** Where to start. Pass a snapshot's `next` so history isn't delivered twice — once rendered, once replayed. */
  from = 0,
): Promise<void> {
  let received = from

  while (!signal.aborted) {
    try {
      const res = await fetch(`/api/session/${sessionId}?from=${received}`, { signal })
      if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`)

      const reader = res.body.getReader()
      const decoder = new TextDecoder()
      let buffer = ''

      // Some proxies (a Cloudflare quick tunnel, for one) accept the request and then BUFFER the whole
      // event-stream, so nothing ever arrives and the chat just looks frozen. The server always writes a
      // padding comment immediately, so if we haven't seen a single byte in this long, we aren't streaming —
      // give up on it and poll instead, permanently, for the rest of the session.
      const firstByte = readWithTimeout(reader, StreamProbeMs)
      const opening = await firstByte
      if (opening === 'timeout') {
        void reader.cancel().catch(() => {})
        await pollSessionEvents(sessionId, received, handlers, signal)
        return
      }
      if (opening.done) throw new Error('stream closed immediately')

      buffer += decoder.decode(opening.value, { stream: true })
      received = drain()

      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        received = drain()
      }

      function drain(): number {
        let split: number
        while ((split = buffer.indexOf('\n\n')) !== -1) {
          const frame = buffer.slice(0, split)
          buffer = buffer.slice(split + 2)
          const ev = parseFrame(frame)
          if (!ev) continue
          received++ // every buffered event counts, so reconnect offsets stay exact
          dispatch(ev, handlers)
        }
        return received
      }
    } catch {
      if (signal.aborted) return
    }
    if (signal.aborted) return
    await delay(500) // the session lives on; reconnect from where we left off
  }
}

/** How long to wait for the first byte before deciding this connection isn't really streaming. */
const StreamProbeMs = 4000

async function readWithTimeout(
  reader: ReadableStreamDefaultReader<Uint8Array>,
  ms: number,
): Promise<ReadableStreamReadResult<Uint8Array> | 'timeout'> {
  return await Promise.race([
    reader.read(),
    delay(ms).then(() => 'timeout' as const),
  ])
}

/**
 * The fallback: ordinary JSON requests for the same events, from the same offset. Slower to feel than a real
 * stream, but it works through anything — and it's the difference between a usable chat over a tunnel and a
 * blank screen.
 */
async function pollSessionEvents(
  sessionId: string,
  from: number,
  handlers: SessionHandlers,
  signal: AbortSignal,
): Promise<void> {
  let next = from
  while (!signal.aborted) {
    try {
      const res = await fetch(`/api/session/${sessionId}/events?from=${next}`, { signal })
      if (res.ok) {
        const page = (await res.json()) as { next: number; events: { event: string; data: string }[] }
        for (const ev of page.events) {
          try {
            dispatch({ event: ev.event, data: JSON.parse(ev.data) }, handlers)
          } catch {
            /* ignore a frame we can't parse */
          }
        }
        next = page.next
      }
    } catch {
      if (signal.aborted) return
    }
    await delay(700)
  }
}

interface Frame {
  event: string
  data: Record<string, unknown>
}

function parseFrame(frame: string): Frame | null {
  let event = 'message'
  let raw = ''
  for (const line of frame.split('\n')) {
    if (line.startsWith('event:')) event = line.slice(6).trim()
    else if (line.startsWith('data:')) raw += line.slice(5).trim()
  }
  if (!raw) return null
  try {
    return { event, data: JSON.parse(raw) }
  } catch {
    return null
  }
}

/**
 * The one informative bit of a tool call's arguments, for a one-line label.
 *
 * Tools are dynamic (an MCP server can offer anything), so rather than a name-to-phrase table that goes stale the
 * moment someone adds a server, pick whichever well-known field is present — a URL, a query, a path — and show
 * that. It answers "what is it doing RIGHT NOW" without needing to know what the tool is.
 */
function toolDetail(args?: string): string {
  if (!args) return ''
  try {
    const parsed = JSON.parse(args) as Record<string, unknown>
    for (const key of ['url', 'query', 'q', 'path', 'name', 'command', 'expression', 'what', 'about', 'key', 'ref']) {
      const v = parsed[key]
      if (typeof v === 'string' && v.trim()) {
        // A URL reads better without the scheme; everything else as-is.
        const text = v.replace(/^https?:\/\//, '')
        return text.length > 64 ? text.slice(0, 64) + '…' : text
      }
    }
    return ''
  } catch {
    return ''
  }
}

/**
 * When an event says it happened, as a local timestamp. Undefined when it doesn't say — recordings made before
 * task events carried a time, where the honest answer is "unknown" rather than "now".
 */
function stamp(data: Record<string, unknown>): number | undefined {
  const at = data.at
  if (typeof at !== 'string' || at.length === 0) return undefined
  const ms = Date.parse(at)
  return Number.isNaN(ms) ? undefined : ms
}

function dispatch(ev: Frame, h: SessionHandlers): void {
  const d = ev.data as { id: number; role: string; text: string; task: string; status?: string }
  // Task events carry a string task id, separate from the numeric message id.
  const taskId = String((ev.data as { id?: unknown }).id ?? '')
  switch (ev.event) {
    case 'msg_start':
      h.onMsgStart?.(d.id, d.role)
      break
    case 'content':
      h.onContent?.(d.id, d.text)
      break
    case 'reasoning':
      h.onReasoning?.(d.id, d.text)
      break
    case 'msg_end':
      h.onMsgEnd?.(d.id, d.text)
      break
    case 'working':
      // msgId ties the task to the assistant message that started it, so its progress can be shown there.
      h.onWorking?.(taskId, d.task, (ev.data as { msgId?: number }).msgId, stamp(ev.data))
      break
    case 'working_done':
      h.onWorkingDone?.(taskId, d.status, stamp(ev.data))
      break
    case 'thinking': {
      const t = ev.data as { text?: string }
      h.onThinking?.(taskId, t.text ?? '')
      break
    }
    case 'file': {
      const f = ev.data as { name?: string; path?: string; caption?: string; msgId?: number }
      const name = f.name ?? (f.path ? f.path.split(/[\/]/).pop() : undefined)
      if (name) h.onFile?.(name, f.caption, f.msgId)
      break
    }
    case 'links': {
      const l = ev.data as { id?: number; links?: LinkCard[] }
      if (typeof l.id === 'number' && l.links?.length) h.onLinks?.(l.id, l.links)
      break
    }
    case 'tool_started':
    case 'tool_completed': {
      const t = ev.data as { name?: string; arguments?: string }
      if (t.name) h.onTool?.(taskId, t.name, toolDetail(t.arguments), ev.event === 'tool_completed')
      break
    }
    case 'question': {
      const q = ev.data as {
        id: string
        question: string
        options?: string[]
        project?: string | null
        kind?: string
        number?: WorkerQuestion['number']
        place?: WorkerQuestion['place']
      }
      h.onQuestion?.({
        taskId: String(q.id),
        question: q.question,
        options: q.options ?? [],
        project: q.project ?? null,
        kind: q.kind ?? 'text',
        number: q.number ?? null,
        place: q.place ?? null,
      })
      break
    }
  }
}

/// Who the brain belongs to.
///
/// Asked once on load. Unknown means nobody has said yet, and the app puts the question to the user rather than
/// inventing a placeholder — a memory whose owner is a guess records every "my brother" against a fiction.
export type Identity = { known: boolean; name: string; assistant: string; email: string }

export async function fetchIdentity(): Promise<Identity | null> {
  try {
    const res = await fetch('/api/identity')
    if (!res.ok) return null
    return (await res.json()) as Identity
  } catch {
    // Offline or still starting. Null means "don't know yet", which must not be shown as "not set up" — the setup page
    // appearing over a working conversation because one fetch failed would be worse than no setup page at all.
    return null
  }
}

export async function saveIdentity(name: string, assistant: string, email: string): Promise<Identity | null> {
  const res = await fetch('/api/identity', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, assistant, email }),
  })
  if (!res.ok) return null
  return (await res.json()) as Identity
}

// ---- Signing in -------------------------------------------------------------------------------------------
//
// Everything behind the API is somebody's life — their accounts, their files, their memory, an assistant that acts on
// what it is told. The password is exchanged once for a session in a cookie this code cannot read, which is the point:
// a token in local storage is a token that leaks through a screenshot.

export interface SignInState {
  /** A password is configured, so a session is needed. */
  wanted: boolean
  signedIn: boolean
  /** Too many wrong guesses from here — it has stopped answering for a few minutes. */
  waiting: boolean
}

export async function fetchSignInState(): Promise<SignInState> {
  try {
    const res = await fetch('/api/auth/state')
    if (!res.ok) return { wanted: true, signedIn: false, waiting: false }
    return (await res.json()) as SignInState
  } catch {
    // Unreachable is not the same as unauthorised, but from here both mean "you are not getting in yet".
    return { wanted: true, signedIn: false, waiting: false }
  }
}

/** Returns null when it worked, or the reason it didn't. */
export async function signIn(password: string): Promise<string | null> {
  try {
    const res = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password }),
    })
    if (res.ok) return null
    const said = await res.json().catch(() => ({}))
    return (said as { error?: string }).error ?? 'That did not work.'
  } catch {
    return "Couldn't reach the server."
  }
}

export async function signOut(): Promise<void> {
  try {
    await fetch('/api/auth/logout', { method: 'POST' })
  } catch {
    /* the cookie is the server's to clear; a failure here just leaves you signed in */
  }
}

// ---- Proact -------------------------------------------------------------------------------------------------
//
// What it did for them without being asked, and the decisions it is waiting on. User-facing, so it lives here on the
// front page rather than in the control surface — Control is for settings; this is the work.

export interface ProactLink {
  /** What it is, in a few words — not "click here". */
  label: string
  url: string
}

export interface ProactImage {
  url: string
  caption?: string | null
}

export interface ProactAction {
  id: string
  at: string
  /** noticed | prepared | proposed | looked */
  kind: string
  mode: string
  /** The title. One short line — the timeline truncates it. */
  what: string
  /** The whole of it, as markdown: links and pictures render inline. */
  body?: string
  why: string
  produced?: string | null
  /** What it was about, coarsely — used to keep it from returning to the same corner of your life. */
  subject?: string
  /** Sources worth opening. Not verified: a human follows these in a real browser. */
  links?: ProactLink[]
  /** Pictures, each one fetched before it was kept — so these load. */
  images?: ProactImage[]
  /** up | down | null. What you told it about its judgement. */
  verdict?: string | null
  /** Put away and not shown again. Never true of a proposal you still owe an answer on. */
  dismissed?: boolean
  /** Proposals only: the steps that run if it is ticked. What makes one click safe. */
  plan?: string | null
  /** Proposals only: what cannot be undone, and by when. */
  risk?: string | null
  expires?: string | null
  /** pending | ticked | crossed | expired */
  answer?: string | null
  outcome?: string | null
  session?: string | null
  seen: boolean
}

/** One day, summed up. The only thing about Proact that reaches out rather than waiting to be visited. */
export interface ProactRoundup {
  day: string
  at: string
  text: string
  did: number
  waiting: number
  looks: number
  seen: boolean
}

export interface ProactState {
  on: boolean
  every: string
  intervals: string[]
  running: boolean
  /** When it next goes out. Null when it is off. Comes from the loop, not from the page's own arithmetic. */
  nextAt?: string | null
  pausedUntil?: string | null
  /** Present only while a run is actually out, so a sheet opened mid-run can show the run. */
  doing?: { task: string; session: string } | null
  todayCount: number
  waiting: number
  roundupHour?: number | null
  actions: ProactAction[]
  roundups: ProactRoundup[]
}

const NO_PROACT: ProactState = {
  on: false,
  every: 'every 10 minutes',
  intervals: [],
  running: false,
  todayCount: 0,
  waiting: 0,
  actions: [],
  roundups: [],
}

/** The switch. */
export async function setProactOn(on: boolean): Promise<boolean> {
  return patchProact({ on })
}

/** The dial. One of the four `intervals` the state carries; anything else is refused by the server. */
export async function setProactEvery(every: string): Promise<boolean> {
  return patchProact({ every })
}

/** Hours, or 0 to un-pause. */
export async function pauseProact(hours: number): Promise<boolean> {
  return patchProact({ pauseHours: hours })
}

async function patchProact(body: Record<string, unknown>): Promise<boolean> {
  try {
    const res = await fetch('/api/proact', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    return res.ok
  } catch {
    return false
  }
}

/**
 * Send it out now.
 *
 * `deep` asks for the expensive look — the one that goes and does something rather than checking whether anything
 * has changed. That is what somebody pressing a button on purpose almost always means, and it is otherwise only
 * reachable by waiting hours for it to come round.
 */
export async function proactLookNow(deep = false): Promise<{ acted: boolean; session?: string | null } | null> {
  try {
    const res = await fetch(`/api/proact/look${deep ? '?deep=true' : ''}`, { method: 'POST' })
    if (!res.ok) return null
    return (await res.json()) as { acted: boolean; session?: string | null }
  } catch {
    return null
  }
}

/** One step of a run: a thought, or a tool and what it was called with. */
export interface ProactStep {
  kind: string
  tool?: string | null
  args?: string | null
  text?: string | null
}

export interface ProactDoing {
  running: boolean
  session?: string | null
  note?: string | null
  status?: string | null
  steps: ProactStep[]
}

/** What it is doing at this moment. `running: false` once it is back. */
export async function proactDoing(): Promise<ProactDoing> {
  try {
    const res = await fetch('/api/proact/doing')
    if (!res.ok) return { running: false, steps: [] }
    const d = (await res.json()) as ProactDoing
    return { ...d, steps: d.steps ?? [] }
  } catch {
    return { running: false, steps: [] }
  }
}

/**
 * The one thing worth reading out of a tool call's arguments.
 *
 * A step list of bare tool names says almost nothing — "chrome_navigate" four times over is not a report. The
 * argument that matters is nearly always the first recognisable one, and a url reads better without its scheme.
 */
export function stepDetail(args?: string | null): string {
  if (!args) return ''
  try {
    const parsed = JSON.parse(args) as Record<string, unknown>
    for (const key of ['url', 'query', 'q', 'what', 'about', 'title', 'item', 'list', 'name', 'path', 'command',
      'expression', 'vacuum', 'room', 'key', 'ref']) {
      const v = parsed[key]
      if (typeof v === 'string' && v.trim()) {
        const text = v.replace(/^https?:\/\//, '')
        return text.length > 52 ? text.slice(0, 52) + '…' : text
      }
    }
    return ''
  } catch {
    return ''
  }
}

export async function fetchProact(): Promise<ProactState> {
  try {
    const res = await fetch('/api/proact')
    if (!res.ok) return NO_PROACT
    return (await res.json()) as ProactState
  } catch {
    return NO_PROACT
  }
}

/** The tick or the cross. Nothing else is offered, and a cross deliberately carries no reason. */
export async function answerProposal(id: string, ticked: boolean): Promise<string | null> {
  try {
    const res = await fetch(`/api/proact/${encodeURIComponent(id)}/answer`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ticked }),
    })
    if (res.ok) return null
    const said = await res.json().catch(() => ({}))
    return (said as { error?: string }).error ?? `HTTP ${res.status}`
  } catch {
    return "Couldn't reach the server."
  }
}

/** A thumb up or down. Passing nothing clears it, because a mis-tap should be undoable. */
export async function voteProact(id: string, verdict: 'up' | 'down' | null): Promise<boolean> {
  try {
    const res = await fetch(`/api/proact/${encodeURIComponent(id)}/vote`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ verdict }),
    })
    return res.ok
  } catch {
    return false
  }
}

/** Put one away for good. Refused on a proposal awaiting a yes or no. */
export async function dismissProact(id: string, undo = false): Promise<boolean> {
  try {
    const res = await fetch(`/api/proact/${encodeURIComponent(id)}/dismiss`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ undo }),
    })
    return res.ok
  } catch {
    return false
  }
}

/**
 * A conversation that already knows what the notice was, for replying to it.
 *
 * Returns the session id; send the actual message with sendMessage, which is the ordinary path — so the reply
 * streams into a chat you can watch like any other. Calling it twice returns the same thread.
 */
export async function proactThread(id: string): Promise<string | null> {
  try {
    const res = await fetch(`/api/proact/${encodeURIComponent(id)}/thread`, { method: 'POST' })
    if (!res.ok) return null
    return ((await res.json()) as { session: string }).session
  } catch {
    return null
  }
}

export async function markRoundupSeen(day: string): Promise<void> {
  try {
    await fetch(`/api/proact/roundup/${encodeURIComponent(day)}/seen`, { method: 'POST' })
  } catch {
    /* a read receipt is not worth an error */
  }
}
