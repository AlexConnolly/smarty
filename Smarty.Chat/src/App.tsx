import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import {
  answerTask,
  cancelTask,
  deleteChat,
  fetchChats,
  fetchProject,
  deleteProject,
  fetchProjects,
  fetchRemoval,
  openSessionStream,
  pinSessionToProject,
  sendFeedback,
  sendMessage,
  fetchSnapshot,
  reportLocation,
  transcribe,
  uploadFiles,
  UploadFailed,
  createList,
  deleteList,
  deleteProjectFile,
  dismissQuestion,
  forgetMemory,
  updateList,
  fetchSchedules,
  answerTaskQuestion,
  fetchQuestions,
  fetchWidgets,
  agoText,
  fetchNudges,
  fetchSignInState,
  signIn,
  type SignInState,
  markChatOpened,
  createSchedule,
  updateSchedule,
  cancelSchedule,
  untilText,
  fetchIdentity,
  saveIdentity,
  type Identity,
  type Schedule,
  type Widget,
  type ChatSummary,
  type ConversationSnapshot,
  type LinkCard,
  type ProjectDetail,
  type ProjectList,
  type ProjectMemory,
  type ProjectRun,
  type OutstandingQuestion,
  type Nudge,
  type ProjectSummary,
  type Removal,
  type RunStep,
  type SessionHandlers,
  type WorkerQuestion,
} from './api'
import { Markdown } from './Markdown'
import { ProactButton, ProactDid, RoundupCard, useProact, WaitingOnYou } from './Proact'
import { BentoGrid } from './Bento'
import { AlwaysOn } from './AlwaysOn'
import { formatDuration, toWav16k, type RecordedAudio } from './audio'

interface AudioNote {
  peaks: number[]
  duration: number
  url?: string
}

interface UiMessage {
  id: number
  role: 'user' | 'assistant'
  content: string
  reasoning: string
  streaming: boolean
  audio?: AudioNote
  /** Names of files sent with this message, shown as chips on the user's own bubble. */
  files?: string[]
  thinkStart?: number
  thinkMs?: number
}

type ToolStep = { key: number; name: string; detail: string; done: boolean; thinking?: boolean }
type Working = {
  id: string
  task: string
  startedAt: number
  msgId?: number
  steps?: ToolStep[]
  /** Set when the task ends. The bar stays — collapsed to its outcome — rather than vanishing. */
  status?: string
  /** When it stopped, so the elapsed time freezes instead of counting on for ever. */
  endedAt?: number
}

// How many recent tool calls to show under a running task. Enough to see it moving, few enough that a
// twenty-step research job doesn't push the conversation off the screen.
const TASK_STEPS_SHOWN = 3

/** Matches the server's cap. Enforced here too, so an oversized file is refused to your face rather than dropped. */
const MaxUploadBytes = 64 * 1024 * 1024

/** The id a just-sent message wears until the server echoes it back. Negative, so it can't collide with a real one. */
const OptimisticId = -1

const SESSION_KEY = 'smarty-session-id'

/**
 * Whether always-on listening is switched on, remembered.
 *
 * It has to survive a reload: this is a thing you turn on when you put the laptop on the kitchen counter, and having
 * to turn it back on after every refresh would mean it was never really on.
 */
const ALWAYS_ON_KEY = 'smarty-always-on'
const VIEW_KEY = 'smarty-view'
const REC_BARS = 56
const EXAMPLES = ['Plan a weekend in Lisbon', "What's the latest tech news?", 'Remember I live in London']

function getSessionId(): string {
  try {
    // /chats/<id> is the address of a conversation, so it wins over both ?s= and whatever was last open —
    // opening a link or refreshing on one has to land you in THAT chat, not the one you were in before.
    const fromPath = chatFromPath()
    if (fromPath) {
      localStorage.setItem(SESSION_KEY, fromPath)
      return fromPath
    }
    const fromUrl = new URLSearchParams(window.location.search).get('s')
    if (fromUrl) {
      localStorage.setItem(SESSION_KEY, fromUrl)
      return fromUrl
    }
    let id = localStorage.getItem(SESSION_KEY)
    if (!id) {
      id = crypto.randomUUID()
      localStorage.setItem(SESSION_KEY, id)
    }
    return id
  } catch {
    return crypto.randomUUID()
  }
}

// Reflect the open project in the URL path (/project/<slug>) so it survives a refresh and can be
// shared/bookmarked. Only the path changes — any query string (e.g. ?s=<session>) is preserved.
function setProjectPath(slug: string | null) {
  try {
    const url = new URL(window.location.href)
    url.pathname = slug ? `/project/${encodeURIComponent(slug)}` : '/'
    window.history.pushState({}, '', url)
  } catch {
    /* ignore */
  }
}

function projectFromPath(): string | null {
  try {
    const m = window.location.pathname.match(/^\/project\/([^/]+)\/?$/)
    return m ? decodeURIComponent(m[1]) : null
  } catch {
    return null
  }
}

/** The open conversation's own address: /chats/<id>. A reload or a shared link reopens that chat. */
function setChatPath(id: string | null, replace = false) {
  try {
    const url = new URL(window.location.href)
    url.pathname = id ? `/chats/${encodeURIComponent(id)}` : '/'
    if (replace) window.history.replaceState({}, '', url)
    else window.history.pushState({}, '', url)
  } catch {
    /* ignore */
  }
}

function chatFromPath(): string | null {
  try {
    const m = window.location.pathname.match(/^\/chats\/([^/]+)\/?$/)
    return m ? decodeURIComponent(m[1]) : null
  } catch {
    return null
  }
}

function greetingText(name?: string): string {
  const h = new Date().getHours()
  const time = h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening'

  // First name only. The full one reads like a form letter, and it is already known — asking again would be worse.
  const first = (name ?? '').trim().split(/\s+/)[0]
  return first.length > 0 ? `${time}, ${first}` : time
}

function initialView(): 'home' | 'chat' {
  try {
    return localStorage.getItem(VIEW_KEY) === 'chat' ? 'chat' : 'home'
  } catch {
    return 'home'
  }
}

/**
 * One panel, alone on the page.
 *
 * Exists to be looked at by a machine. A client-mode panel — an image, a stream — cannot be verified by either of the
 * self-repair loops: an <img> that fails to load doesn't throw, so the error boundary sees nothing, and there is no
 * server load to fail. The only thing that can tell a working camera from a broken-image icon is a pair of eyes, so
 * the build pipeline screenshots this route and asks a vision model what it can see.
 *
 * Sized to a large panel and centred on a plain ground, so what fills the frame is the panel and nothing else.
 */
function SoloWidget({ id }: { id: string }) {
  const [widget, setWidget] = useState<Widget | null>(null)
  const [missing, setMissing] = useState(false)

  useEffect(() => {
    fetchWidgets().then((all) => {
      const found = all.find((w) => w.id === id) ?? null
      setWidget(found)
      setMissing(found === null)
    })
  }, [id])

  if (missing) return <div className="p-4 text-sm text-danger">No panel {id}</div>
  if (!widget) return <div className="p-4 text-sm text-ink-mute">Loading…</div>

  return (
    <div className="flex min-h-screen items-center justify-center bg-bg p-6">
      <div className="h-[16rem] w-[40rem]">
        <BentoGrid widgets={[{ ...widget, size: 'wide' }]} onChanged={() => {}} />
      </div>
    </div>
  )
}

export default function App() {
  // Checked before anything else: this route mounts one panel and none of the app around it.
  const solo = window.location.pathname.match(/^\/widget\/([^/]+)\/?$/)
  if (solo) return <SoloWidget id={decodeURIComponent(solo[1])} />

  return <Locked />
}

/**
 * The door.
 *
 * In front of everything, because behind it is a browser signed into somebody's accounts, their files, their memory and
 * an assistant that acts on what it is told. Nothing else in the app renders until this says so — not because the shell
 * is secret, but because a UI full of empty panels and failed requests is a worse answer to "you are not signed in"
 * than a password box is.
 */
function Locked() {
  const [state, setState] = useState<SignInState | null>(null)
  const [password, setPassword] = useState('')
  const [why, setWhy] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let live = true
    fetchSignInState().then((found) => live && setState(found))
    return () => {
      live = false
    }
  }, [])

  // Nothing until the answer is in: a flash of a password box in front of somebody who is already signed in reads as
  // having been logged out.
  if (state === null) return null
  if (state.signedIn || !state.wanted) return <Introduce />

  const submit = async () => {
    if (password.length === 0 || busy) return
    setBusy(true)
    const failed = await signIn(password)
    setBusy(false)
    if (failed) {
      setWhy(failed)
      setPassword('')
      return
    }
    // A full reload rather than a state flip: every stream, poll and fetch in the app starts on mount, and restarting
    // them one by one is a longer list than it looks.
    window.location.reload()
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-bg px-6">
      <div className="w-full max-w-sm">
        <h1 className="text-[1.375rem] font-semibold tracking-tight text-ink">Welcome back</h1>
        <p className="mt-1 text-sm text-ink-soft">Enter your password to carry on.</p>

        <form
          className="mt-5 space-y-3"
          onSubmit={(e) => {
            e.preventDefault()
            void submit()
          }}
        >
          <input
            type="password"
            autoFocus
            autoComplete="current-password"
            value={password}
            onChange={(e) => {
              setPassword(e.target.value)
              setWhy(null)
            }}
            className="w-full rounded-lg border border-line bg-surface px-3.5 py-2.5 text-[1rem] text-ink outline-none transition focus:border-accent"
            placeholder="Password"
          />
          {why && <p className="text-xs text-danger">{why}</p>}
          <button
            type="submit"
            disabled={busy || password.length === 0}
            className="w-full rounded-lg bg-accent px-4 py-2.5 text-sm font-medium text-on-accent transition disabled:opacity-40"
          >
            {busy ? 'Checking…' : 'Continue'}
          </button>
        </form>
      </div>
    </div>
  )
}

/// Everything hangs off knowing whose memory this is, so it is asked once, here, before the app mounts.
///
/// Not a config file and not a restart. The graph needs a node for "me" or a sentence about your brother records the
/// brother and quietly loses the brother-of-you part, which is the half that makes him findable — and a placeholder owner
/// is worse than none, because it looks deliberate.
function Introduce() {
  const [identity, setIdentity] = useState<Identity | null>(null)
  const [asked, setAsked] = useState(false)

  useEffect(() => {
    let live = true
    fetchIdentity().then(found => {
      if (!live) return
      setIdentity(found)
      setAsked(true)
    })
    return () => {
      live = false
    }
  }, [])

  // Nothing is shown until the answer is in. A flash of the setup page over a brain that is already set up would be a
  // worse first impression than a moment of nothing.
  if (!asked) return null

  // A failed fetch is "don't know", never "not set up" — the app carries on rather than interrupting for a network blip.
  if (identity && !identity.known) return <Setup onDone={setIdentity} />

  return <Chat name={identity?.name} assistant={identity?.assistant} />
}

/// A few to pick from, so naming it is one tap rather than a blank box nobody wants to fill in.
const SUGGESTED = ['Ada', 'Fig', 'Mo', 'Otto', 'Pip', 'Wren']

function Setup({ onDone }: { onDone: (identity: Identity) => void }) {
  const [name, setName] = useState('')
  const [assistant, setAssistant] = useState('')
  const [email, setEmail] = useState('')
  const [saving, setSaving] = useState(false)
  const [failed, setFailed] = useState<string | null>(null)

  const ready = name.trim().length >= 2 && assistant.trim().length >= 2

  const submit = async () => {
    if (!ready || saving) return
    setSaving(true)
    setFailed(null)
    const saved = await saveIdentity(name.trim(), assistant.trim(), email.trim())
    if (saved) onDone(saved)
    else {
      setFailed("That didn't save — try again.")
      setSaving(false)
    }
  }

  return (
    <div className="min-h-dvh flex items-center justify-center px-6 bg-neutral-950 text-neutral-100">
      <div className="w-full max-w-sm animate-rise">
        <div className="text-5xl mb-6">👋</div>
        <h1 className="text-2xl font-semibold tracking-tight">What should I call you?</h1>

        <input
          autoFocus
          value={name}
          onChange={e => setName(e.target.value)}
          onKeyDown={e => e.key === 'Enter' && submit()}
          placeholder="Your name"
          className="mt-7 w-full rounded-xl bg-neutral-900 border border-neutral-800 px-4 py-3 text-base
                     outline-none focus:border-neutral-600 transition"
        />

        <input
          value={email}
          onChange={e => setEmail(e.target.value)}
          onKeyDown={e => e.key === 'Enter' && submit()}
          placeholder="Email (optional)"
          className="mt-3 w-full rounded-xl bg-neutral-900 border border-neutral-800 px-4 py-3 text-base
                     outline-none focus:border-neutral-600 transition"
        />

        <h2 className="mt-9 text-2xl font-semibold tracking-tight">Pick a name for me</h2>

        <div className="mt-4 flex flex-wrap gap-2">
          {SUGGESTED.map((option, i) => (
            <button
              key={option}
              onClick={() => setAssistant(option)}
              style={{ animationDelay: `${i * 40}ms` }}
              className={`animate-rise rounded-full px-4 py-2 text-sm border transition ${
                assistant === option
                  ? 'bg-neutral-100 text-neutral-900 border-neutral-100'
                  : 'bg-neutral-900 text-neutral-300 border-neutral-800 hover:border-neutral-600'
              }`}
            >
              {option}
            </button>
          ))}
        </div>

        <input
          value={assistant}
          onChange={e => setAssistant(e.target.value)}
          onKeyDown={e => e.key === 'Enter' && submit()}
          placeholder="Or type your own"
          className="mt-3 w-full rounded-xl bg-neutral-900 border border-neutral-800 px-4 py-3 text-base
                     outline-none focus:border-neutral-600 transition"
        />

        {failed && <p className="mt-4 text-sm text-red-400">{failed}</p>}

        <button
          onClick={submit}
          disabled={!ready || saving}
          className="mt-5 w-full rounded-xl bg-neutral-100 text-neutral-900 font-medium py-3.5
                     disabled:opacity-40 disabled:cursor-not-allowed active:scale-[0.99] transition"
        >
          {saving ? 'Saving…' : 'Continue'}
        </button>
      </div>
    </div>
  )
}

function Chat({ name, assistant }: { name?: string; assistant?: string }) {
  const me = assistant && assistant.length > 0 ? assistant : 'Smarty'
  // Remember whether you were on the home or in the conversation, so a refresh lands you back where you were.
  const [view, setView] = useState<'home' | 'chat'>(initialView)
  const [messages, setMessages] = useState<UiMessage[]>([])
  const [working, setWorking] = useState<Working[]>([])
  // Cards keyed by the message they belong to. They arrive after it — the lookups are remote.
  const [linkCards, setLinkCards] = useState<Record<number, LinkCard[]>>({})
  // Files a task delivered, and the one currently open full-screen.
  const [files, setFiles] = useState<{ name: string; caption?: string; msgId?: number }[]>([])
  // The most recent message id seen, so a file with no msgId of its own still has somewhere sensible to go.
  const lastMsgId = useRef<number | undefined>(undefined)
  const [presenting, setPresenting] = useState<string | null>(null)
  // The remote browser, for signing in to something while away from the machine.
  const [browsing, setBrowsing] = useState(false)
  // Workers that paused to ask the user something. They sit "in your face" until answered.
  const [questions, setQuestions] = useState<WorkerQuestion[]>([])
  const [tasksOpen, setTasksOpen] = useState(false)
  const [input, setInput] = useState('')
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [projects, setProjects] = useState<ProjectSummary[]>([])

  /// Asked for before anything is deleted, so the modal can say what goes with it rather than asking you to guess.
  const [removing, setRemoving] = useState<Removal | null>(null)
  const [removingBusy, setRemovingBusy] = useState(false)
  const [activeProject, setActiveProject] = useState<ProjectDetail | null>(null)
  const [projectLoading, setProjectLoading] = useState(false)
  /*
   * Always on: listening for its own name, and taking the screen over when it hears it.
   *
   * The state kept here is only what the app around it needs — whether it is on at all, and the assistant's latest
   * message so it can be captioned and read out. Everything else (the microphone, the waiting, the three-second
   * silence, the speaking) belongs to AlwaysOn, because none of it is any of the chat's business.
   */
  const [alwaysOn, setAlwaysOn] = useState(() => {
    try {
      return localStorage.getItem(ALWAYS_ON_KEY) === '1'
    } catch {
      return false
    }
  })
  const [micTrouble, setMicTrouble] = useState<string | null>(null)

  const [recording, setRecording] = useState(false)
  const [recPeaks, setRecPeaks] = useState<number[]>([])
  // Finishing the transcription after the user has stopped. The one moment that used to be silent.
  const [transcribing, setTranscribing] = useState(false)
  const [recSeconds, setRecSeconds] = useState(0)
  const [, setNow] = useState(Date.now())
  // Files picked but not yet sent. Held here rather than uploaded on selection: they go up with the message
  // they belong to, so removing one before sending costs nothing and nothing is left orphaned if you change
  // your mind. It also keeps the server's "next message takes them" rule true — one upload, one message.
  // The retained conversations, and the counter the event stream is subscribed under — bumping it moves the
  // stream to another chat.
  const [chats, setChats] = useState<ChatSummary[]>([])
  // Standing work. Re-read on a timer as well as on change, because the interesting field is "next in 6h" and
  // the whole list also moves on its own when something fires.
  const [schedules, setSchedules] = useState<Schedule[]>([])
  // The home page itself, as panels the system decided on.
  const [widgets, setWidgets] = useState<Widget[]>([])
  const [streamKey, setStreamKey] = useState(0)
  // Sent, but the server hasn't echoed it yet — the window that used to look like nothing had happened.
  const [awaitingEcho, setAwaitingEcho] = useState(false)
  const [attached, setAttached] = useState<File[]>([])
  const [uploading, setUploading] = useState(false)
  const [attachError, setAttachError] = useState<string | null>(null)
  const [dragging, setDragging] = useState(false)

  const sessionId = useRef(getSessionId())
  const greeting = useRef(greetingText(name))
  const scrollRef = useRef<HTMLDivElement>(null)
  const taRef = useRef<HTMLTextAreaElement>(null)
  const atBottomRef = useRef(true)
  const recRef = useRef<{ mr: MediaRecorder; ctx: AudioContext; sampler: number } | null>(null)
  const partialBusy = useRef(false)
  const cancelledRef = useRef(false)
  const pendingAudio = useRef<AudioNote | null>(null)
  const pendingFiles = useRef<string[] | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  // Questions from conversations that aren't open. The session's own stream can't see them, and something waiting
  // silently on an answer is the same as it having stopped.
  const [elsewhere, setElsewhere] = useState<OutstandingQuestion[]>([])

  // Conversations the assistant started on its own, because something it was watching for happened. Same problem as
  // the questions above and the sharper version of it: nobody would ever think to open a chat they did not start.
  const [nudges, setNudges] = useState<Nudge[]>([])

  const refreshProjects = () => fetchProjects().then(setProjects)

  const askToRemoveProject = async (slug: string) => {
    const what = await fetchRemoval(slug)
    if (what) setRemoving(what)
  }

  const confirmRemoveProject = async () => {
    if (!removing) return
    setRemovingBusy(true)
    const gone = await deleteProject(removing.slug)
    setRemovingBusy(false)
    if (!gone) return
    setRemoving(null)
    await refreshProjects()
  }
  const refreshChats = () => fetchChats().then(setChats)
  const refreshSchedules = () => fetchSchedules().then(setSchedules)
  const refreshWidgets = () => fetchWidgets().then(setWidgets)
  const refreshElsewhere = () => fetchQuestions().then(setElsewhere)
  const refreshNudges = () => fetchNudges().then(setNudges)


  /** Book standing work from the home page. Returns the server's complaint, or null when it went in. */
  async function addSchedule(task: string, repeat: string): Promise<string | null> {
    const res = await createSchedule({ task, repeat })
    await refreshSchedules()
    return res.ok ? null : res.error
  }

  async function pauseSchedule(id: string, paused: boolean) {
    // Optimistic, because the only thing that changes is a word and a dot, and waiting on a round trip to
    // redraw those reads as the button not having worked.
    setSchedules((prev) => prev.map((s) => (s.id === id ? { ...s, status: paused ? 'paused' : 'pending' } : s)))
    await updateSchedule(id, { paused })
    refreshSchedules()
  }

  async function dropSchedule(id: string) {
    setSchedules((prev) => prev.filter((s) => s.id !== id))
    await cancelSchedule(id)
    refreshSchedules()
  }

  function upsert(id: number, fn: (m: UiMessage) => UiMessage, role?: 'user' | 'assistant') {
    setMessages((prev) => {
      const idx = prev.findIndex((m) => m.id === id)
      if (idx >= 0) {
        const next = prev.slice()
        next[idx] = fn(next[idx])
        return next
      }
      const fresh: UiMessage = { id, role: role ?? 'assistant', content: '', reasoning: '', streaming: true }
      return [...prev, fn(fresh)]
    })
  }

  /**
   * Smooth out the stream.
   *
   * The provider hands us big, uneven chunks — a paragraph lands in one go, then nothing for a second — and
   * appending each straight to state is exactly what makes it read as clunky. So deltas go into a per-message
   * backlog and are released at a steady character rate, with the rate scaling to the backlog so a burst catches
   * up instead of falling behind. The text is identical; only the arrival is even.
   */
  const backlog = useRef<Map<number, string>>(new Map())
  const drainTimer = useRef<number | null>(null)

  function startDraining() {
    if (drainTimer.current !== null) return
    drainTimer.current = window.setInterval(() => {
      if (backlog.current.size === 0) {
        window.clearInterval(drainTimer.current!)
        drainTimer.current = null
        return
      }
      for (const [id, text] of Array.from(backlog.current.entries())) {
        // Release a slice proportional to what's waiting: ~2 chars minimum, more when behind, so long answers
        // don't crawl and short ones still feel typed rather than pasted.
        const take = Math.max(3, Math.ceil(text.length / 8))
        const piece = text.slice(0, take)
        const rest = text.slice(take)
        if (rest.length > 0) backlog.current.set(id, rest)
        else backlog.current.delete(id)
        upsert(id, (m) => ({
          ...m,
          content: m.content + piece,
          thinkMs: m.thinkMs ?? (m.thinkStart ? Date.now() - m.thinkStart : undefined),
        }))
      }
    }, 24)
  }

  function enqueue(id: number, text: string) {
    backlog.current.set(id, (backlog.current.get(id) ?? '') + text)
    startDraining()
  }

  /** Release everything waiting for a message at once — used when the turn ends. */
  function flush(id: number) {
    const rest = backlog.current.get(id)
    backlog.current.delete(id)
    if (rest) upsert(id, (m) => ({ ...m, content: m.content + rest }))
  }

  // The single persistent connection to the session — everything the assistant says (and results pushed
  // back from background workers) arrives here, whichever view you're looking at.
  useEffect(() => {
    const controller = new AbortController()
    const handlers: SessionHandlers = {
      onMsgStart: (id, role) => {
        lastMsgId.current = id
        const audio = role === 'user' && pendingAudio.current ? pendingAudio.current : undefined
        if (audio) pendingAudio.current = null
        const sent = role === 'user' && pendingFiles.current ? pendingFiles.current : undefined
        if (sent) pendingFiles.current = null
        // The real thing has arrived — retire the placeholder standing in for it.
        if (role === 'user') {
          setAwaitingEcho(false)
          setMessages((prev) => (prev.some((m) => m.id === OptimisticId) ? prev.filter((m) => m.id !== OptimisticId) : prev))
        }
        upsert(id, (m) => ({ ...m, role: role as 'user' | 'assistant', streaming: true, audio: audio ?? m.audio, files: sent ?? m.files }), role as 'user' | 'assistant')
      },
      onContent: (id, text) => enqueue(id, text),
      onReasoning: (id, text) => upsert(id, (m) => ({ ...m, reasoning: m.reasoning + text, thinkStart: m.thinkStart ?? Date.now() })),
      onMsgEnd: (id, text) => {
        flush(id) // release any backlog immediately; the final text is authoritative anyway
        upsert(id, (m) => ({
          ...m,
          streaming: false,
          content: text && text.length > 0 ? text : m.content,
          thinkMs: m.thinkMs ?? (m.thinkStart ? Date.now() - m.thinkStart : undefined),
        }))
      },
      onWorking: (id, task, msgId, at) => {
        // Resuming (or starting) clears any pending question for this task.
        setQuestions((q) => q.filter((x) => x.taskId !== id))
        setWorking((w) =>
          w.some((x) => x.id === id)
            ? w.map((x) =>
                x.id === id
                  ? {
                      ...x,
                      task,
                      // A `working` event means it is running NOW, so any outcome we were holding is stale. This
                      // matters for a task that resumes after asking a question (it would otherwise stay
                      // "Waiting on you" for ever) and it's the difference between a spinner and a tick.
                      status: undefined,
                      endedAt: undefined,
                      startedAt: at ?? x.startedAt,
                      // And it belongs to the message that started THIS run. Keeping the old one is what pinned a
                      // new job to a previous message — visible when a task id gets recycled, because then two
                      // different jobs arrive under one id and the first one's message wins.
                      msgId: msgId ?? x.msgId,
                    }
                  : x,
              )
            // The server's own start time when it gave one. Using the arrival time instead is what made every
            // replayed task claim it ran for 0:00 — start and finish both "happened" as the chat reopened.
            : [...w, { id, task, startedAt: at ?? Date.now(), msgId }],
        )
      },
      // The task is over, but the reply isn't said yet — a finalize pass and the re-voice still have to run. So
      // the bar stays and collapses to its outcome instead of disappearing, which read as a hang.
      onWorkingDone: (id, status, at) =>
        setWorking((w) =>
          w.map((x) => (x.id === id ? { ...x, status: status ?? 'done', steps: [], endedAt: at ?? Date.now() } : x)),
        ),
      onLinks: (msgId, cards) => setLinkCards((l) => ({ ...l, [msgId]: cards })),
      onFile: (name, caption, msgId) =>
        setFiles((f) =>
          f.some((x) => x.name === name)
            ? f
            : // No msgId means an older server: attach it to the newest message so it still lands beside the
              // answer rather than in a pile at the bottom of the conversation.
              [...f, { name, caption, msgId: msgId ?? lastMsgId.current }],
        ),
      onThinking: (taskId, text) =>
        setWorking((w) =>
          w.map((x) => {
            if (x.id !== taskId) return x
            const steps = [...(x.steps ?? [])]
            // One rolling "thinking" row, replaced in place — it's a state, not a list of events. Any completed
            // tool row stays; an unfinished one is what the model is thinking *about*, so it stays too.
            const at = steps.findIndex((s) => s.thinking)
            const row = { key: at >= 0 ? steps[at].key : Date.now(), name: 'thinking', detail: text, done: false, thinking: true }
            if (at >= 0) steps[at] = row
            else steps.push(row)
            return { ...x, steps: steps.slice(-TASK_STEPS_SHOWN) }
          }),
        ),
      onTool: (taskId, name, detail, done) =>
        setWorking((w) =>
          w.map((x) => {
            if (x.id !== taskId) return x
            const steps = [...(x.steps ?? [])]
            if (done) {
              // Mark the most recent matching call finished rather than adding a second row for it.
              for (let i = steps.length - 1; i >= 0; i--)
                if (steps[i].name === name && !steps[i].done) {
                  steps[i] = { ...steps[i], done: true }
                  break
                }
            } else {
              const at = steps.findIndex((s) => s.thinking)
              if (at >= 0) steps.splice(at, 1) // it's doing something now, not thinking
              steps.push({ key: Date.now() + steps.length, name, detail, done: false })
            }
            return { ...x, steps: steps.slice(-TASK_STEPS_SHOWN) }
          }),
        ),
      onQuestion: (q) => setQuestions((prev) => (prev.some((x) => x.taskId === q.taskId) ? prev : [...prev, q])),
    }
    // History first, as content — then the live stream from where the history ended.
    //
    // This used to open the stream at offset 0, so every reload re-delivered the entire conversation through the
    // handlers above: text came back through the typewriter drip, and each task's outcome had to be re-derived
    // from its events. Now the server hands over the finished article and the stream only carries what happens
    // next, which is what a stream is for.
    void (async () => {
      const snapshot = await fetchSnapshot(sessionId.current)
      if (controller.signal.aborted) return // switched chats while it was in flight
      if (snapshot) applySnapshot(snapshot)
      openSessionStream(sessionId.current, handlers, controller.signal, snapshot?.next ?? 0)
    })()

    refreshProjects()
    refreshChats()
    refreshSchedules()
    refreshWidgets()
    refreshElsewhere()
    refreshNudges()
    return () => controller.abort()
    // streamKey, not the id: switching conversations bumps it, which tears this down and reopens against
    // whatever sessionId.current now points at.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [streamKey])

  // Deep linking: open whatever the URL points at on load, and keep in sync with back/forward. Two kinds of
  // address — /project/<slug> and /chats/<id>.
  useEffect(() => {
    const initialProject = projectFromPath()
    if (initialProject) openProject(initialProject, false)
    // The session id already came from the path (see getSessionId), so landing on /chats/<id> only needs the
    // view moved to the conversation.
    if (chatFromPath()) setView('chat')

    const onPop = () => {
      const slug = projectFromPath()
      if (slug) openProject(slug, false)
      else closeProject(false)

      const chat = chatFromPath()
      if (chat) openChat(chat, false)
      else if (!slug) setView('home') // back out of a conversation to the home
    }
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Which of these are ACTUALLY still going.
  //
  // `working` is every task this conversation has, finished ones included — the in-chat bar needs them so it can
  // show what became of each. Anything that reports on what's happening NOW has to filter, and didn't: reopening
  // a chat replayed its old tasks and the home page listed them under "Running now", progress bar and all, for
  // work that had finished hours earlier.
  const running = working.filter((t) => !outcomeOf(t.status))

  // Tick once a second while anything is running, so elapsed timers stay live.
  useEffect(() => {
    if (running.length === 0) return
    const t = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(t)
  }, [running.length])

  // Re-read the schedule while the home page is up. The countdown comes from the server (the browser's clock and
  // a stored instant disagree, and on a phone that has been asleep they disagree by a lot), so keeping "next in
  // 6h" honest means asking again rather than counting down locally. Only while it's on screen and only when
  // there's something to count — a minute is plenty for a list measured in hours.
  useEffect(() => {
    if (view !== 'home' || schedules.length === 0) return
    const t = setInterval(refreshSchedules, 60_000)
    return () => clearInterval(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, schedules.length])

  // The panels change on their own — an agent one goes and looks on its cadence, and a just-created one fills in
  // within half a minute. Only while the page is actually on screen; a background tab needs no news.
  useEffect(() => {
    if (view !== 'home') return
    const t = setInterval(refreshWidgets, 20_000)
    return () => clearInterval(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view])

  function onScroll() {
    const el = scrollRef.current
    if (!el) return
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80
  }
  useLayoutEffect(() => {
    const el = scrollRef.current
    if (el && atBottomRef.current && view === 'chat') el.scrollTop = el.scrollHeight
  }, [messages, working, view])

  useEffect(() => {
    const el = taRef.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = Math.min(Math.max(el.scrollHeight, 24), 200) + 'px'
  }, [input, view])

  useEffect(() => {
    if (working.length === 0) setTasksOpen(false)
  }, [working.length])

  // Keep the server's idea of where we are current, but only ever with permission already granted — this never
  // triggers a browser prompt. Refreshed when the tab comes back rather than on a timer: the position only
  // matters when someone is actually here to ask something of it.
  useEffect(() => {
    void reportLocation()
    const onVisible = () => {
      if (document.visibilityState === 'visible') void reportLocation()
    }
    document.addEventListener('visibilitychange', onVisible)
    return () => document.removeEventListener('visibilitychange', onVisible)
  }, [])

  // Dropping a file anywhere on the window attaches it — the composer is a small target and the whole page
  // reads as the conversation. dragleave only counts when the pointer has left the window entirely
  // (relatedTarget null), otherwise crossing between elements flickers the overlay off.
  useEffect(() => {
    const over = (e: DragEvent) => {
      if (!e.dataTransfer?.types.includes('Files')) return
      e.preventDefault()
      setDragging(true)
    }
    const leave = (e: DragEvent) => {
      if (e.relatedTarget === null) setDragging(false)
    }
    const drop = (e: DragEvent) => {
      if (!e.dataTransfer?.files.length) return
      e.preventDefault()
      setDragging(false)
      addFiles(Array.from(e.dataTransfer.files))
    }
    window.addEventListener('dragover', over)
    window.addEventListener('dragleave', leave)
    window.addEventListener('drop', drop)
    return () => {
      window.removeEventListener('dragover', over)
      window.removeEventListener('dragleave', leave)
      window.removeEventListener('drop', drop)
    }
  }, [])

  useEffect(() => {
    try {
      localStorage.setItem(VIEW_KEY, view)
    } catch {
      /* ignore */
    }
  }, [view])

  function goHome() {
    setView('home')
    setChatPath(null)
    refreshProjects()
    refreshChats()
  }
  function openDrawer() {
    setDrawerOpen(true)
    refreshProjects()
    refreshChats()
  }

  // Whatever conversation you're looking at owns the address bar, so a refresh comes back to it. replaceState:
  // being in a chat isn't a navigation, and pushing here would stack an entry per message.
  useEffect(() => {
    if (view !== 'chat' || projectFromPath()) return // a project's URL wins while its page is open
    if (chatFromPath() !== sessionId.current) setChatPath(sessionId.current, true)
  }, [view, streamKey, messages.length])
  // Projects are deep-linked at /project/<slug>, so a refresh (or a shared link) lands you back on the
  // project page instead of bouncing home. `push` controls whether this opening adds a history entry —
  // false when we're reacting to the URL itself (initial load / back-forward), true when the user clicked in.
  async function openProject(slug: string, push = true) {
    setDrawerOpen(false)
    if (push) setProjectPath(slug)
    setProjectLoading(true)
    setActiveProject(null)
    const detail = await fetchProject(slug)
    setActiveProject(detail)
    setProjectLoading(false)
    if (!detail) setProjectPath(null) // bad/stale slug — don't leave it in the URL
  }
  function closeProject(push = true) {
    setActiveProject(null)
    setProjectLoading(false)
    if (push) setProjectPath(null)
  }

  /**
   * Render a conversation's history in one go.
   *
   * Deliberately a straight assignment rather than a fold: the server has already worked out what each message
   * says, which files hang off it and how every task ended, so there is nothing here to derive — and nothing to
   * animate. Messages arrive complete, with `streaming: false`, so the drip never touches history.
   */
  function applySnapshot(snapshot: ConversationSnapshot) {
    setMessages(
      snapshot.messages.map((m) => ({
        id: m.id,
        role: m.role,
        content: m.text,
        reasoning: '',
        streaming: false,
        files: m.files.length > 0 ? m.files.map((f) => f.name) : undefined,
      })),
    )

    setFiles(
      snapshot.messages.flatMap((m) =>
        m.files.map((f) => ({ name: f.name, caption: f.caption, msgId: m.id })),
      ),
    )

    setLinkCards(
      Object.fromEntries(
        snapshot.messages.filter((m) => m.links.length > 0).map((m) => [m.id, m.links]),
      ),
    )

    setWorking(
      snapshot.tasks.map((t) => ({
        id: t.id,
        task: t.task,
        msgId: t.msgId,
        status: t.status,
        startedAt: t.startedAt ? Date.parse(t.startedAt) : Date.now(),
        endedAt: t.endedAt ? Date.parse(t.endedAt) : undefined,
      })),
    )

    setQuestions(
      snapshot.questions.map((q) => ({
        taskId: q.id,
        question: q.question,
        options: q.options ?? [],
        project: q.project ?? null,
        kind: q.kind ?? 'text',
        number: q.number ?? null,
        place: q.place ?? null,
      })),
    )

    const last = snapshot.messages[snapshot.messages.length - 1]
    lastMsgId.current = last?.id
  }

  /**
   * Put what you just said on screen, now, without waiting to be told.
   *
   * Every message used to appear only when the server echoed it back — and the server echoes it from inside the
   * turn lock, so if anything else was mid-turn (a task finalising, a previous answer still streaming) your own
   * words simply weren't there. Sending felt like nothing had happened, for as long as the lock was held.
   *
   * It's shown under a temporary negative id, which no real message can collide with, and the real echo replaces
   * it — so the ordinary path stays the source of truth and nothing is duplicated.
   */
  function showSent(text: string, files: string[]) {
    setMessages((prev) => [
      ...prev.filter((m) => m.id !== OptimisticId),
      {
        id: OptimisticId,
        role: 'user',
        content: text,
        reasoning: '',
        streaming: false,
        files: files.length > 0 ? files : undefined,
      },
    ])
    setAwaitingEcho(true)
  }

  /** Take a pick or a drop and hold it. Deduped by name+size, so dropping the same file twice attaches one. */
  function addFiles(picked: File[]) {
    const tooBig = picked.filter((f) => f.size > MaxUploadBytes)
    const ok = picked.filter((f) => f.size > 0 && f.size <= MaxUploadBytes)
    // The server silently skips anything oversized, which would read as the file having been sent. Say it.
    setAttachError(
      tooBig.length > 0
        ? `${tooBig.map((f) => f.name).join(', ')} — too big to send (limit ${MaxUploadBytes / 1024 / 1024}MB).`
        : null,
    )
    if (ok.length === 0) return
    setAttached((prev) => [
      ...prev,
      ...ok.filter((f) => !prev.some((p) => p.name === f.name && p.size === f.size)),
    ])
  }

  /**
   * Push what's attached to the session, so the message that follows carries it. Empty array when there was
   * nothing to send; null when the upload failed — in which case the caller must NOT post the message, or the
   * assistant would be told about files that never arrived.
   */
  async function uploadAttached(): Promise<string[] | null> {
    if (attached.length === 0) return []
    setUploading(true)
    try {
      const saved = await uploadFiles(sessionId.current, attached)
      setAttached([])
      setAttachError(null)
      return saved.map((f) => f.name)
    } catch (e) {
      // "Try again" is only useful advice when trying again could work. A 404/405 means this build has no
      // upload endpoint at all — the page is newer than the server it's talking to — and retrying that for
      // ever is exactly the wrong thing to tell someone.
      const status = e instanceof UploadFailed ? e.status : 0
      setAttachError(
        status === 404 || status === 405
          ? 'This server has no upload endpoint — it needs restarting to pick up the new build.'
          : "That didn't upload — nothing has been sent, so try again.",
      )
      return null
    } finally {
      setUploading(false)
    }
  }

  // Sending anything (typed or voice) drops you into the conversation.
  async function send(text?: string) {
    const body = (text ?? input).trim()
    if (uploading) return
    if (!body && attached.length === 0) return

    const names = await uploadAttached()
    if (names === null) return // failed; the text and the files are still here to retry with

    setInput('')
    if (names.length > 0) pendingFiles.current = names
    setView('chat')
    atBottomRef.current = true
    // Attachments with no words of their own still need a message to ride on — the filenames are the most
    // honest thing to put in the user's own bubble, and what the assistant is told about them is separate.
    const outgoing = body || names.join(', ')
    showSent(outgoing, names)
    sendMessage(sessionId.current, outgoing)
  }

  async function sendVoice(rec: RecordedAudio) {
    // The wait used to be silent and total: stop talking, then several seconds of nothing before the message
    // appeared. Now the partials have already been filling the box while you spoke, so this pass only has to
    // finish the job — and it says it is doing so.
    setTranscribing(true)
    let text = ''
    try {
      text = await transcribe(rec.wav)
    } catch {
      /* ignore */
    }
    setTranscribing(false)

    // Fall back to whatever the partials got if the final pass fails: some text beats losing the note.
    const body = (text || input).trim()
    if (!body) return

    // A voice note can have files attached too — same rule, they go up before the message does.
    const names = await uploadAttached()
    if (names === null) return

    setInput('')
    pendingAudio.current = { peaks: rec.peaks, duration: rec.duration, url: URL.createObjectURL(rec.wav) }
    if (names.length > 0) pendingFiles.current = names
    setView('chat')
    atBottomRef.current = true
    showSent(body, names)
    sendMessage(sessionId.current, body)
  }

  /**
   * Transcribe what has been said SO FAR, while the recording continues.
   *
   * Whisper needs whole audio rather than a stream, so each pass re-reads everything recorded up to now — which
   * also keeps the wording coherent, where transcribing three-second slices in isolation would not. One request at
   * a time: if a pass is still running when the next chunk lands, that chunk simply waits for the one after.
   */
  async function transcribePartial(chunks: Blob[], mimeType: string) {
    if (partialBusy.current || chunks.length === 0) return
    partialBusy.current = true
    try {
      const wav = await toWav16k(new Blob(chunks, { type: mimeType }))
      const text = await transcribe(wav.wav)
      // Discard a partial that lands after the user has stopped — the final pass owns the box by then.
      if (text && recRef.current) setInput(text)
    } catch {
      /* a dropped partial costs nothing; the next one is along in a moment */
    } finally {
      partialBusy.current = false
    }
  }

  /**
   * Point everything at another conversation. Every bit of state below belongs to the chat being left — leave any
   * of it behind and the new chat inherits the old one's files, cards and half-streamed text.
   *
   * Bumping streamKey is what actually moves us: the event stream is subscribed under it, so it tears down and
   * reopens against the new session, and the server replays that conversation from disk on the way back.
   */
  function switchTo(id: string) {
    sessionId.current = id
    try {
      localStorage.setItem(SESSION_KEY, id)
    } catch {
      /* ignore */
    }
    setMessages([])
    setWorking([])
    setFiles([])
    setLinkCards({})
    setQuestions([])
    setPresenting(null)
    setAttached([])
    setAttachError(null)
    setAwaitingEcho(false)
    setInput('')
    lastMsgId.current = undefined
    backlog.current.clear()
    pendingAudio.current = null
    pendingFiles.current = null
    atBottomRef.current = true
    setStreamKey((k) => k + 1)
  }

  /**
   * Delete a conversation. If it's the one you're in, move to a fresh one first — otherwise you'd be sitting in
   * a chat whose history has just been thrown away, and the next thing you said would re-create it.
   */
  async function removeChat(id: string) {
    setChats((prev) => prev.filter((c) => c.id !== id))
    if (id === sessionId.current) newChat()
    await deleteChat(id)
    refreshChats()
  }

  /** Open a retained conversation from the sidebar. `push` is false when the URL is what sent us here. */
  function openChat(id: string, push = true) {
    setDrawerOpen(false)
    // Reading it is what clears the nudge, and the card goes now rather than after the round trip: it was clicked,
    // so it has been seen, whatever the server says a moment later.
    if (nudges.some((n) => n.session === id)) {
      setNudges((prev) => prev.filter((n) => n.session !== id))
      void markChatOpened(id)
    }
    if (id === sessionId.current) {
      setView('chat')
      return
    }
    switchTo(id)
    setView('chat')
    if (push) setChatPath(id)
  }

  /* ---- always on ------------------------------------------------------------------------------------------- */

  /**
   * The assistant's latest message, for the caption and the voice.
   *
   * Read off the messages the stream is already filling rather than kept separately, so the words appear on the big
   * screen at the same rate they appear in the chat — and so the answer being read out is provably the answer that
   * was logged.
   */
  /**
   * EVERY reply, in the order they arrived — not just the latest one.
   *
   * It used to hand over only the last assistant message, and that quietly threw messages away: a turn is routinely
   * two ("on it, checking now", then the answer), and if the second landed in the same render as the first, the first
   * was never seen by the voice at all. Reading them out is a queue, not a snapshot, so the queue is what gets passed.
   */
  const replies = messages
    .filter((m) => m.role === 'assistant')
    .map((m) => ({ id: m.id, text: m.content, streaming: !!m.streaming }))

  /**
   * Whether the assistant is still working on something.
   *
   * Always-on listening needs this to know the difference between a silence that means "we are done here" and one that
   * means "it is thinking". Without it, fifteen seconds of the user waiting politely for an answer closed the
   * conversation and sent them back to the home page while the answer was still being written.
   */
  const busy = working.length > 0 || replies.some((r) => r.streaming) || awaitingEcho

  function toggleAlwaysOn() {
    const next = !alwaysOn
    setAlwaysOn(next)
    setMicTrouble(null)
    try {
      localStorage.setItem(ALWAYS_ON_KEY, next ? '1' : '0')
    } catch {
      /* a preference that cannot be saved is still a preference for this tab */
    }
  }

  /**
   * Called by name: a new conversation for what follows.
   *
   * Deliberately a fresh one every time rather than continuing whatever was last open. Somebody who walks up and
   * says its name is starting something, not resuming a chat from Tuesday — and the chat they left is still in the
   * list, unchanged, which it would not be if this talked into it.
   */
  function wakeChat() {
    setChatPath(null, true)
    switchTo(crypto.randomUUID())
    setView('chat')
    refreshChats()
  }

  /** What was heard, sent as an ordinary message — because that is all it is. */
  function saySomething(text: string) {
    atBottomRef.current = true
    showSent(text, [])
    sendMessage(sessionId.current, text)
  }

  function newChat() {
    // replace, not push: a brand-new chat has nothing in it, so leaving it in the back stack just gives you a
    // Back button that returns to an empty room.
    setChatPath(null, true)
    switchTo(crypto.randomUUID())
    setView('home') // a fresh chat starts you back at the home
    refreshChats()
  }

  async function cancelRunningTask(id: string) {
    setWorking((w) => w.filter((x) => x.id !== id))
    try {
      await cancelTask(sessionId.current, id)
    } catch {
      /* the stream will restore state if the task is still running */
    }
  }

  // Answer a worker's question — it resumes from where it paused; the reply comes back in the conversation.
  // Put a question down rather than answer it: the work moved on, or another task covered the same ground.
  function dropQuestion(taskId: string) {
    setQuestions((q) => q.filter((x) => x.taskId !== taskId))
    void dismissQuestion(sessionId.current, taskId)
  }

  function answerQuestion(taskId: string, text: string) {
    const body = text.trim()
    if (!body) return
    setQuestions((q) => q.filter((x) => x.taskId !== taskId))
    answerTask(sessionId.current, taskId, body).catch(() => {})
  }

  // ---- recording ----
  async function startRecording() {
    if (recording) return
    let stream: MediaStream
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true })
    } catch {
      return
    }
    const mr = new MediaRecorder(stream)
    const chunks: Blob[] = []
    mr.ondataavailable = (e) => {
      if (!e.data.size) return
      chunks.push(e.data)
      // Each chunk is a chance to have the text ready before the user finishes talking.
      void transcribePartial(chunks, mr.mimeType || 'audio/webm')
    }
    mr.onstop = () => {
      const r = recRef.current
      if (r) {
        clearInterval(r.sampler)
        r.ctx.close()
      }
      stream.getTracks().forEach((t) => t.stop())
      recRef.current = null
      setRecording(false)
      setRecPeaks([])
      setRecSeconds(0)
      const blob = new Blob(chunks, { type: mr.mimeType || 'audio/webm' })
      if (cancelledRef.current) {
        cancelledRef.current = false
        setInput('') // the partials had been filling the box; a cancelled note leaves nothing behind
        return
      }
      toWav16k(blob).then(sendVoice).catch(() => {})
    }
    const AC: typeof AudioContext =
      window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    const ctx = new AC()
    const analyser = ctx.createAnalyser()
    analyser.fftSize = 256
    ctx.createMediaStreamSource(stream).connect(analyser)
    const data = new Uint8Array(analyser.fftSize)
    const startTime = Date.now()
    const sampler = window.setInterval(() => {
      analyser.getByteTimeDomainData(data)
      let max = 0
      for (let i = 0; i < data.length; i++) {
        const v = Math.abs(data[i] - 128) / 128
        if (v > max) max = v
      }
      setRecPeaks((prev) => [...prev, Math.min(1, max * 1.6)].slice(-REC_BARS))
      setRecSeconds(Math.floor((Date.now() - startTime) / 1000))
    }, 90)
    recRef.current = { mr, ctx, sampler }
    cancelledRef.current = false
    // A timeslice, so chunks arrive during the recording rather than all at the end. Three seconds is long
    // enough to be worth transcribing and short enough that the box keeps up with the speaker.
    mr.start(3000)
    setRecording(true)
  }
  const stopRecording = () => recRef.current?.mr.stop()
  const cancelRecording = () => {
    cancelledRef.current = true
    recRef.current?.mr.stop()
  }

  return (
    <div className="flex h-full flex-col overflow-x-hidden bg-bg text-ink">
      {/*
        Over everything, and only once it has been called by name — until then it is a microphone and a nav icon.
        Mounted here rather than inside a view because being called from across the room has nothing to do with which
        screen you happened to leave open.
      */}
      <AlwaysOn
        enabled={alwaysOn && !micTrouble}
        name={me}
        replies={replies}
        busy={busy}
        onWake={wakeChat}
        onSay={saySomething}
        onStandDown={goHome}
        onDeaf={(why) => {
          setMicTrouble(why)
          setAlwaysOn(false)
          try {
            localStorage.setItem(ALWAYS_ON_KEY, '0')
          } catch {
            /* nothing to do about it */
          }
        }}
      />

      <header className="z-10 flex items-center gap-2.5 border-b border-line bg-bg/80 px-4 py-2.5 backdrop-blur sm:px-6">
        <button
          onClick={openDrawer}
          aria-label="Projects"
          title="Projects"
          className="-ml-1 grid h-9 w-9 shrink-0 place-items-center rounded-md text-ink-soft transition hover:bg-surface-mid hover:text-ink"
        >
          <BurgerIcon />
        </button>
        <button onClick={goHome} title="Home" className="flex items-center gap-2">
          <span className="grid h-7 w-7 shrink-0 place-items-center rounded-full bg-accent text-sm font-semibold text-on-accent">S</span>
          <span className="text-[1rem] font-semibold tracking-tight text-ink">{me}</span>
        </button>
        <div className="ml-auto flex items-center gap-1.5">
          {running.length > 0 && <RunningPill count={running.length} onOpen={() => setTasksOpen(true)} />}
          {/*
            Always on. A switch rather than a button, because it has a state you need to be able to SEE from a
            distance: a microphone that might be listening is not the same as one that is.
          */}
          <button
            onClick={toggleAlwaysOn}
            aria-pressed={alwaysOn}
            aria-label={alwaysOn ? `Stop listening for "Hey ${me}"` : `Listen for "Hey ${me}"`}
            title={micTrouble ?? (alwaysOn ? `Listening for "Hey ${me}"` : `Always on — answer to "Hey ${me}"`)}
            className={`grid h-9 w-9 shrink-0 place-items-center rounded-md transition ${
              micTrouble
                ? 'text-danger hover:bg-surface-mid'
                : alwaysOn
                  ? 'bg-accent-soft text-accent'
                  : 'text-ink-soft hover:bg-surface-mid hover:text-ink'
            }`}
          >
            <EarIcon listening={alwaysOn && !micTrouble} />
          </button>
          {/*
            Next to the ear, because they are the same kind of thing: the two ways this assistant does something
            without being spoken to first. Both carry their state in the header for the same reason — what they
            are doing while you are not looking at them is the only thing worth knowing about either.
          */}
          <ProactButton />
          <button
            onClick={() => setBrowsing(true)}
            aria-label="Your browser"
            title="Your browser — sign in to something without being at the machine"
            className="grid h-9 w-9 shrink-0 place-items-center rounded-md text-ink-soft transition hover:bg-surface-mid hover:text-ink"
          >
            <BrowserIcon />
          </button>
          <button onClick={newChat} className="rounded-md px-2.5 py-1.5 text-xs font-medium text-ink-soft transition hover:bg-surface-mid hover:text-ink">
            New chat
          </button>
        </div>
      </header>

      <main ref={scrollRef} onScroll={onScroll} className="flex-1 overflow-y-auto">
        {view === 'home' ? (
          <HomeView
            greeting={greeting.current}
            working={working}
            questions={questions}
            schedules={schedules}
            widgets={widgets}
            onOpenTasks={() => setTasksOpen(true)}
            onPick={(p) => send(p)}
            onAnswer={answerQuestion}
            onDismiss={dropQuestion}
            onAddSchedule={addSchedule}
            onPauseSchedule={pauseSchedule}
            onCancelSchedule={dropSchedule}
            onOpenChat={openChat}
            onWidgetsChanged={refreshWidgets}
            elsewhere={elsewhere}
            onElsewhereAnswered={refreshElsewhere}
            nudges={nudges}
          />
        ) : (
          <div className="mx-auto max-w-reading px-4 py-6">
            <div className="space-y-6">
              {messages.map((m) => (
                <div key={m.id}>
                  <MessageRow message={m} sessionId={sessionId.current} />
                  <TaskBar tasks={working.filter((w) => w.msgId === m.id)} onCancel={cancelRunningTask} />
                  <LinkCards cards={linkCards[m.id]} />
                  <FileCards
                    files={files.filter((f) => f.msgId === m.id)}
                    sessionId={sessionId.current}
                    onOpen={(name) => setPresenting(name)}
                  />
                </div>
              ))}
              {/* Only files that belong to no message we know about — normally none. */}
              <FileCards
                files={files.filter((f) => f.msgId === undefined || !messages.some((m) => m.id === f.msgId))}
                sessionId={sessionId.current}
                onOpen={(name) => setPresenting(name)}
              />

              {/* Running work that belongs to no message we can see — the same safety net the files above have.
                  A task resumed after a restart carried msgId -1, the "unknown" sentinel, so it matched no
                  message and its progress rendered nowhere: it uploaded photos and drove a whole listing while
                  the chat sat silent. Work in flight should always be visible somewhere. */}
              <TaskBar
                tasks={working.filter((w) => w.msgId === undefined || !messages.some((m) => m.id === w.msgId))}
                onCancel={cancelRunningTask}
              />
              {/* The gap between sending and the server picking it up. It can be a moment — the turn lock is
                  held while another turn finishes — and silence there reads as a message that went nowhere. */}
              {awaitingEcho && (
                <div className="flex items-center justify-end gap-2 pr-1 text-xs text-ink-mute">
                  <span
                    className="h-3 w-3 animate-spin rounded-full border-[1.5px] border-ink-mute border-t-transparent"
                    aria-hidden
                  />
                  <span>Sending…</span>
                </div>
              )}
              {questions.map((q) => (
                <QuestionCard key={q.taskId} q={q} onAnswer={answerQuestion} onDismiss={dropQuestion} />
              ))}
            </div>
          </div>
        )}
      </main>

      {browsing && (
        <RemoteBrowser
          onClose={() => setBrowsing(false)}
          initialUrl={questions.map((q) => q.question.match(/https?:\/\/[^\s)"']+/)?.[0]).find(Boolean)}
        />
      )}

      {/* One state, two viewers: a generated page is presented in its own frame, a document is read. */}
      {presenting &&
        (isReadable(presenting) ? (
          <DocViewer
            src={`/api/session/${sessionId.current}/files/${encodeURIComponent(presenting)}`}
            title={presenting}
            onClose={() => setPresenting(null)}
          />
        ) : (
          <Presenter
            src={`/api/session/${sessionId.current}/files/${encodeURIComponent(presenting)}`}
            title={presenting}
            onClose={() => setPresenting(null)}
          />
        ))}

      <footer className="border-t border-line bg-bg/80 px-4 py-3 backdrop-blur">
        <input
          ref={fileRef}
          type="file"
          multiple
          className="hidden"
          onChange={(e) => {
            addFiles(Array.from(e.target.files ?? []))
            e.target.value = '' // so picking the same file again still fires a change
          }}
        />

        {(attached.length > 0 || attachError) && (
          <div className="mx-auto mb-2 max-w-reading">
            {attached.length > 0 && (
              <div className="flex flex-wrap gap-1.5">
                {attached.map((f) => (
                  <span
                    key={f.name + f.size}
                    className="flex max-w-full items-center gap-1.5 rounded-lg border border-line bg-surface py-1 pl-2 pr-1 text-xs text-ink-soft"
                  >
                    <PaperclipIcon />
                    <span className="min-w-0 truncate" title={f.name}>
                      {f.name}
                    </span>
                    <span className="shrink-0 text-ink-mute">{fileSize(f.size)}</span>
                    <button
                      onClick={() => setAttached((prev) => prev.filter((p) => p !== f))}
                      title="Remove"
                      disabled={uploading}
                      className="grid h-5 w-5 shrink-0 place-items-center rounded text-ink-mute transition hover:bg-surface-mid hover:text-ink disabled:opacity-30"
                    >
                      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
                        <line x1="6" y1="6" x2="18" y2="18" />
                        <line x1="18" y1="6" x2="6" y2="18" />
                      </svg>
                    </button>
                  </span>
                ))}
              </div>
            )}
            {attachError && <p className="mt-1.5 text-xs text-danger">{attachError}</p>}
          </div>
        )}

        <div className="mx-auto flex max-w-reading items-end gap-2">
          {transcribing ? (
            // The gap between stopping and the message appearing. It used to be blank, which read as nothing
            // happening — and what the partials already heard stays on screen while the final pass finishes.
            <div className="flex flex-1 items-center gap-3 rounded-xl border border-line bg-surface px-3 py-2.5">
              <span
                className="h-3.5 w-3.5 shrink-0 animate-spin rounded-full border-[1.5px] border-accent border-t-transparent"
                aria-hidden
              />
              <span className="min-w-0 flex-1 truncate text-sm text-ink-soft">
                {input.trim() || 'Finishing the transcription…'}
              </span>
            </div>
          ) : recording ? (
            <div className="flex flex-1 items-center gap-3 rounded-xl border border-danger/30 bg-danger/[0.04] px-3 py-2.5">
              <span className="h-2.5 w-2.5 shrink-0 animate-pulse rounded-full bg-danger" />
              <span className="shrink-0 font-mono text-xs text-danger">{formatDuration(recSeconds)}</span>
              <div className="min-w-0 flex-1 overflow-hidden">
                {/* The words so far, once there are any — the waveform proves it heard you, this proves it
                    understood you, and it means the text is ready before you stop talking. */}
                {input.trim() ? (
                  <div className="truncate text-sm text-ink" title={input}>
                    {input}
                  </div>
                ) : (
                  <Waveform peaks={recPeaks} progress={1} idleClass="bg-danger/40" activeClass="bg-danger/60" />
                )}
              </div>
              <button onClick={cancelRecording} title="Cancel" className="grid h-9 w-9 shrink-0 place-items-center rounded-full text-ink-soft hover:bg-surface-mid">
                <XIcon />
              </button>
              <button onClick={stopRecording} title="Send voice note" className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-accent text-on-accent hover:brightness-110">
                <ArrowUp />
              </button>
            </div>
          ) : (
            <>
              <div className="flex flex-1 items-end gap-2 rounded-xl border border-line bg-surface px-2.5 py-2 shadow-card transition focus-within:border-accent">
                <button onClick={() => fileRef.current?.click()} title="Attach a file" className="mb-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-full text-ink-mute transition hover:bg-surface-mid hover:text-ink">
                  <PaperclipIcon />
                </button>
                <button onClick={startRecording} title="Record a voice note" className="mb-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-full text-ink-mute transition hover:bg-surface-mid hover:text-ink">
                  <MicIcon />
                </button>
                <textarea
                  ref={taRef}
                  value={input}
                  onChange={(e) => setInput(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' && !e.shiftKey) {
                      e.preventDefault()
                      send()
                    }
                  }}
                  rows={1}
                  placeholder={`Message ${me}…`}
                  className="max-h-52 flex-1 resize-none bg-transparent py-1 text-[1rem] leading-relaxed text-ink outline-none placeholder:text-ink-mute"
                />
              </div>
              <button
                onClick={() => send()}
                disabled={(!input.trim() && attached.length === 0) || uploading}
                title={uploading ? 'Uploading…' : 'Send'}
                className="grid h-11 w-11 shrink-0 place-items-center rounded-full bg-accent text-on-accent shadow-ambient transition enabled:hover:brightness-110 disabled:opacity-30"
              >
                {uploading ? (
                  <span className="h-4 w-4 animate-spin rounded-full border-2 border-on-accent border-t-transparent" aria-hidden />
                ) : (
                  <ArrowUp />
                )}
              </button>
            </>
          )}
        </div>
      </footer>

      <ProjectsDrawer
        me={me}
        open={drawerOpen}
        projects={projects}
        chats={chats}
        currentChatId={sessionId.current}
        onClose={() => setDrawerOpen(false)}
        onPick={openProject}
        onPickChat={openChat}
        onDeleteChat={removeChat}
        onDeleteProject={askToRemoveProject}
        onNewChat={newChat}
      />

      {/* What goes with it, read back before anything is removed. The facts are listed rather than counted because a
          number cannot tell you whether you mind losing them. */}
      {removing && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-ink/40 p-4" onClick={() => setRemoving(null)}>
          <div className="w-full max-w-md rounded-xl border border-line bg-surface p-5 shadow-ambient" onClick={(e) => e.stopPropagation()}>
            <div className="text-sm font-semibold text-ink">
              Delete {removing.sort} “{removing.title}”?
            </div>

            <div className="mt-3 space-y-1.5 text-xs text-ink-soft">
              {removing.lists > 0 && <div>{removing.lists} list{removing.lists === 1 ? '' : 's'} kept against it</div>}
              {removing.runs > 0 && <div>{removing.runs} run{removing.runs === 1 ? '' : 's'} of work</div>}
              {removing.files > 0 && <div>{removing.files} file{removing.files === 1 ? '' : 's'}</div>}
              {removing.facts.length > 0 && (
                <div>
                  <div className="text-ink">
                    {removing.facts.length} thing{removing.facts.length === 1 ? '' : 's'} the brain knows about it:
                  </div>
                  <ul className="mt-1 space-y-0.5">
                    {removing.facts.slice(0, 6).map((f) => (
                      <li key={f} className="truncate text-ink-mute">{f}</li>
                    ))}
                    {removing.facts.length > 6 && (
                      <li className="text-ink-mute">and {removing.facts.length - 6} more</li>
                    )}
                  </ul>
                </div>
              )}
              {removing.facts.length === 0 && !removing.node && (
                <div>Nothing in the brain is filed under this name.</div>
              )}
              {removing.lists === 0 && removing.runs === 0 && removing.facts.length === 0 && removing.files === 0 && removing.node && (
                <div>Nothing else is kept against it.</div>
              )}
              {removing.panels.length > 0 && (
                <div className="text-ink">
                  {removing.panels.length === 1 ? 'A panel is' : `${removing.panels.length} panels are`} built on it:{' '}
                  {removing.panels.join(', ')} — {removing.panels.length === 1 ? 'it' : 'they'} will have nothing to show.
                </div>
              )}
              <div className="pt-1 text-ink-mute">Your chats are kept either way. This cannot be undone.</div>
            </div>

            <div className="mt-4 flex gap-2">
              <button
                disabled={removingBusy}
                onClick={() => void confirmRemoveProject()}
                className="rounded-md bg-danger px-3 py-1.5 text-xs font-medium text-white disabled:opacity-50"
              >
                {removingBusy ? 'Deleting…' : 'Delete it'}
              </button>
              <button
                onClick={() => setRemoving(null)}
                className="rounded-md border border-line px-3 py-1.5 text-xs text-ink-soft hover:text-ink"
              >
                Keep it
              </button>
            </div>
          </div>
        </div>
      )}

      {(projectLoading || activeProject) && (
        <ProjectOverview project={activeProject} loading={projectLoading} onClose={() => closeProject()} mainSessionId={sessionId.current} />
      )}

      {tasksOpen && <TaskRunner tasks={working} onClose={() => setTasksOpen(false)} onCancel={cancelRunningTask} />}

      {dragging && (
        <div className="pointer-events-none fixed inset-0 z-50 grid place-items-center bg-bg/70 backdrop-blur-sm">
          <div className="rounded-2xl border-2 border-dashed border-accent bg-surface px-8 py-6 text-center shadow-ambient">
            <p className="text-[1rem] font-medium text-ink">Drop to attach</p>
            <p className="mt-0.5 text-xs text-ink-mute">Goes with your next message</p>
          </div>
        </div>
      )}
    </div>
  )
}

// ---- Home: a calm "here's what's going on", with the quick chat box living in the shared footer ----
function HomeView({
  greeting,
  working,
  questions,
  schedules,
  widgets,
  onOpenTasks,
  onPick,
  onAnswer,
  onDismiss,
  onAddSchedule,
  onPauseSchedule,
  onCancelSchedule,
  onOpenChat,
  onWidgetsChanged,
  elsewhere,
  onElsewhereAnswered,
  nudges,
}: {
  greeting: string
  working: Working[]
  questions: WorkerQuestion[]
  schedules: Schedule[]
  widgets: Widget[]
  onOpenTasks: () => void
  onPick: (prompt: string) => void
  onAnswer: (taskId: string, text: string) => void
  onDismiss: (taskId: string) => void
  onAddSchedule: (task: string, repeat: string) => Promise<string | null>
  onPauseSchedule: (id: string, paused: boolean) => void
  onCancelSchedule: (id: string) => void
  onOpenChat: (sessionId: string) => void
  onWidgetsChanged: () => void
  elsewhere: OutstandingQuestion[]
  onElsewhereAnswered: () => void
  nudges: Nudge[]
}) {
  // What Proact has been up to. Polled here rather than threaded down from the top, because this is the only place
  // that renders it and the alternative is five more props for one section of one page.
  const { proact, reloadProact } = useProact()

  // Only what's genuinely still going belongs under "Running now" — the array also holds finished tasks, which is
  // what put completed work on this page under a moving progress bar.
  const running = working.filter((t) => !outcomeOf(t.status))
  const nothing =
    running.length === 0 &&
    questions.length === 0 &&
    elsewhere.length === 0 &&
    schedules.length === 0 &&
    nudges.length === 0 &&
    widgets.length === 0 &&
    // A decision waiting on them, or a roundup they have not read, is not an empty page.
    (proact?.waiting ?? 0) === 0 &&
    !(proact?.roundups ?? []).some((r) => !r.seen)
  return (
    // Wider than a reading column on a big screen, because the bento is the one thing here that WANTS the width: the
    // grid gains columns as the viewport grows, and capping it at 680px meant eight columns of 80px on a 1900px
    // display. The text above it stays in a readable measure.
    <div className="mx-auto max-w-reading px-4 py-10 sm:py-14 md:max-w-3xl xl:max-w-6xl">
      <h1 className="animate-fade-in-up text-[1.625rem] font-semibold tracking-tight text-ink sm:text-[2rem]">{greeting}</h1>
      <p className="animate-fade-in-up text-[1rem] text-ink-soft" style={{ animationDelay: '40ms' }}>
        {nothing ? 'What can I help you with?' : "Here's what's going on."}
      </p>

      {/* Proact, first, because the roundup and any decision it is waiting on are the two things that happened
          while nobody was looking. Everything below this line is something they set in motion themselves. */}
      {proact && <RoundupCard state={proact} onRead={reloadProact} />}
      {proact && <WaitingOnYou state={proact} onAnswered={reloadProact} />}

      {nudges.length > 0 && (
        <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '50ms' }}>
          {/* First on the page, above even the questions. Everything else here is something they set in motion; this
              is the one thing that happened while they were not looking. */}
          <SectionLabel>Started for you</SectionLabel>
          <div className="mt-2.5 space-y-2.5">
            {nudges.map((n) => (
              <button
                key={n.session}
                onClick={() => onOpenChat(n.session)}
                className="flex w-full items-start gap-3 rounded-lg border border-accent bg-accent-soft px-4 py-3 text-left transition hover:bg-surface-low"
              >
                <span className="mt-0.5 h-2 w-2 shrink-0 rounded-full bg-accent" aria-hidden />
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-sm font-medium text-ink">{n.title || n.watcher}</span>
                  <span className="block truncate text-[0.6875rem] text-ink-soft">{n.watcher}</span>
                </span>
                <span className="shrink-0 text-[0.6875rem] text-ink-mute">{agoText(n.at)}</span>
              </button>
            ))}
          </div>
        </section>
      )}

      {(questions.length > 0 || elsewhere.length > 0) && (
        <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '60ms' }}>
          <SectionLabel>Awaiting your answer</SectionLabel>
          <div className="mt-2.5 space-y-2.5">
            {questions.map((q) => (
              <QuestionCard key={q.taskId} q={q} onAnswer={onAnswer} onDismiss={onDismiss} />
            ))}
            {/* And the ones asked somewhere else. Same section, because from the user's side there is no difference
                between a question from the chat they have open and one from a job they started yesterday — both are
                the system waiting on them. */}
            {elsewhere.map((q) => (
              <ElsewhereCard
                key={`${q.session}#${q.taskId}`}
                q={q}
                onAnswered={onElsewhereAnswered}
                onOpen={() => onOpenChat(q.session)}
              />
            ))}
          </div>
        </section>
      )}

      {/* What it got on with, below anything the user themselves set going. Quiet on purpose. */}
      {proact && <ProactDid state={proact} onOpenChat={onOpenChat} onVoted={reloadProact} />}

      {running.length > 0 && (
        <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '80ms' }}>
          <SectionLabel>Running now</SectionLabel>
          <div className="mt-2.5 space-y-2">
            {running.map((t) => (
              <button
                key={t.id}
                onClick={onOpenTasks}
                className="block w-full overflow-hidden rounded-lg border border-line bg-surface text-left shadow-card transition hover:bg-surface-low"
              >
                <div className="progress-line h-0.5 w-full" />
                <div className="flex items-center gap-3 px-4 py-3">
                  <span className="min-w-0 flex-1 truncate text-sm text-ink">{t.task}</span>
                  <span className="shrink-0 font-mono text-xs text-ink-mute">{durationOf(t)}</span>
                </div>
              </button>
            ))}
          </div>
        </section>
      )}

      {/* The page proper. Panels the system decided on, in the order it thinks matters — this is where a meal
          plan, a tracked flight and a shopping list all end up, rather than each needing a section of its own
          written into this file. Questions and running work stay above as their own thing: they're interactive
          and time-critical, and burying "needs your answer" in a grid is how it gets missed. */}
      {widgets.length > 0 && (
        <div className="animate-fade-in-up" style={{ animationDelay: '100ms' }}>
          <BentoGrid widgets={widgets} onChanged={onWidgetsChanged} />
        </div>
      )}

      <ScheduleSection
        schedules={schedules}
        onAdd={onAddSchedule}
        onPause={onPauseSchedule}
        onCancel={onCancelSchedule}
        onOpenChat={onOpenChat}
      />

      {nothing && (
        <div className="mt-7 flex flex-wrap gap-2">
          {EXAMPLES.map((e) => (
            <button
              key={e}
              onClick={() => onPick(e)}
              className="rounded-full border border-line bg-surface px-3.5 py-1.5 text-xs text-ink-soft transition hover:border-accent/40 hover:text-accent"
            >
              {e}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

function SectionLabel({ children }: { children: ReactNode }) {
  return <div className="text-[0.75rem] font-semibold uppercase tracking-wider text-ink-mute">{children}</div>
}

/**
 * Standing work, on the home page next to projects and chats.
 *
 * Three things about a scheduled task are worth seeing without opening anything: what it does, when it next goes
 * off, and what it said last time. The last one matters most for a recurring job — its answers land in the
 * conversation it belongs to, which is where they should be READ (that thread is what gives the next run its
 * context) and a terrible place to check whether the thing has been working for six weeks.
 */
function ScheduleSection({
  schedules,
  onAdd,
  onPause,
  onCancel,
  onOpenChat,
}: {
  schedules: Schedule[]
  onAdd: (task: string, repeat: string) => Promise<string | null>
  onPause: (id: string, paused: boolean) => void
  onCancel: (id: string) => void
  onOpenChat: (sessionId: string) => void
}) {
  const [adding, setAdding] = useState(false)
  const [task, setTask] = useState('')
  const [repeat, setRepeat] = useState('daily at 08:00')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Finished one-offs aren't standing work — they're history, and they'd bury the two things that still run.
  const live = schedules.filter((s) => s.status === 'pending' || s.status === 'paused' || s.status === 'firing')

  async function submit() {
    if (!task.trim() || busy) return
    setBusy(true)
    const err = await onAdd(task.trim(), repeat.trim())
    setBusy(false)
    if (err) {
      setError(err)
      return
    }
    setTask('')
    setError(null)
    setAdding(false)
  }

  return (
    <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '100ms' }}>
      <div className="flex items-center justify-between">
        <SectionLabel>Scheduled</SectionLabel>
        <button
          onClick={() => setAdding((v) => !v)}
          className="text-xs text-ink-mute transition hover:text-accent"
        >
          {adding ? 'Cancel' : '+ New'}
        </button>
      </div>

      {adding && (
        <div className="mt-2.5 space-y-2 rounded-lg border border-line bg-surface p-3 shadow-card">
          <textarea
            value={task}
            onChange={(e) => setTask(e.target.value)}
            placeholder="What should I do? e.g. check the overnight orders and tell me anything odd"
            rows={2}
            className="w-full resize-none rounded-md border border-line bg-surface-low px-3 py-2 text-sm text-ink placeholder:text-ink-mute focus:border-accent/50 focus:outline-none"
          />
          <div className="flex items-center gap-2">
            <input
              value={repeat}
              onChange={(e) => setRepeat(e.target.value)}
              placeholder="daily at 08:00"
              className="min-w-0 flex-1 rounded-md border border-line bg-surface-low px-3 py-1.5 font-mono text-xs text-ink placeholder:text-ink-mute focus:border-accent/50 focus:outline-none"
            />
            <button
              onClick={submit}
              disabled={!task.trim() || busy}
              className="shrink-0 rounded-md bg-accent px-3 py-1.5 text-xs font-medium text-white transition disabled:opacity-40"
            >
              {busy ? 'Saving…' : 'Schedule'}
            </button>
          </div>
          {error ? (
            <div className="text-xs text-danger">{error}</div>
          ) : (
            <div className="text-xs text-ink-mute">
              daily at 08:00 · every weekday at 07:30 · weekly on monday at 09:00 · every 2 hours · hourly
            </div>
          )}
        </div>
      )}

      {live.length > 0 && (
        <div className="mt-2.5 space-y-2">
          {live.map((s) => (
            <ScheduleRow
              key={s.id}
              s={s}
              onPause={onPause}
              onCancel={onCancel}
              onOpenChat={onOpenChat}
            />
          ))}
        </div>
      )}
    </section>
  )
}

function ScheduleRow({
  s,
  onPause,
  onCancel,
  onOpenChat,
}: {
  s: Schedule
  onPause: (id: string, paused: boolean) => void
  onCancel: (id: string) => void
  onOpenChat: (sessionId: string) => void
}) {
  const [open, setOpen] = useState(false)
  const when = new Date(s.nextAt)

  return (
    <div className="overflow-hidden rounded-lg border border-line bg-surface shadow-card">
      <button onClick={() => setOpen((v) => !v)} className="block w-full px-4 py-3 text-left transition hover:bg-surface-low">
        <div className="flex items-center gap-2">
          <span
            className={`h-1.5 w-1.5 shrink-0 rounded-full ${
              s.status === 'paused' ? 'bg-ink-mute' : s.recurring ? 'bg-accent' : 'bg-accent/50'
            }`}
          />
          <span className="min-w-0 flex-1 truncate text-sm text-ink">{s.task}</span>
          <span className="shrink-0 font-mono text-xs text-ink-mute">
            {s.status === 'paused' ? 'paused' : untilText(s.dueInSeconds)}
          </span>
        </div>
        <div className="mt-0.5 flex flex-wrap items-center gap-x-2 pl-3.5 text-xs text-ink-mute">
          <span>{s.recurring ? s.repeat : 'once'}</span>
          <span aria-hidden>·</span>
          <span>{when.toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}</span>
          {s.runs > 0 && (
            <>
              <span aria-hidden>·</span>
              <span>
                ran {s.runs}
                {s.runs === 1 ? ' time' : ' times'}
              </span>
            </>
          )}
        </div>
      </button>

      {open && (
        <div className="border-t border-line bg-surface-low px-4 py-3">
          {s.lastResult && (
            <div className="mb-3">
              <div className="text-[0.7rem] font-semibold uppercase tracking-wider text-ink-mute">Last run</div>
              <div className="mt-1 whitespace-pre-wrap text-sm text-ink-soft">{s.lastResult}</div>
            </div>
          )}
          <div className="flex flex-wrap items-center gap-2">
            {s.title !== null && (
              <button
                onClick={() => onOpenChat(s.session)}
                className="rounded-md border border-line px-2.5 py-1 text-xs text-ink-soft transition hover:border-accent/40 hover:text-accent"
              >
                Open {s.title.length > 32 ? s.title.slice(0, 32) + '…' : s.title}
              </button>
            )}
            {s.recurring && (
              <button
                onClick={() => onPause(s.id, s.status !== 'paused')}
                className="rounded-md border border-line px-2.5 py-1 text-xs text-ink-soft transition hover:border-accent/40 hover:text-accent"
              >
                {s.status === 'paused' ? 'Resume' : 'Pause'}
              </button>
            )}
            <button
              onClick={() => onCancel(s.id)}
              className="rounded-md border border-line px-2.5 py-1 text-xs text-ink-mute transition hover:border-danger/40 hover:text-danger"
            >
              Delete
            </button>
          </div>
        </div>
      )}
    </div>
  )
}

function RunningPill({ count, onOpen }: { count: number; onOpen: () => void }) {
  return (
    <button
      onClick={onOpen}
      className="inline-flex items-center gap-1.5 rounded-full bg-accent-soft px-2.5 py-1 text-xs font-medium text-accent transition hover:brightness-95"
      aria-label={`${count} running ${count === 1 ? 'task' : 'tasks'}`}
    >
      <span className="h-1.5 w-1.5 rounded-full bg-accent blink" />
      {count} running
    </button>
  )
}

// ---- Projects drawer ----
function ProjectsDrawer({
  open,
  projects,
  chats,
  currentChatId,
  onClose,
  onPick,
  onPickChat,
  onDeleteChat,
  onDeleteProject,
  onNewChat,
  me,
}: {
  open: boolean
  projects: ProjectSummary[]
  chats: ChatSummary[]
  currentChatId: string
  onClose: () => void
  onPick: (slug: string) => void
  onPickChat: (id: string) => void
  onDeleteChat: (id: string) => void
  onDeleteProject: (slug: string) => void
  onNewChat: () => void
  me: string
}) {
  return (
    <>
      <div
        className={`fixed inset-0 z-30 bg-ink/20 transition-opacity ${open ? 'opacity-100' : 'pointer-events-none opacity-0'}`}
        onClick={onClose}
      />
      <aside
        className={`fixed left-0 top-0 z-40 flex h-full w-80 max-w-[85vw] flex-col border-r border-line bg-surface shadow-ambient transition-transform duration-200 ${
          // A shut drawer is only translated off-screen, so without pointer-events-none its rows stay clickable
          // and keep their place in the tab order — you can tab into a chat list that isn't on screen.
          open ? 'translate-x-0' : 'pointer-events-none -translate-x-full'
        }`}
        aria-hidden={!open}
      >
        <div className="flex items-center justify-between border-b border-line px-4 py-3.5">
          <div className="text-sm font-semibold text-ink">{me}</div>
          <div className="flex items-center gap-1">
            <button onClick={onNewChat} title="New chat" className="grid h-8 w-8 place-items-center rounded-md text-ink-soft hover:bg-surface-mid hover:text-ink">
              <PlusIcon />
            </button>
            <button onClick={onClose} className="grid h-8 w-8 place-items-center rounded-md text-ink-soft hover:bg-surface-mid hover:text-ink">
              <XIcon />
            </button>
          </div>
        </div>

        {/*
          Projects sit at the top and take only the room they need; chats take everything left and scroll on
          their own. Chats are the list that grows without limit, so giving IT the scrollbar keeps the handful
          of things you're actually working on permanently in view — under the old order they were pushed below
          a hundred conversations and you had to go looking for them.
        */}
        <div className="flex min-h-0 flex-1 flex-col p-2">
          {/*
            Two headings, because they are two things. A project is being driven at an outcome and its goal is the
            useful line under it; a topic is only somewhere things are kept. Listed together, a favourite restaurant
            sat among the work in progress and read as something outstanding.
          */}
          <div className="shrink-0">
            {projects.length === 0 && (
              <>
                <SectionLabel>Projects</SectionLabel>
                <p className="mb-4 mt-1.5 px-3 py-4 text-xs text-ink-mute">Nothing on the go yet.</p>
              </>
            )}
            {(['project', 'topic'] as const).map((sort) => {
              const held = projects.filter((p) => p.sort === sort)
              if (held.length === 0) return null
              return (
                <div key={sort}>
                  <SectionLabel>{sort === 'project' ? 'Projects' : 'Topics'}</SectionLabel>
                  <div className="mb-4 mt-1.5">
                    {held.map((p) => (
                      /* The delete sits on the row rather than inside the project, because wanting rid of one is
                         usually a reaction to seeing it in this list. */
                      <div key={p.slug} className="group relative mb-0.5">
                        <button onClick={() => onPick(p.slug)} className="w-full rounded-md px-3 py-2.5 pr-9 text-left transition hover:bg-surface-low">
                          <div className="truncate text-sm font-medium text-ink">{p.title}</div>
                          {p.goal && <div className="mt-0.5 line-clamp-2 text-xs text-ink-soft">Done when {p.goal}</div>}
                          {!p.goal && p.description && <div className="mt-0.5 line-clamp-2 text-xs text-ink-soft">{p.description}</div>}
                          {p.window && <div className="mt-1 text-xs text-ink-mute">{p.window}</div>}
                        </button>
                        <button
                          onClick={() => onDeleteProject(p.slug)}
                          title={`Delete this ${p.sort}`}
                          className="absolute right-1.5 top-1.5 rounded p-1 text-ink-mute opacity-0 transition hover:bg-surface hover:text-danger focus:opacity-100 group-hover:opacity-100"
                        >
                          ✕
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
              )
            })}
          </div>

          {/* min-h-0 is what lets a flex child actually shrink, and so what makes the scroll land here. */}
          <div className="flex min-h-0 flex-1 flex-col">
            <SectionLabel>Chats</SectionLabel>
            <div className="mt-1.5 min-h-0 flex-1 overflow-y-auto">
              {chats.length === 0 ? (
                <p className="px-3 py-4 text-xs text-ink-mute">Conversations you've had will be kept here.</p>
              ) : (
                chats.map((c) => (
                  <ChatRow
                    key={c.id}
                    chat={c}
                    active={c.id === currentChatId}
                    onOpen={() => onPickChat(c.id)}
                    onOpenProject={onPick}
                    onDelete={() => onDeleteChat(c.id)}
                  />
                ))
              )}
            </div>
          </div>
        </div>
      </aside>
    </>
  )
}

/**
 * One conversation in the sidebar, with a pill per project its work touched — which is what makes the list
 * scannable: you look for the chat about the thing, and the thing is a project name, not a paraphrased title.
 */
function ChatRow({
  chat,
  active,
  onOpen,
  onOpenProject,
  onDelete,
}: {
  chat: ChatSummary
  active: boolean
  onOpen: () => void
  onOpenProject: (slug: string) => void
  onDelete: () => void
}) {
  const [confirming, setConfirming] = useState(false)

  return (
    <div
      className={`group relative mb-0.5 rounded-md transition ${active ? 'bg-surface-low' : 'hover:bg-surface-low'}`}
    >
      <button onClick={onOpen} className="w-full px-3 py-2.5 pr-9 text-left">
        <div className="flex items-baseline gap-2">
          <span className={`min-w-0 flex-1 truncate text-sm ${active ? 'font-medium text-ink' : 'text-ink'}`}>
            {chat.title}
          </span>
          <span className="shrink-0 text-[0.75rem] text-ink-mute">{shortWhen(chat.lastActivityAt)}</span>
        </div>
      </button>

      {chat.projects.length > 0 && (
        <div className="flex flex-wrap gap-1 px-3 pb-2">
          {chat.projects.map((p) => (
            // A pill is a link to the project, not part of the chat button — clicking the badge should take you
            // to the thing it names.
            <button
              key={p.slug}
              onClick={(e) => {
                e.stopPropagation()
                onOpenProject(p.slug)
              }}
              title={`Open ${p.title}`}
              className="max-w-full truncate rounded-full bg-accent/10 px-2 py-0.5 text-[0.75rem] font-medium text-accent transition hover:bg-accent/20"
            >
              {p.title}
            </button>
          ))}
        </div>
      )}

      {/* Kept at half opacity rather than hover-only: on a touch screen there is no hover, so a hidden control
          is an unreachable one. */}
      <button
        onClick={() => (confirming ? onDelete() : setConfirming(true))}
        onBlur={() => setConfirming(false)}
        title={confirming ? 'Click again to delete' : 'Delete chat'}
        className={`absolute right-1.5 top-2 grid h-6 w-6 place-items-center rounded transition ${
          confirming ? 'bg-danger/15 text-danger' : 'text-ink-mute opacity-50 hover:bg-surface-mid hover:text-ink hover:opacity-100'
        }`}
      >
        {confirming ? <CheckIcon /> : <TrashIcon />}
      </button>
    </div>
  )
}

/** Compact "when" for a dense list: today as a time, this week as a day, older as a date. */
function shortWhen(iso: string): string {
  const then = new Date(iso)
  if (Number.isNaN(then.getTime())) return ''
  const days = (Date.now() - then.getTime()) / 86400000
  if (days < 1) return then.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
  if (days < 7) return then.toLocaleDateString(undefined, { weekday: 'short' })
  return then.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })
}

// ---- contextual project header ----
// Stable 32-bit hash of a string (FNV-1a) — drives both the fallback gradient's hue and the image "lock"
// seed, so a given project always gets the same banner.
function hashNum(s: string): number {
  let h = 2166136261
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return h >>> 0
}

// Map a project to a GENERIC category keyword for the image search. Deliberately coarse: only a category
// word leaves the browser, never the project's title or any of its details.
function bannerKeyword(p: ProjectDetail): string {
  const t = `${p.title} ${p.description}`.toLowerCase()
  if (/pub|bar|drink|crawl|beer|cocktail|brewery/.test(t)) return 'pub,bar'
  if (/wedding|marriage|bride|groom/.test(t)) return 'wedding'
  if (/party|birthday|celebrat|anniversary|festiv/.test(t)) return 'celebration,party'
  if (/trip|holiday|travel|flight|vacation|tour|getaway|abroad/.test(t)) return 'travel,landscape'
  if (/mov(e|ing)|house|home|flat|apartment|relocat/.test(t)) return 'house,home'
  if (/garden|plant|allotment/.test(t)) return 'garden,plants'
  if (/christmas|xmas/.test(t)) return 'christmas'
  if (/food|dinner|restaurant|meal|cook|menu|cater/.test(t)) return 'food,restaurant'
  if (/work|business|launch|startup|office|client/.test(t)) return 'office,city'
  if (/concert|gig|music|festival|band/.test(t)) return 'concert,music'
  return 'abstract,texture'
}

function ProjectBanner({ project }: { project: ProjectDetail }) {
  const [imgFailed, setImgFailed] = useState(false)
  const seed = hashNum(project.slug)
  const hue = seed % 360
  const keyword = bannerKeyword(project)
  // loremflickr: keyword-matched Creative-Commons photos, no API key. `lock` pins one image per project.
  const src = `https://loremflickr.com/1200/400/${encodeURIComponent(keyword)}?lock=${seed}`
  const gradient = `linear-gradient(135deg, hsl(${hue} 58% 44%), hsl(${(hue + 38) % 360} 62% 30%))`
  return (
    <div className="relative h-40 w-full overflow-hidden rounded-xl shadow-card animate-fade-in sm:h-48" style={{ background: gradient }}>
      {!imgFailed && (
        <img
          src={src}
          alt=""
          onError={() => setImgFailed(true)}
          loading="lazy"
          className="absolute inset-0 h-full w-full object-cover"
        />
      )}
      {/* legibility scrim under the title */}
      <div className="absolute inset-0 bg-gradient-to-t from-black/70 via-black/20 to-transparent" />
      <div className="absolute inset-x-0 bottom-0 p-4 sm:p-5">
        <h1 className="text-[1.5rem] font-semibold tracking-tight text-white drop-shadow-sm sm:text-[1.75rem]">{project.title}</h1>
        {project.description && <p className="mt-0.5 line-clamp-1 text-sm text-white/85">{project.description}</p>}
        {/* What it covers, and whether that has passed — the same fact the model is given, said the same way. */}
        {project.window && (
          <p
            className={`mt-1 text-xs font-medium drop-shadow-sm ${project.ended ? 'text-amber-200' : 'text-white/75'}`}
          >
            {project.ended ? '⌛ ' : '📅 '}
            {project.window}
          </p>
        )}
      </div>
    </div>
  )
}

// ---- Project page: overview + a dedicated, scoped chat about this project ----
function ProjectOverview({
  project,
  loading,
  onClose,
  mainSessionId,
}: {
  project: ProjectDetail | null
  loading: boolean
  onClose: () => void
  mainSessionId: string
}) {
  const [tab, setTab] = useState<'overview' | 'chat'>('overview')
  const [showAllRuns, setShowAllRuns] = useState(false)
  // Forgotten locally the moment it goes, rather than waiting for a refetch: the list is short and the click
  // should feel immediate. The server is the truth; this only hides what it has already agreed to drop.
  const [forgotten, setForgotten] = useState<string[]>([])

  const forget = async (id: string) => {
    if (await forgetMemory(id)) setForgotten((f) => [...f, id])
  }

  // Held locally so an edit shows at once; seeded from the project whenever it reloads.
  const [lists, setLists] = useState<ProjectList[]>([])
  // A project file carries its own full URL — it lives under whichever conversation produced it — so the presenter
  // is opened by address here rather than by name as it is in the chat.
  const [presentingUrl, setPresentingUrl] = useState<string | null>(null)
  const [readingDoc, setReadingDoc] = useState<{ url: string; name: string } | null>(null)
  // Removed here and now, rather than waiting for the project to reload — the click should feel done.
  const [removedFiles, setRemovedFiles] = useState<string[]>([])
  const [newList, setNewList] = useState('')

  useEffect(() => {
    setLists(project?.lists ?? [])
    setForgotten([])
  }, [project?.slug, project?.lists])

  const startList = async () => {
    const title = newList.trim()
    if (!title || !project?.slug) return
    setNewList('')
    const created = await createList(project.slug, title)
    if (created) setLists((prev) => [...prev, created])
  }
  const [messages, setMessages] = useState<UiMessage[]>([])
  const [questions, setQuestions] = useState<WorkerQuestion[]>([])
  const [input, setInput] = useState('')
  const runs = project?.runs ?? []
  const files = (project?.files ?? []).filter((f) => !removedFiles.includes(f.name))
  const facts = orderedFacts((project?.memories ?? []).filter((m) => !m.id || !forgotten.includes(m.id)))
  const visibleRuns = showAllRuns ? runs : runs.slice(0, 3)
  const slug = project?.slug
  const projSession = slug ? `${mainSessionId}__p__${slug}` : null

  const taRef = useRef<HTMLTextAreaElement>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const atBottomRef = useRef(true)

  function upsert(id: number, fn: (m: UiMessage) => UiMessage, role?: 'user' | 'assistant') {
    setMessages((prev) => {
      const idx = prev.findIndex((m) => m.id === id)
      if (idx >= 0) {
        const next = prev.slice()
        next[idx] = fn(next[idx])
        return next
      }
      return [...prev, fn({ id, role: role ?? 'assistant', content: '', reasoning: '', streaming: true })]
    })
  }

  // Pin the (project-specific) session and stream its scoped conversation.
  useEffect(() => {
    if (!projSession || !slug) return
    pinSessionToProject(projSession, slug)
    const controller = new AbortController()
    openSessionStream(
      projSession,
      {
        onMsgStart: (id, role) => upsert(id, (m) => ({ ...m, role: role as 'user' | 'assistant', streaming: true }), role as 'user' | 'assistant'),
        onContent: (id, text) => upsert(id, (m) => ({ ...m, content: m.content + text, thinkMs: m.thinkMs ?? (m.thinkStart ? Date.now() - m.thinkStart : undefined) })),
        onReasoning: (id, text) => upsert(id, (m) => ({ ...m, reasoning: m.reasoning + text, thinkStart: m.thinkStart ?? Date.now() })),
        onMsgEnd: (id, text) =>
          upsert(id, (m) => ({ ...m, streaming: false, content: text && text.length > 0 ? text : m.content, thinkMs: m.thinkMs ?? (m.thinkStart ? Date.now() - m.thinkStart : undefined) })),
        onWorking: (id) => setQuestions((q) => q.filter((x) => x.taskId !== id)),
        onQuestion: (q) => setQuestions((prev) => (prev.some((x) => x.taskId === q.taskId) ? prev : [...prev, q])),
      },
      controller.signal,
    )
    return () => controller.abort()
  }, [projSession, slug])

  // Put a question down rather than answer it: the work moved on, or another task covered the same ground.
  function dropQuestion(taskId: string) {
    if (!projSession) return
    setQuestions((q) => q.filter((x) => x.taskId !== taskId))
    void dismissQuestion(projSession, taskId)
  }

  function answerQuestion(taskId: string, text: string) {
    const body = text.trim()
    if (!body || !projSession) return
    setQuestions((q) => q.filter((x) => x.taskId !== taskId))
    setTab('chat')
    answerTask(projSession, taskId, body).catch(() => {})
  }

  useLayoutEffect(() => {
    const el = scrollRef.current
    if (el && tab === 'chat' && atBottomRef.current) el.scrollTop = el.scrollHeight
  }, [messages, tab])

  useEffect(() => {
    const el = taRef.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = Math.min(Math.max(el.scrollHeight, 24), 160) + 'px'
  }, [input, tab])

  function send() {
    const body = input.trim()
    if (!body || !projSession) return
    setInput('')
    setTab('chat')
    atBottomRef.current = true
    sendMessage(projSession, body)
  }

  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-bg animate-fade-in">
      <header className="flex items-center gap-2 border-b border-line bg-bg/80 px-4 py-2.5 backdrop-blur sm:px-6">
        <button onClick={onClose} title="Back" className="-ml-1 grid h-9 w-9 shrink-0 place-items-center rounded-md text-ink-soft hover:bg-surface-mid hover:text-ink">
          <BackIcon />
        </button>
        <h1 className="min-w-0 flex-1 truncate text-sm font-medium tracking-tight text-ink-soft">{project?.title ?? (loading ? 'Loading…' : 'Project')}</h1>
        {project && (
          <div className="flex shrink-0 rounded-md bg-surface-low p-0.5 text-xs font-medium">
            <button onClick={() => setTab('overview')} className={`rounded px-2.5 py-1 transition ${tab === 'overview' ? 'bg-surface text-ink shadow-card' : 'text-ink-mute hover:text-ink'}`}>
              Overview
            </button>
            <button onClick={() => setTab('chat')} className={`relative rounded px-2.5 py-1 transition ${tab === 'chat' ? 'bg-surface text-ink shadow-card' : 'text-ink-mute hover:text-ink'}`}>
              Chat
              {questions.length > 0 && <span className="absolute right-0.5 top-0.5 h-1.5 w-1.5 rounded-full bg-accent blink" />}
            </button>
          </div>
        )}
      </header>

      <main ref={scrollRef} className="flex-1 overflow-y-auto">
        {loading || !project ? (
          <div className="pt-20 text-center text-sm text-ink-mute">Loading project…</div>
        ) : tab === 'overview' ? (
          <div className="mx-auto max-w-reading space-y-9 px-4 py-8 sm:py-10">
            <div>
              <ProjectBanner project={project} />
              {project.summary && <p className="mt-4 text-[1rem] leading-relaxed text-ink-soft">{project.summary}</p>}
            </div>

            {facts.length === 0 && runs.length === 0 && (
              <p className="text-sm text-ink-mute">Nothing tracked yet — details will show up as things get sorted out.</p>
            )}

            {facts.length > 0 && (
              <section className="space-y-2.5">
                {facts.map((m, i) => (
                  <FactCard key={m.id ?? i} fact={m} onForget={m.id ? () => forget(m.id!) : undefined} />
                ))}
              </section>
            )}

            {files.length > 0 && (
              <section>
                <SectionLabel>Files</SectionLabel>
                {/* A grid of previews rather than a list of filenames: a picture of the thing beats its name, and
                    for a deck a scaled-down live render IS the thumbnail — no screenshot pipeline needed. */}
                <div className="mt-2.5 grid grid-cols-2 gap-2.5 sm:grid-cols-3">
                  {files.map((f) => (
                    <FileTile
                      key={f.url}
                      file={f}
                      onOpen={() =>
                        isDeck(f.name)
                          ? setPresentingUrl(f.url)
                          : isReadable(f.name)
                            ? setReadingDoc({ url: f.url, name: f.name })
                            : window.open(f.url, '_blank', 'noreferrer')
                      }
                      onDelete={async () => {
                        if (!slug) return
                        if (await deleteProjectFile(slug, f.name)) setRemovedFiles((r) => [...r, f.name])
                      }}
                    />
                  ))}
                </div>
              </section>
            )}

            {slug && (
              <section>
                <SectionLabel>Lists</SectionLabel>
                <div className="mt-2.5 space-y-2.5">
                  {lists.map((l) => (
                    <ListCard
                      key={l.id}
                      list={l}
                      onChanged={(u) => setLists((prev) => prev.map((x) => (x.id === u.id ? u : x)))}
                      onDeleted={() => setLists((prev) => prev.filter((x) => x.id !== l.id))}
                    />
                  ))}
                  <div className="flex items-center gap-2">
                    <input
                      value={newList}
                      onChange={(e) => setNewList(e.target.value)}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') {
                          e.preventDefault()
                          void startList()
                        }
                      }}
                      placeholder="Start a list — preferred amenities, must-sees…"
                      className="min-w-0 flex-1 rounded-md border border-line bg-bg px-2.5 py-1.5 text-sm text-ink outline-none placeholder:text-ink-mute focus:border-accent"
                    />
                    <button
                      onClick={() => void startList()}
                      disabled={!newList.trim()}
                      className="shrink-0 rounded-md border border-line px-2.5 py-1.5 text-xs font-medium text-ink-soft transition hover:border-accent hover:text-ink disabled:opacity-40"
                    >
                      New list
                    </button>
                  </div>
                </div>
              </section>
            )}

            {runs.length > 0 && (
              <section>
                <SectionLabel>Latest activity</SectionLabel>
                <div className="mt-2.5 space-y-2">
                  {visibleRuns.map((r) => (
                    <RunCard key={r.id} run={r} />
                  ))}
                </div>
                {runs.length > 3 && (
                  <button onClick={() => setShowAllRuns((v) => !v)} className="mt-2 text-xs font-medium text-accent hover:brightness-90">
                    {showAllRuns ? 'Show less' : `View all ${runs.length}`}
                  </button>
                )}
              </section>
            )}
          </div>
        ) : (
          <div className="mx-auto max-w-reading px-4 py-6">
            {messages.length === 0 && questions.length === 0 ? (
              <div className="pt-16 text-center text-sm text-ink-mute">Ask me anything about {project.title} — I'll keep to this project.</div>
            ) : (
              <div className="space-y-6">
                {messages.map((m) => (
                  <MessageRow key={m.id} message={m} sessionId={projSession ?? ''} />
                ))}
                {questions.map((q) => (
                  <QuestionCard key={q.taskId} q={q} onAnswer={answerQuestion} onDismiss={dropQuestion} />
                ))}
              </div>
            )}
          </div>
        )}
      </main>

      {project && (
        <footer className="border-t border-line bg-bg/80 px-4 py-3 backdrop-blur">
          <div className="mx-auto flex max-w-reading items-end gap-2">
            <div className="flex flex-1 items-end rounded-xl border border-line bg-surface px-3 py-2 shadow-card transition focus-within:border-accent">
              <textarea
                ref={taRef}
                value={input}
                onChange={(e) => setInput(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault()
                    send()
                  }
                }}
                rows={1}
                placeholder={`Message about ${project.title}…`}
                className="max-h-40 flex-1 resize-none bg-transparent py-1 text-[1rem] leading-relaxed text-ink outline-none placeholder:text-ink-mute"
              />
            </div>
            <button
              onClick={send}
              disabled={!input.trim()}
              title="Send"
              className="grid h-11 w-11 shrink-0 place-items-center rounded-full bg-accent text-on-accent shadow-ambient transition enabled:hover:brightness-110 disabled:opacity-30"
            >
              <ArrowUp />
            </button>
          </div>
        </footer>
      )}

      {presentingUrl && (
        <Presenter
          src={presentingUrl}
          title={presentingUrl.split('/').pop() ?? 'Presentation'}
          onClose={() => setPresentingUrl(null)}
        />
      )}

      {readingDoc && (
        <DocViewer src={readingDoc.url} title={readingDoc.name} onClose={() => setReadingDoc(null)} />
      )}
    </div>
  )
}

function RunCard({ run }: { run: ProjectRun }) {
  const [open, setOpen] = useState(false)
  const title = run.title || run.task
  return (
    <div className="overflow-hidden rounded-lg border border-line bg-surface shadow-card">
      <button onClick={() => setOpen((o) => !o)} className="flex w-full items-center gap-2.5 px-3.5 py-2.5 text-left transition hover:bg-surface-low">
        <StatusDot status={run.status} />
        <span className="min-w-0 flex-1 truncate text-sm text-ink">{title}</span>
        <span className="shrink-0 font-mono text-[0.75rem] text-ink-mute">{relTime(run.startedAt)}</span>
        <span className={`shrink-0 text-ink-mute transition-transform ${open ? 'rotate-90' : ''}`}>
          <ChevronIcon />
        </span>
      </button>
      {open && (
        <div className="space-y-2 border-t border-line px-3.5 py-3">
          {run.steps.map((s, i) => (
            <StepRow key={i} step={s} />
          ))}
          {run.result && (
            <div className="mt-2 rounded-md border border-line bg-surface-low px-3 py-2">
              <div className="mb-1 text-[0.75rem] font-semibold uppercase tracking-wider text-ink-mute">Result</div>
              <div className="whitespace-pre-wrap text-xs leading-relaxed text-ink">{run.result}</div>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function relTime(iso: string): string {
  const s = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000))
  if (s < 60) return 'just now'
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ago`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ago`
  return `${Math.floor(h / 24)}d ago`
}

function StepRow({ step }: { step: RunStep }) {
  if (step.kind === 'thinking') {
    return (
      <div className="flex gap-2.5">
        <span className="mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full bg-ink/20" />
        <pre className="min-w-0 flex-1 whitespace-pre-wrap font-sans text-xs leading-relaxed text-ink-mute">{step.text}</pre>
      </div>
    )
  }
  if (step.kind === 'tool') {
    return (
      <div className="flex gap-2.5">
        <span className="mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full bg-accent" />
        <div className="min-w-0 flex-1">
          <div className="font-mono text-xs text-accent">
            {step.tool}
            {step.args && step.args !== '{}' && <span className="text-ink-mute"> {step.args}</span>}
          </div>
          {step.result && (
            <pre className="mt-1 max-h-40 overflow-y-auto whitespace-pre-wrap rounded-md bg-surface-low px-2.5 py-1.5 font-mono text-[0.75rem] leading-relaxed text-ink-soft">
              {step.result}
            </pre>
          )}
        </div>
      </div>
    )
  }
  return (
    <div className="flex gap-2.5">
      <span className="mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full bg-ink/40" />
      <div className="min-w-0 flex-1 whitespace-pre-wrap text-xs leading-relaxed text-ink-soft">{step.text}</div>
    </div>
  )
}

// ---- rich project facts ----
type FactKind = 'location' | 'email' | 'phone' | 'url' | 'date' | 'money' | 'person' | 'plain'

function classifyFact(fact: ProjectMemory): FactKind {
  const v = (fact.value || '').trim()
  const hint = `${fact.type} ${fact.key}`.toLowerCase()
  if (/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(v)) return 'email'
  if (/^https?:\/\//i.test(v)) return 'url'
  if (/^\+\d[\d\s().-]{6,}\d$/.test(v)) return 'phone'
  if (/phone|mobile|\btel\b|contact number/.test(hint) && v.replace(/\D/g, '').length >= 7) return 'phone'
  if (/[£$€]\s?\d|\d+\s?(gbp|usd|eur|pounds?|dollars?|euros?)\b/i.test(v)) return 'money'
  if (/\b(location|address|venue|place|destination|home|hotel|restaurant|city|country)\b/.test(hint) && v.length > 2) return 'location'
  if (/\b(date|time|when|deadline|day|schedule)\b/.test(hint) || /\b\d{1,2}(st|nd|rd|th)?\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)/i.test(v)) return 'date'
  if (/\b(person|people|contact|wife|husband|friend|sister|brother|partner|guest|colleague|client)\b/.test(hint)) return 'person'
  return 'plain'
}

const FACT_RANK: Record<FactKind, number> = { location: 0, date: 1, person: 2, phone: 3, email: 4, money: 5, url: 6, plain: 7 }
function orderedFacts(facts: ProjectMemory[]): ProjectMemory[] {
  return [...facts].sort((a, b) => FACT_RANK[classifyFact(a)] - FACT_RANK[classifyFact(b)])
}

function FactCard({ fact, onForget }: { fact: ProjectMemory; onForget?: () => void }) {
  const kind = classifyFact(fact)
  const label = fact.key.replace(/[_-]+/g, ' ')
  const ctx = fact.context ? <div className="mt-0.5 text-sm text-ink-mute">{fact.context}</div> : null

  if (kind === 'location') {
    const q = encodeURIComponent(fact.value)
    return (
      <FactShell onForget={onForget} label={label} icon={<PinIcon />}>
        <div className="text-lg font-medium text-ink">{fact.value}</div>
        {ctx}
        <div className="mt-2.5 overflow-hidden rounded-md border border-line">
          <iframe title={fact.value} loading="lazy" className="h-40 w-full" style={{ border: 0 }} src={`https://www.google.com/maps?q=${q}&output=embed`} />
        </div>
        <a href={`https://www.google.com/maps/search/?api=1&query=${q}`} target="_blank" rel="noreferrer" className="mt-2 inline-block text-xs font-medium text-accent hover:brightness-90">
          Open in Maps ↗
        </a>
      </FactShell>
    )
  }
  if (kind === 'email')
    return (
      <FactShell onForget={onForget} label={label} icon={<MailIcon />}>
        <a href={`mailto:${fact.value}`} className="break-all text-lg font-medium text-accent hover:brightness-90">{fact.value}</a>
        {ctx}
      </FactShell>
    )
  if (kind === 'phone')
    return (
      <FactShell onForget={onForget} label={label} icon={<PhoneIcon />}>
        <a href={`tel:${fact.value.replace(/[^\d+]/g, '')}`} className="text-lg font-medium text-accent hover:brightness-90">{fact.value}</a>
        {ctx}
      </FactShell>
    )
  if (kind === 'url')
    return (
      <FactShell onForget={onForget} label={label} icon={<LinkIcon />}>
        <a href={fact.value} target="_blank" rel="noreferrer" className="break-all text-[1rem] font-medium text-accent hover:brightness-90">{fact.value}</a>
        {ctx}
      </FactShell>
    )
  if (kind === 'date')
    return (
      <FactShell onForget={onForget} label={label} icon={<CalendarIcon />}>
        <div className="text-lg font-medium text-ink">{fact.value}</div>
        {ctx}
      </FactShell>
    )
  if (kind === 'money')
    return (
      <FactShell onForget={onForget} label={label} icon={<PoundIcon />}>
        <div className="text-lg font-medium text-ink">{fact.value}</div>
        {ctx}
      </FactShell>
    )
  if (kind === 'person') {
    const initial = (fact.value || '?').trim().charAt(0).toUpperCase()
    return (
      <FactShell onForget={onForget} label={label} icon={<span className="grid h-5 w-5 place-items-center rounded-full bg-accent text-[0.75rem] font-bold text-on-accent">{initial}</span>}>
        <div className="text-lg font-medium text-ink">{fact.value}</div>
        {ctx}
      </FactShell>
    )
  }
  return (
    <FactShell onForget={onForget} label={label}>
      <div className="text-lg font-medium text-ink">{fact.value}</div>
      {ctx}
    </FactShell>
  )
}

const isDeck = (name: string) => /\.html?$/i.test(name)
const isPicture = (name: string) => /\.(png|jpe?g|gif|webp|avif|svg)$/i.test(name)
// Text we can show in-app rather than handing to the browser, which renders markdown as its own source.
const isReadable = (name: string) => /\.(md|markdown|txt|csv|tsv|json|log)$/i.test(name)
// Mirrors Thumbnails.Supported on the server. Kept in step by the 404 rather than by discipline: asking for a
// preview of something it can't draw simply falls back to the label.
const THUMBABLE = /\.(pdf|html?|docx|xlsx|md|markdown|txt|csv|tsv|json|log)$/i

/**
 * A project file as a tile with a preview.
 *
 * A filename tells you almost nothing; the thing itself tells you everything. So a picture shows the picture, and a
 * deck shows a scaled-down live render of its own first slide — the file already knows how to draw itself, which
 * makes a real thumbnail cheaper than a screenshot pipeline would be. Everything else falls back to its extension,
 * which is honest about being a document.
 */
function FileTile({
  file,
  onOpen,
  onDelete,
}: {
  file: { name: string; url: string; producedAt: string }
  onOpen: () => void
  onDelete: () => void
}) {
  const deck = isDeck(file.name)
  const picture = isPicture(file.name)
  const extension = (file.name.split('.').pop() ?? '').toUpperCase()
  // Every non-image gets a real picture of its first page from the server: a PDF rendered by PyMuPDF, a
  // document drawn from its text, and an HTML page screenshotted by headless Chrome — the browser it was
  // written for, so a deck looks like the deck. Cached after the first request. The iframe that used to do
  // this for decks was a live render in every tile: correct-looking, but it re-ran the page's own scripts and
  // fonts on every visit, and only ever worked where an iframe could run.
  const [thumbFailed, setThumbFailed] = useState(false)
  const drawable = !picture && THUMBABLE.test(file.name) && !thumbFailed

  return (
    <div className="group relative overflow-hidden rounded-lg border border-line bg-surface shadow-card transition hover:border-accent">
      <button onClick={onOpen} className="block w-full text-left" title={`Open ${file.name}`}>
        <div className="relative h-28 overflow-hidden bg-surface-mid">
          {picture && <img src={file.url} alt="" loading="lazy" className="h-full w-full object-cover" />}
          {drawable && (
            // Top-anchored: the useful part of a page is the top of it.
            <img
              src={`${file.url}/thumb`}
              alt=""
              loading="lazy"
              onError={() => setThumbFailed(true)}
              className="h-full w-full object-cover object-top"
            />
          )}
          {!picture && !drawable && (
            <div className="grid h-full place-items-center text-xs font-semibold tracking-wider text-ink-mute">
              {extension || 'FILE'}
            </div>
          )}
          {deck && (
            <span className="absolute bottom-1.5 right-1.5 rounded bg-black/60 px-1.5 py-0.5 text-[0.75rem] font-medium text-white">
              ▶ deck
            </span>
          )}
        </div>
        <div className="px-2.5 py-2">
          <div className="truncate text-xs text-ink" title={file.name}>
            {file.name}
          </div>
        </div>
      </button>

      <button
        onClick={onDelete}
        aria-label={`Remove ${file.name} from this project`}
        title="Remove from this project"
        className="absolute right-1.5 top-1.5 grid h-7 w-7 place-items-center rounded-md bg-bg/80 text-ink-mute opacity-60 backdrop-blur transition hover:text-danger hover:opacity-100"
      >
        ✕
      </button>
    </div>
  )
}

/**
 * One of a project's lists, editable in place.
 *
 * The same operations the assistant has, because the point of a list is that either side maintains it: the user
 * strikes out the pool, the assistant adds the spa it found, and neither has to rewrite the other's work. Edits go
 * straight to the server and the returned list replaces this one, so what is on screen is what is stored.
 */
function ListCard({
  list,
  onChanged,
  onDeleted,
}: {
  list: ProjectList
  onChanged: (updated: ProjectList) => void
  onDeleted: () => void
}) {
  const [adding, setAdding] = useState('')
  const [busy, setBusy] = useState(false)

  const change = async (payload: {
    add?: string[]
    remove?: string[]
    done?: string[]
    undone?: string[]
  }) => {
    setBusy(true)
    const updated = await updateList(list.id, payload)
    setBusy(false)
    if (updated) onChanged(updated)
  }

  // A checklist is a list you work THROUGH — a shopping list, packing, jobs before a trip. Criteria like
  // "preferred amenities" stay plain, because a tick against them would mean nothing.
  const ticked = new Set((list.done ?? []).map((d) => d.toLowerCase()))
  const isDone = (item: string) => ticked.has(item.toLowerCase())
  const doneCount = list.items.filter(isDone).length

  const add = async () => {
    const item = adding.trim()
    if (!item) return
    setAdding('')
    await change({ add: [item] })
  }

  return (
    <div className="rounded-lg border border-line bg-surface px-4 py-3.5 shadow-card">
      <div className="flex items-center gap-1.5 text-[0.75rem] font-semibold uppercase tracking-wider text-accent">
        <span className="min-w-0 flex-1 truncate">{list.title}</span>
        {list.checklist && list.items.length > 0 && (
          <span className="shrink-0 normal-case tracking-normal text-ink-mute">
            {doneCount}/{list.items.length}
          </span>
        )}
        {busy && <span className="shrink-0 normal-case tracking-normal text-ink-mute">saving…</span>}
        <button
          onClick={async () => {
            if (await deleteList(list.id)) onDeleted()
          }}
          aria-label={`Delete the "${list.title}" list`}
          title="Delete this list"
          className="shrink-0 text-ink-mute opacity-50 transition hover:text-danger hover:opacity-100"
        >
          ✕
        </button>
      </div>

      {list.items.length > 0 ? (
        <ul className="mt-2 space-y-1">
          {list.items.map((item) => (
            <li key={item} className="group flex items-center gap-2 text-[1rem] text-ink">
              {list.checklist ? (
                <input
                  type="checkbox"
                  checked={isDone(item)}
                  onChange={(e) =>
                    void change(e.target.checked ? { done: [item] } : { undone: [item] })
                  }
                  aria-label={item}
                  className="h-4 w-4 shrink-0 accent-[var(--accent,currentColor)]"
                />
              ) : (
                <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-accent" aria-hidden />
              )}
              <span className={`min-w-0 flex-1 ${isDone(item) ? 'text-ink-mute line-through' : ''}`}>
                {item}
              </span>
              <button
                onClick={() => void change({ remove: [item] })}
                aria-label={`Remove "${item}"`}
                title="Remove"
                className="shrink-0 text-xs text-ink-mute opacity-50 transition hover:text-danger hover:opacity-100"
              >
                ✕
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <div className="mt-2 text-sm text-ink-mute">Nothing on this list yet.</div>
      )}

      <div className="mt-2.5 flex items-center gap-2">
        <input
          value={adding}
          onChange={(e) => setAdding(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              void add()
            }
          }}
          placeholder="Add an item"
          className="min-w-0 flex-1 rounded-md border border-line bg-bg px-2.5 py-1.5 text-sm text-ink outline-none placeholder:text-ink-mute focus:border-accent"
        />
        <button
          onClick={() => void add()}
          disabled={!adding.trim()}
          className="shrink-0 rounded-md border border-line px-2.5 py-1.5 text-xs font-medium text-ink-soft transition hover:border-accent hover:text-ink disabled:opacity-40"
        >
          Add
        </button>
      </div>
    </div>
  )
}

function FactShell({
  label,
  icon,
  children,
  onForget,
}: {
  label: string
  icon?: ReactNode
  children: ReactNode
  /** Present when this fact can be forgotten. Every card shape goes through here, so one button covers them all. */
  onForget?: () => void
}) {
  return (
    <div className="group relative rounded-lg border border-line bg-surface px-4 py-3.5 shadow-card">
      <div className="flex items-center gap-1.5 text-[0.75rem] font-semibold uppercase tracking-wider text-accent">
        {icon}
        {label}
      </div>
      <div className="mt-1.5">{children}</div>
      {onForget && (
        // Faint until you want it, never hidden. It was opacity-0 until hover, which on a phone means it does not
        // exist — there is no hover on a touchscreen, so the control was unreachable for exactly the person most
        // likely to be tidying a list on the move.
        <button
          onClick={onForget}
          aria-label={`Forget "${label}"`}
          title="Forget this"
          className="absolute right-2 top-2 grid h-7 w-7 place-items-center rounded-md text-ink-mute opacity-50 transition hover:bg-surface-mid hover:text-danger focus:opacity-100 group-hover:opacity-100"
        >
          ✕
        </button>
      )}
    </div>
  )
}

// ---- task runner ----
function TaskRunner({ tasks, onClose, onCancel }: { tasks: Working[]; onClose: () => void; onCancel: (id: string) => void }) {
  const [, setNow] = useState(Date.now())
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(t)
  }, [])

  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-bg animate-fade-in">
      <header className="flex items-center gap-2 border-b border-line bg-bg/80 px-4 py-2.5 backdrop-blur sm:px-6">
        <button onClick={onClose} title="Back" className="-ml-1 grid h-9 w-9 place-items-center rounded-md text-ink-soft hover:bg-surface-mid hover:text-ink">
          <BackIcon />
        </button>
        <h1 className="text-sm font-medium tracking-tight text-ink-soft">Background tasks</h1>
      </header>

      <main className="flex-1 overflow-y-auto">
        <div className="mx-auto max-w-reading px-4 py-8">
          <h2 className="text-[1.375rem] font-semibold tracking-tight text-ink">Working on it</h2>
          <p className="mt-1 text-sm text-ink-soft">
            {tasks.length} {tasks.length === 1 ? 'task' : 'tasks'} running — keep chatting, results come back in the conversation.
          </p>
          <div className="mt-6 space-y-3">
            {tasks.length === 0 ? (
              <div className="pt-12 text-center text-sm text-ink-mute">Nothing running right now.</div>
            ) : (
              tasks.map((task) => (
                <div key={task.id} className="overflow-hidden rounded-xl border border-line bg-surface shadow-card">
                  <div className="progress-line h-0.5 w-full" />
                  <div className="px-5 py-4">
                    <div className="flex items-center justify-between">
                      <div className="text-[0.75rem] font-semibold uppercase tracking-wider text-accent">Task #{task.id}</div>
                      <div className="font-mono text-xs text-ink-mute">{elapsed(task.startedAt)}</div>
                    </div>
                    <div className="mt-1.5 text-[1rem] leading-relaxed text-ink">{task.task}</div>
                    <button onClick={() => onCancel(task.id)} className="mt-3 rounded-md border border-line px-3 py-1.5 text-xs text-ink-soft transition hover:bg-surface-low">
                      Cancel task
                    </button>
                  </div>
                </div>
              ))
            )}
          </div>
        </div>
      </main>
    </div>
  )
}

/**
 * How long a task has taken: still counting while it runs, frozen at what it took once it stopped. Ticking on
 * past the end is how a finished job goes on looking like a live one.
 */
function durationOf(t: Working): string {
  return elapsed(t.startedAt, t.endedAt)
}

function elapsed(startedAt: number, endedAt?: number): string {
  const s = Math.max(0, Math.floor(((endedAt ?? Date.now()) - startedAt) / 1000))
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  return `${m}m ${s % 60}s`
}

// ---- chat messages ----
/**
 * The live task bar under a message: a spinner and what the task is actually doing right now.
 *
 * Pinned to the message that kicked the task off, because that's where the user's attention already is — a
 * detached list somewhere else means "something is happening" without saying what, or why, or to which request.
 * The label follows the task (a plan's current step re-labels it), so it reads as progress rather than a spinner
 * that never changes.
 */
/**
 * The user's browser, on the user's phone.
 *
 * For the one case that matters: a task is parked on a login wall and the user is nowhere near the machine. A
 * screenshot after every action, taps forwarded as clicks, typing forwarded as keystrokes. What they type goes
 * straight to the page — it is never held here, never sent to a model, never logged.
 */
function RemoteBrowser({ onClose, initialUrl }: { onClose: () => void; initialUrl?: string }) {
  const [image, setImage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [text, setText] = useState('')
  const [url, setUrl] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  // The page's CSS viewport. A screenshot is in DEVICE pixels, a click is in CSS pixels — mapping a tap against
  // the image's own size puts it a quarter too far down and right on a scaled display.
  const [viewport, setViewport] = useState<{ width: number; height: number } | null>(null)
  const imgRef = useRef<HTMLImageElement | null>(null)

  const act = async (payload: Record<string, unknown>) => {
    setBusy(true)
    try {
      const res = await fetch('/api/browser/act', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      })
      const data = await res.json()
      if (data.image) setImage(data.image + '?t=' + Date.now())
      // Silence was the actual bug the first time: a tap that did nothing and said nothing.
      setProblem(data.error ?? data.result ?? null)
    } catch (e) {
      setProblem(String(e))
    } finally {
      setBusy(false)
    }
  }

  useEffect(() => {
    // Land on the page that needs signing in, rather than asking the user to type an address they were just
    // told. When a task parks on a login wall its question carries the URL, so the panel opens there.
    if (initialUrl) void act({ kind: 'navigate', url: initialUrl })
    void fetch('/api/browser/viewport')
      .then((r) => r.json())
      .then((v) => v.width > 0 && setViewport(v))
      .catch(() => {})

    // Frames arrive down a socket, so there is no request per frame and no image to load: the bytes come in and
    // go straight to an object URL. The previous version fetched a URL and then let the browser fetch the picture,
    // which was two round-trips for every frame and the reason it felt like a slideshow.
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:'
    const socket = new WebSocket(`${proto}//${location.host}/api/browser/stream`)
    let previous: string | null = null

    socket.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data as string)
        if (data.error) {
          setProblem(data.error)
          return
        }
        if (!data.frame) return

        // An object URL rather than a data URI: the browser decodes it without parsing a megabyte of base64 into
        // the DOM, and revoking the last one keeps this from leaking a blob per frame.
        const bytes = Uint8Array.from(atob(data.frame), (c) => c.charCodeAt(0))
        const url = URL.createObjectURL(new Blob([bytes], { type: data.mime ?? 'image/jpeg' }))
        setImage(url)
        setProblem(null)
        if (previous) URL.revokeObjectURL(previous)
        previous = url
      } catch {
        /* a malformed frame is not worth a message; the next one is milliseconds away */
      }
    }
    socket.onerror = () => setProblem('lost the connection to the browser')

    return () => {
      socket.close()
      if (previous) URL.revokeObjectURL(previous)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // A tap maps to a page coordinate: the screenshot is the viewport, so scale by the rendered size.
  const tap = (e: { clientX: number; clientY: number }) => {
    const el = imgRef.current
    if (!el) return
    const box = el.getBoundingClientRect()
    // Scale to the page's own coordinate space, falling back to the image's if we couldn't read the viewport.
    const pageW = viewport?.width || el.naturalWidth
    const pageH = viewport?.height || el.naturalHeight
    const x = Math.round(((e.clientX - box.left) / box.width) * pageW)
    const y = Math.round(((e.clientY - box.top) / box.height) * pageH)
    void act({ kind: 'click', x, y })
  }

  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-black/85 backdrop-blur-sm">
      <div className="flex items-center gap-2 px-3 py-2">
        <input
          className="min-w-0 flex-1 rounded-lg border border-white/15 bg-white/5 px-3 py-1.5 text-sm text-white outline-none placeholder:text-white/35"
          placeholder="Go to a site — mail.proton.me"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          onKeyDown={(e) => {
            if (e.key !== 'Enter') return
            e.preventDefault()
            if (url.trim()) void act({ kind: 'navigate', url: url.trim() })
          }}
        />
        <button
          className="shrink-0 rounded-lg border border-white/15 px-3 py-1.5 text-xs text-white/80 transition hover:text-white"
          onClick={() => url.trim() && void act({ kind: 'navigate', url: url.trim() })}
        >
          go
        </button>
        {busy && <span className="shrink-0">working…</span>}
        <span className="shrink-0 text-[0.75rem] text-white/40">live</span>
        <button className="shrink-0 transition hover:text-white" onClick={onClose} aria-label="Close">
          {'✕'}
        </button>
      </div>

      {problem && (
        <div className="mx-3 mb-1 rounded-lg border border-amber-400/30 bg-amber-400/10 px-3 py-1.5 text-xs text-amber-200">
          {problem}
        </div>
      )}

      <div className="min-h-0 flex-1 overflow-auto bg-black/40 p-2">
        {image ? (
          <img
            ref={imgRef}
            src={image}
            alt="the page"
            onClick={tap}
            className="mx-auto max-w-full cursor-crosshair rounded border border-white/10"
          />
        ) : (
          <div className="flex h-full items-center justify-center text-sm text-white/50">
            No browser connected.
          </div>
        )}
      </div>

      <div className="flex items-center gap-2 border-t border-white/10 px-3 py-2">
        <input
          className="min-w-0 flex-1 rounded-lg border border-white/15 bg-white/5 px-3 py-2 text-sm text-white outline-none placeholder:text-white/35"
          placeholder="Tap a field on the page, then type here"
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key !== 'Enter') return
            e.preventDefault()
            const value = text
            setText('')
            void act({ kind: 'type', text: value }).then(() => act({ kind: 'key', key: 'Enter' }))
          }}
        />
        <button
          className="shrink-0 rounded-lg border border-white/15 px-3 py-2 text-sm text-white/80 transition hover:text-white"
          onClick={() => {
            const value = text
            setText('')
            void act({ kind: 'type', text: value })
          }}
        >
          send
        </button>
        <button
          className="shrink-0 rounded-lg border border-white/15 px-3 py-2 text-sm text-white/80 transition hover:text-white"
          onClick={() => void act({ kind: 'key', key: 'Enter' })}
        >
          enter
        </button>
      </div>
    </div>
  )
}

/**
 * The files a task handed back. A presentation opens where the user is standing rather than downloading —
 * clicking a deck should feel like starting it, not like filing it.
 */
function FileCards({
  files,
  sessionId,
  onOpen,
}: {
  files: { name: string; caption?: string }[]
  sessionId: string
  onOpen: (name: string) => void
}) {
  const isDeck = (n: string) => /\.html?$/i.test(n)
  return (
    <div className="space-y-1.5">
      {files.map((f) => {
        const href = `/api/session/${sessionId}/files/${encodeURIComponent(f.name)}`
        const deck = isDeck(f.name)
        const readable = isReadable(f.name)
        return (
          <button
            key={f.name}
            className="flex w-full items-center gap-2.5 rounded-lg border border-line bg-surface/60 px-3 py-2 text-left text-sm text-ink-soft transition hover:border-accent"
            onClick={() =>
              deck || readable ? onOpen(f.name) : window.open(href, '_blank', 'noreferrer')
            }
          >
            {/* The same page-preview the project shelf shows, so a file a chat just produced looks like the
                thing it is at the moment it arrives — not a paperclip you have to open to identify. */}
            {THUMBABLE.test(f.name) ? (
              <img
                src={`${href}/thumb`}
                alt=""
                loading="lazy"
                onError={(e) => ((e.currentTarget.style.display = 'none'))}
                className="h-10 w-8 shrink-0 rounded border border-line bg-surface-mid object-cover object-top"
              />
            ) : (
              <span className="shrink-0 text-base" aria-hidden>
                {deck ? '▶' : '📄'}
              </span>
            )}
            <span className="min-w-0 flex-1">
              <span className="block truncate text-ink">{f.name}</span>
              <span className="block truncate text-xs text-ink-mute">
                {f.caption ?? (deck ? 'Presentation — click to open' : 'Click to open')}
              </span>
            </span>
          </button>
        )
      })}
    </div>
  )
}

/**
 * A deck, full-screen, over the conversation. Sandboxed: it is generated HTML with its own script for paging,
 * so it gets to run that and nothing else — no access back into the app, no network.
 */
function Presenter({ src, title, onClose }: { src: string; title: string; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    // The iframe swallows its own arrow keys; Escape has to work from either side, so listen here too.
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-black/80 backdrop-blur-sm">
      <div className="flex items-center gap-3 px-4 py-2 text-xs text-white/70">
        <span className="min-w-0 flex-1 truncate">{title}</span>
        {/*
          Printed on the server, not through the browser's dialog.
          The page's own CSS is correct — A4, no margin, a hard break after each .page — and printed headlessly
          it yields every page. Through print() on the iframe it did not: an iframe print inherits whatever
          scale, margins and background-graphics setting that browser happens to have, and pages went missing.
          A plain download link means the same file for everyone, with the backgrounds, and nothing to tick.
        */}
        <a
          className="shrink-0 transition hover:text-white"
          href={`${src}/pdf`}
          download={title.replace(/\.html?$/i, '.pdf')}
        >
          save as PDF
        </a>
        <a
          className="shrink-0 transition hover:text-white"
          href={src}
          target="_blank"
          rel="noreferrer noopener"
        >
          open in a tab
        </a>
        <button className="shrink-0 transition hover:text-white" onClick={onClose} aria-label="Close">
          {'✕'}
        </button>
      </div>
      <iframe
        id="deck-frame"
        src={src}
        title={title}
        className="min-h-0 flex-1 border-0 bg-white"
        sandbox="allow-scripts allow-popups allow-modals allow-same-origin"
      />
    </div>
  )
}

/**
 * The links in a message, shown as cards under it. Only links that advertise a picture get one: a card with no
 * image is just the link again in a box, and the link is already in the prose above. So this stays a visual
 * strip, and everything else keeps its place in the sentence that mentioned it.
 */
/** A browser window. Same stroke weight and 16px box as the other header icons. */
/**
 * Always on, as an icon: a microphone, with sound coming off it when it is listening.
 *
 * Same stroke weight and 16px box as its neighbours. The listening state is drawn rather than coloured alone, because
 * the one question this icon has to answer from the other side of a room is whether the microphone is live.
 */
function EarIcon({ listening }: { listening: boolean }) {
  return (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
      <rect x="6" y="1.75" width="4" height="7.5" rx="2" />
      <path d="M4 7.25a4 4 0 008 0" strokeLinecap="round" />
      <path d="M8 11.25v2.5" strokeLinecap="round" />
      {listening && (
        <>
          <path d="M12.5 4.5a5 5 0 010 5" strokeLinecap="round" opacity="0.55" />
          <path d="M3.5 4.5a5 5 0 000 5" strokeLinecap="round" opacity="0.55" />
        </>
      )}
    </svg>
  )
}

function BrowserIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
      <rect x="1.75" y="2.75" width="12.5" height="10.5" rx="1.5" />
      <path d="M1.75 6h12.5" />
      <path d="M4.25 4.5h.01M6.25 4.5h.01" strokeLinecap="round" />
    </svg>
  )
}

function LinkCards({ cards }: { cards?: LinkCard[] }) {
  /**
   * Images that turned out not to load, dropped from the row.
   *
   * Needed once pictures started pointing straight at the source. Most sites that refuse our server will serve the
   * same image to this browser, which is the whole reason it works — but some check the referer and refuse anyway, and
   * a card holding a broken frame is worse than one less picture.
   */
  const [broken, setBroken] = useState<Record<string, true>>({})
  const shown = (cards ?? []).filter((c) => c.image && !broken[c.image])
  if (shown.length === 0) return null
  return (
    <div className="mt-2 flex snap-x gap-2 overflow-x-auto pb-1">
      {shown.map((c) => (
        <a
          key={c.url}
          href={c.url}
          target="_blank"
          rel="noreferrer noopener"
          className="w-40 shrink-0 snap-start overflow-hidden rounded-lg border border-line bg-surface/60 transition hover:border-accent"
          title={c.title ?? c.url}
        >
          {/* no-referrer both ways: it gets past the hotlink checks that refuse a foreign referer, and it keeps the
              page you were on out of the request. */}
          <img
            src={c.image}
            alt={c.title ?? ''}
            loading="lazy"
            referrerPolicy="no-referrer"
            onError={() => c.image && setBroken((b) => ({ ...b, [c.image!]: true }))}
            className="h-24 w-full bg-surface object-cover"
          />
          <div className="px-2 py-1.5">
            <div className="line-clamp-2 text-xs leading-snug text-ink-soft">{c.title ?? c.url}</div>
            {/* What it is beats where it came from, so a caption wins the second line when there is one. */}
            {c.subtitle ? (
              <div className="mt-0.5 line-clamp-2 text-[0.75rem] leading-snug text-ink-mute">{c.subtitle}</div>
            ) : c.site ? (
              <div className="mt-0.5 truncate text-[0.75rem] text-ink-mute">{c.site}</div>
            ) : null}
          </div>
        </a>
      ))}
    </div>
  )
}

/**
 * How a finished task reads. Once it's over, the step list is scaffolding — it existed to show the thing was
 * moving, and the task's own name is the whole story. So the bar collapses to one line.
 *
 * "waiting" deliberately has no entry: that task is paused on a question, not finished, so it keeps its spinner.
 */
/**
 * What a finished task's row should say. Null means "still going" — which is why every state that ISN'T listed
 * here has to be listed here: an unrecognised status renders as a spinner and a cancel button, so a task that
 * stopped hours ago goes on claiming to be running, and offers to be cancelled.
 */
function outcomeOf(status?: string): { mark: string; label: string; tone: string } | null {
  switch (status) {
    case 'done':
      return { mark: '✓', label: 'Completed', tone: 'text-accent' }
    case 'cancelled':
      return { mark: '✕', label: 'Stopped', tone: 'text-ink-mute' }
    case 'failed':
      return { mark: '!', label: 'Failed', tone: 'text-danger' }
    case 'interrupted':
      // Its process died mid-task (a restart, a crash). It didn't finish and it isn't coming back.
      return { mark: '⌁', label: 'Interrupted', tone: 'text-ink-mute' }
    case 'waiting':
      // Alive, but it can't move until the question it asked gets an answer — not the same as working.
      return { mark: '?', label: 'Waiting on you', tone: 'text-ink-soft' }
    default:
      return null
  }
}

function TaskBar({ tasks, onCancel }: { tasks: Working[]; onCancel: (id: string) => void }) {
  // A ticking clock while anything is still going. "It has been three minutes" is the difference between waiting
  // and wondering whether to give up — and a spinner on its own never tells you which.
  const [, tick] = useState(0)
  const anyRunning = tasks.some((t) => !outcomeOf(t.status))
  useEffect(() => {
    if (!anyRunning) return
    const timer = setInterval(() => tick((n) => n + 1), 1000)
    return () => clearInterval(timer)
  }, [anyRunning])

  if (tasks.length === 0) return null
  return (
    <div className="mt-2 space-y-1">
      {tasks.map((t) => (
        <div
          key={t.id}
          className="rounded-lg border border-line bg-surface/60 px-2.5 py-1.5 text-xs text-ink-soft"
        >
          <div className="flex items-center gap-2">
            {outcomeOf(t.status) ? (
              <span className={`w-3 shrink-0 text-center ${outcomeOf(t.status)!.tone}`} aria-hidden>
                {outcomeOf(t.status)!.mark}
              </span>
            ) : (
              <span
                className="h-3 w-3 shrink-0 animate-spin rounded-full border-[1.5px] border-accent border-t-transparent"
                aria-hidden
              />
            )}
            <span className="min-w-0 flex-1 truncate" title={t.task}>
              {t.task}
            </span>
            {/* How long it has been going, or how long it took. Ticking while it runs, frozen once it stops. */}
            <span className="shrink-0 font-mono text-[0.75rem] text-ink-mute">
              {formatDuration(Math.max(0, Math.round(((t.endedAt ?? Date.now()) - t.startedAt) / 1000)))}
            </span>
            {outcomeOf(t.status) ? (
              <span className={`shrink-0 ${outcomeOf(t.status)!.tone}`}>{outcomeOf(t.status)!.label}</span>
            ) : (
              <button
                className="shrink-0 text-ink-mute transition hover:text-danger"
                onClick={() => onCancel(t.id)}
                aria-label="Stop this task"
                title="Stop this task"
              >
                ✕
              </button>
            )}
          </div>

          {/* The last few tool calls, newest at the bottom — proof it's working, not just spinning. */}
          {!outcomeOf(t.status) && (t.steps ?? []).length > 0 && (
            <div className="mt-1.5 space-y-0.5 border-l border-line pl-3 ml-1">
              {(t.steps ?? []).map((s) => (
                <div key={s.key} className="flex items-center gap-1.5 text-[0.75rem] text-ink-mute">
                  <span className={s.done ? 'text-accent' : 'animate-pulse'} aria-hidden>
                    {s.done ? '✓' : '•'}
                  </span>
                  <span className={s.thinking ? 'shrink-0 italic' : 'shrink-0 font-mono'}>{s.name}</span>
                  {s.detail && (
                    <span className="min-w-0 truncate" title={s.detail}>
                      {s.detail}
                    </span>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      ))}
    </div>
  )
}

function MessageRow({ message, sessionId }: { message: UiMessage; sessionId: string }) {
  // While the model is still only thinking (no answer text yet) we tick a clock so the timer counts up
  // live; the moment text starts streaming, thinkMs is frozen and the indicator nudges up to make room.
  const isAssistant = message.role === 'assistant'
  const onlyIndicator = isAssistant && message.streaming && message.content.length === 0
  const thinking = onlyIndicator && !!message.reasoning
  const [, tick] = useState(0)
  useEffect(() => {
    if (!thinking) return
    const t = setInterval(() => tick((n) => n + 1), 250)
    return () => clearInterval(t)
  }, [thinking])

  if (message.role === 'user') {
    const bubble = message.audio ? (
      <VoiceBubble message={message} />
    ) : (
      <div className="flex justify-end animate-fade-in-up">
        <div className="max-w-[85%] whitespace-pre-wrap rounded-2xl rounded-br-md bg-surface-low px-4 py-2.5 text-[1rem] leading-relaxed text-ink">
          {message.content}
        </div>
      </div>
    )
    if (!message.files?.length) return bubble
    return (
      <div className="space-y-1.5">
        {/* What you attached, as links to the stored copies — the assistant is told separately where they are. */}
        <div className="flex flex-wrap justify-end gap-1.5 animate-fade-in-up">
          {message.files.map((name) => (
            <a
              key={name}
              href={`/api/session/${sessionId}/files/${encodeURIComponent(name)}`}
              target="_blank"
              rel="noreferrer"
              className="flex max-w-[85%] items-center gap-1.5 rounded-lg border border-line bg-surface px-2 py-1 text-xs text-ink-soft transition hover:bg-surface-low"
            >
              <PaperclipIcon />
              <span className="min-w-0 truncate" title={name}>
                {name}
              </span>
            </a>
          ))}
        </div>
        {bubble}
      </div>
    )
  }

  const liveSecs = thinking && message.thinkStart ? Math.max(0, Math.round((Date.now() - message.thinkStart) / 1000)) : 0
  const doneSecs = message.thinkMs ? Math.max(1, Math.round(message.thinkMs / 1000)) : 0
  const thinkLabel = thinking ? `Thinking… ${liveSecs}s` : doneSecs ? `Thought for ${doneSecs}s` : 'thought'

  return (
    <div className={`flex gap-3 animate-fade-in-up ${onlyIndicator ? 'items-center' : 'items-start'}`}>
      <span className="grid h-7 w-7 shrink-0 place-items-center rounded-full bg-accent text-xs font-semibold text-on-accent">S</span>
      <div className="min-w-0 flex-1 space-y-1.5">
        {message.reasoning && (
          <details className="text-xs text-ink-mute marker:text-ink-mute">
            <summary className={`cursor-pointer select-none ${thinking ? 'shimmer font-medium' : 'text-ink-soft'}`}>{thinkLabel}</summary>
            <pre className="mt-2 whitespace-pre-wrap font-sans leading-relaxed text-ink-mute">{message.reasoning}</pre>
          </details>
        )}
        {message.content ? (
          <div className="text-[1rem] leading-relaxed text-ink">
            <Markdown text={message.content} />
          </div>
        ) : message.streaming && !message.reasoning ? (
          <TypingDots />
        ) : null}
        {message.content && !message.streaming && (
          <div className="flex items-center gap-1 pt-1">
            <CopyButton text={message.content} />
            <FeedbackButtons sessionId={sessionId} messageId={message.id} />
          </div>
        )}
      </div>
    </div>
  )
}

function FeedbackButtons({ sessionId, messageId }: { sessionId: string; messageId: number }) {
  const [rated, setRated] = useState<'up' | 'down' | null>(null)
  function rate(r: 'up' | 'down') {
    setRated(r)
    sendFeedback(sessionId, messageId, r)
  }
  return (
    <span className="inline-flex items-center gap-0.5">
      <button
        onClick={() => rate('up')}
        title="Good response"
        className={`grid h-7 w-7 place-items-center rounded-md transition hover:bg-surface-mid ${rated === 'up' ? 'text-accent' : 'text-ink-mute hover:text-ink-soft'}`}
      >
        <ThumbIcon up />
      </button>
      <button
        onClick={() => rate('down')}
        title="Bad response"
        className={`grid h-7 w-7 place-items-center rounded-md transition hover:bg-surface-mid ${rated === 'down' ? 'text-danger' : 'text-ink-mute hover:text-ink-soft'}`}
      >
        <ThumbIcon />
      </button>
    </span>
  )
}

function VoiceBubble({ message }: { message: UiMessage }) {
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const [playing, setPlaying] = useState(false)
  const [progress, setProgress] = useState(0)
  const audio = message.audio!
  function toggle() {
    const a = audioRef.current
    if (!a) return
    if (playing) a.pause()
    else a.play()
  }
  return (
    <div className="flex justify-end">
      <div className="max-w-[85%] rounded-2xl rounded-br-md bg-surface-low px-3 py-2.5 text-ink">
        <div className="flex items-center gap-3">
          <button onClick={toggle} disabled={!audio.url} className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-accent text-on-accent hover:brightness-110 disabled:opacity-50">
            {playing ? <PauseIcon /> : <PlayIcon />}
          </button>
          <div className="w-44 sm:w-56">
            <Waveform peaks={audio.peaks} progress={progress} idleClass="bg-ink/20" activeClass="bg-accent" />
          </div>
          <span className="shrink-0 font-mono text-[0.75rem] text-ink-mute">{formatDuration(audio.duration)}</span>
        </div>
        {message.content && <div className="mt-1.5 text-sm text-ink-soft">{message.content}</div>}
        {audio.url && (
          <audio
            ref={audioRef}
            src={audio.url}
            className="hidden"
            onPlay={() => setPlaying(true)}
            onPause={() => setPlaying(false)}
            onEnded={() => {
              setPlaying(false)
              setProgress(0)
            }}
            onTimeUpdate={(e) => {
              const a = e.currentTarget
              setProgress(a.duration ? a.currentTime / a.duration : 0)
            }}
          />
        )}
      </div>
    </div>
  )
}

function Waveform({ peaks, progress, idleClass, activeClass }: { peaks: number[]; progress: number; idleClass: string; activeClass: string }) {
  return (
    <div className="flex h-7 items-center gap-px overflow-hidden">
      {peaks.map((p, i) => {
        const on = peaks.length ? (i + 1) / peaks.length <= progress : false
        return <div key={i} className={`min-w-0 flex-1 rounded-full ${on ? activeClass : idleClass}`} style={{ height: `${Math.max(12, p * 100)}%` }} />
      })}
    </div>
  )
}

/**
 * A text document, read where you are.
 *
 * Opening a markdown file used to hand it to the browser, which has no idea what markdown is: you got a new tab
 * of raw source, hashes and asterisks and all — for a file this app renders beautifully three inches away in
 * every chat message. HTML already opened in the Presenter; this is the same courtesy for everything textual,
 * using the very same renderer the conversation uses.
 */
function DocViewer({ src, title, onClose }: { src: string; title: string; onClose: () => void }) {
  const [text, setText] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)
  const markdown = /\.(md|markdown)$/i.test(title)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  useEffect(() => {
    let live = true
    fetch(src)
      .then((r) => (r.ok ? r.text() : Promise.reject(new Error(String(r.status)))))
      .then((t) => live && setText(t))
      .catch(() => live && setFailed(true))
    return () => {
      live = false
    }
  }, [src])

  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-black/80 backdrop-blur-sm" onClick={onClose}>
      <div className="flex items-center gap-3 px-4 py-2 text-xs text-white/70">
        <span className="min-w-0 flex-1 truncate">{title}</span>
        <a
          className="shrink-0 transition hover:text-white"
          href={src}
          download={title}
          onClick={(e) => e.stopPropagation()}
        >
          download
        </a>
        <button className="shrink-0 transition hover:text-white" onClick={onClose} aria-label="Close">
          {'✕'}
        </button>
      </div>
      {/* Stop propagation so clicking the page itself doesn't dismiss what you're reading. */}
      <div className="min-h-0 flex-1 overflow-y-auto px-4 pb-6" onClick={(e) => e.stopPropagation()}>
        <div className="mx-auto max-w-3xl rounded-lg bg-surface p-6 shadow-card">
          {failed ? (
            <p className="text-sm text-ink-mute">Couldn't read that file.</p>
          ) : text === null ? (
            <p className="text-sm text-ink-mute">Loading…</p>
          ) : markdown ? (
            <Markdown text={text} />
          ) : (
            // Anything else textual is shown as written — a CSV or a log is meant to line up.
            <pre className="overflow-x-auto whitespace-pre-wrap break-words text-xs text-ink">{text}</pre>
          )}
        </div>
      </div>
    </div>
  )
}

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false)
  async function copy() {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      /* blocked */
    }
  }
  return (
    <button onClick={copy} className="inline-flex items-center gap-1.5 rounded-md px-1.5 py-1 text-[0.75rem] text-ink-mute transition hover:bg-surface-mid hover:text-ink-soft" title="Copy">
      {copied ? <CheckIcon /> : <CopyIcon />}
      {copied ? 'copied' : 'copy'}
    </button>
  )
}

function TypingDots() {
  return (
    <span className="inline-flex gap-1 py-1.5">
      <span className="h-1.5 w-1.5 rounded-full bg-ink-mute blink" />
      <span className="h-1.5 w-1.5 rounded-full bg-ink-mute blink" style={{ animationDelay: '0.2s' }} />
      <span className="h-1.5 w-1.5 rounded-full bg-ink-mute blink" style={{ animationDelay: '0.4s' }} />
    </span>
  )
}

// ---- interactive worker question ----
/**
 * A quantity, as a number field rather than a sentence to type.
 *
 * The unit is shown beside the box instead of being left for the user to write, so "2" is a complete answer and
 * what gets sent back is "2 people" — unambiguous at the other end without anyone having to spell it out.
 */
function NumberAnswer({
  number,
  onSubmit,
}: {
  number: WorkerQuestion['number']
  onSubmit: (value: string) => void
}) {
  const [value, setValue] = useState(number?.suggested != null ? String(number.suggested) : '')
  const unit = number?.unit?.trim()

  function send() {
    const n = value.trim()
    if (!n) return
    onSubmit(unit ? `${n} ${unit}` : n)
  }

  return (
    <div className="mt-3 flex items-center gap-2">
      <input
        type="number"
        value={value}
        min={number?.min}
        max={number?.max}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault()
            send()
          }
        }}
        autoFocus
        className="w-28 rounded-lg border border-line bg-surface px-3 py-2 text-sm text-ink outline-none transition focus:border-accent"
      />
      {unit && <span className="text-sm text-ink-soft">{unit}</span>}
      <button
        onClick={send}
        disabled={!value.trim()}
        className="rounded-lg bg-accent px-3.5 py-2 text-sm font-medium text-on-accent transition enabled:hover:brightness-110 disabled:opacity-30"
      >
        Send
      </button>
    </div>
  )
}

/**
 * A question asked somewhere the user isn't looking, answerable from here.
 *
 * Deliberately quieter than {@link QuestionCard}: that one is the conversation you are in asking you something, this
 * one is a job you started elsewhere. It says which job, so "which model is it?" is answerable at all — a question
 * with no subject is worse than no question — and offers the thread for the rest of the story.
 */
function ElsewhereCard({
  q,
  onAnswered,
  onOpen,
}: {
  q: OutstandingQuestion
  onAnswered: () => void
  onOpen: () => void
}) {
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)

  async function send() {
    const body = text.trim()
    if (!body || busy) return
    setBusy(true)
    await answerTaskQuestion(q.session, q.taskId, body)
    setText('')
    setBusy(false)
    onAnswered()
  }

  return (
    <div className="animate-fade-in-up overflow-hidden rounded-xl border border-line bg-surface px-4 py-3.5 shadow-card">
      <div className="flex items-center gap-1.5 text-[0.75rem] font-semibold uppercase tracking-wider text-amber-600">
        <QuestionIcon />
        <span className="min-w-0 flex-1 truncate">{q.task}</span>
        <button onClick={onOpen} className="shrink-0 text-[0.6875rem] font-medium normal-case text-ink-mute hover:text-ink">
          open thread
        </button>
      </div>
      <div className="mt-1.5 whitespace-pre-wrap break-words text-sm leading-relaxed text-ink">{q.question}</div>
      <div className="mt-3 flex items-center gap-2">
        <input
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              send()
            }
          }}
          placeholder="Your answer…"
          className="min-w-0 flex-1 rounded-lg border border-line bg-surface px-3 py-2 text-sm text-ink outline-none transition focus:border-accent"
        />
        <button
          onClick={send}
          disabled={!text.trim() || busy}
          className="shrink-0 rounded-lg bg-accent px-3.5 py-2 text-sm font-medium text-on-accent transition enabled:hover:brightness-110 disabled:opacity-30"
        >
          {busy ? 'Sending…' : 'Send'}
        </button>
      </div>
    </div>
  )
}

function QuestionCard({
  q,
  onAnswer,
  onDismiss,
}: {
  q: WorkerQuestion
  onAnswer: (taskId: string, text: string) => void
  onDismiss?: (taskId: string) => void
}) {
  const [text, setText] = useState('')
  function submit(value: string) {
    const body = value.trim()
    if (!body) return
    setText('')
    onAnswer(q.taskId, body)
  }
  return (
    <div className="overflow-hidden rounded-xl border border-accent/30 bg-accent-soft shadow-card animate-fade-in-up">
      <div className="px-4 py-3.5">
        <div className="flex items-center gap-1.5 text-[0.75rem] font-semibold uppercase tracking-wider text-accent">
          <QuestionIcon />
          <span className="min-w-0 flex-1">Needs your answer</span>
          {/* Answering is not the only way a question stops mattering: work moves on, or another task covers
              the same ground. Without this the only way to clear one was to answer something pointless. */}
          {onDismiss && (
            <button
              onClick={() => onDismiss(q.taskId)}
              aria-label="Dismiss this question"
              title="Dismiss — I don't need to answer this"
              className="shrink-0 text-ink-mute opacity-50 transition hover:text-danger hover:opacity-100"
            >
              ✕
            </button>
          )}
        </div>
        <div className="mt-1.5 text-[1rem] leading-relaxed text-ink">{q.question}</div>

        {/* A map to look at, when the question is about a place. Confirming where you are by reading coordinates
            back to yourself is not confirming anything; seeing the pin is. The free-text box stays below for
            "no, somewhere else". */}
        {q.kind === 'place' && q.place && (
          <div className="mt-3 overflow-hidden rounded-lg border border-line">
            <iframe
              title="The place in question"
              className="block h-44 w-full"
              loading="lazy"
              referrerPolicy="no-referrer"
              src={
                `https://www.openstreetmap.org/export/embed.html?bbox=` +
                `${(q.place.longitude - 0.004).toFixed(5)}%2C${(q.place.latitude - 0.002).toFixed(5)}%2C` +
                `${(q.place.longitude + 0.004).toFixed(5)}%2C${(q.place.latitude + 0.002).toFixed(5)}` +
                `&layer=mapnik&marker=${q.place.latitude}%2C${q.place.longitude}`
              }
            />
            <div className="flex items-center gap-2 border-t border-line bg-surface px-3 py-2">
              <span className="min-w-0 flex-1 truncate text-xs text-ink-soft">
                {q.place.label ?? `${q.place.latitude.toFixed(4)}, ${q.place.longitude.toFixed(4)}`}
              </span>
              {/* OSM rate-limits that embed and answers 503 often enough — measured. A blank frame with no way
                  to check the spot is a dead end, so there's always a link out. */}
              <a
                href={`https://www.openstreetmap.org/?mlat=${q.place.latitude}&mlon=${q.place.longitude}#map=17/${q.place.latitude}/${q.place.longitude}`}
                target="_blank"
                rel="noreferrer"
                className="shrink-0 text-xs text-ink-mute underline decoration-dotted transition hover:text-ink"
              >
                open map
              </a>
              <button
                onClick={() => submit(q.place!.label ?? `${q.place!.latitude}, ${q.place!.longitude}`)}
                className="shrink-0 rounded-md bg-accent px-3 py-1.5 text-xs font-medium text-on-accent transition hover:brightness-110"
              >
                Yes, that's the spot
              </button>
            </div>
          </div>
        )}

        {/* A number field for a quantity, with the unit shown rather than left to be typed. */}
        {q.kind === 'number' && <NumberAnswer number={q.number} onSubmit={submit} />}

        {q.kind === 'confirm' && (
          <div className="mt-3 flex gap-2">
            <button
              onClick={() => submit('Yes')}
              className="rounded-lg bg-accent px-4 py-2 text-sm font-medium text-on-accent transition hover:brightness-110"
            >
              Yes
            </button>
            <button
              onClick={() => submit('No')}
              className="rounded-lg border border-line bg-surface px-4 py-2 text-sm text-ink transition hover:border-accent/50"
            >
              No
            </button>
          </div>
        )}

        {q.options.length > 0 && (
          <div className="mt-3 flex flex-wrap gap-2">
            {q.options.map((o, i) => (
              <button
                key={i}
                onClick={() => submit(o)}
                className="rounded-full border border-line bg-surface px-3.5 py-1.5 text-sm text-ink transition hover:border-accent/50 hover:text-accent"
              >
                {o}
              </button>
            ))}
          </div>
        )}
        <div className="mt-3 flex items-center gap-2">
          <input
            value={text}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault()
                submit(text)
              }
            }}
            placeholder="Or type your own answer…"
            className="min-w-0 flex-1 rounded-lg border border-line bg-surface px-3 py-2 text-sm text-ink outline-none transition focus:border-accent placeholder:text-ink-mute"
          />
          <button
            onClick={() => submit(text)}
            disabled={!text.trim()}
            title="Send answer"
            className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-accent text-on-accent transition enabled:hover:brightness-110 disabled:opacity-30"
          >
            <ArrowUp />
          </button>
        </div>
      </div>
    </div>
  )
}

// ---- icons ----
function QuestionIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="12" r="10" />
      <path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3" />
      <line x1="12" y1="17" x2="12.01" y2="17" />
    </svg>
  )
}
function BurgerIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
      <line x1="3" y1="6" x2="21" y2="6" />
      <line x1="3" y1="12" x2="21" y2="12" />
      <line x1="3" y1="18" x2="21" y2="18" />
    </svg>
  )
}
function PinIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M21 10c0 7-9 13-9 13s-9-6-9-13a9 9 0 0 1 18 0Z" />
      <circle cx="12" cy="10" r="3" />
    </svg>
  )
}
function MailIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="2" y="4" width="20" height="16" rx="2" />
      <path d="m22 7-10 6L2 7" />
    </svg>
  )
}
function PhoneIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72c.13.96.36 1.9.7 2.81a2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45c.91.34 1.85.57 2.81.7A2 2 0 0 1 22 16.92Z" />
    </svg>
  )
}
function LinkIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
      <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
    </svg>
  )
}
function CalendarIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3" y="4" width="18" height="18" rx="2" />
      <path d="M16 2v4M8 2v4M3 10h18" />
    </svg>
  )
}
function PoundIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M18 7c0-2.2-1.8-4-4-4S10 4.8 10 7v3m-3 0h7M7 21h11c-2 0-3.5-1.5-3.5-3.5V13" />
    </svg>
  )
}
function StatusDot({ status }: { status: string }) {
  const color = status === 'done' ? 'bg-green-600' : status === 'failed' ? 'bg-danger' : status === 'cancelled' ? 'bg-ink-mute' : 'bg-accent'
  return <span className={`h-2 w-2 shrink-0 rounded-full ${color}`} />
}
function BackIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
      <line x1="19" y1="12" x2="5" y2="12" />
      <polyline points="12 19 5 12 12 5" />
    </svg>
  )
}
function ChevronIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
      <polyline points="9 18 15 12 9 6" />
    </svg>
  )
}
function ThumbIcon({ up }: { up?: boolean }) {
  return (
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={up ? undefined : { transform: 'rotate(180deg)' }}>
      <path d="M7 10v12" />
      <path d="M15 5.88 14 10h5.83a2 2 0 0 1 1.92 2.56l-2.33 8A2 2 0 0 1 17.5 22H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h2.76a2 2 0 0 0 1.79-1.11L12 2a3.13 3.13 0 0 1 3 3.88Z" />
    </svg>
  )
}
function ArrowUp() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
      <line x1="12" y1="19" x2="12" y2="5" />
      <polyline points="5 12 12 5 19 12" />
    </svg>
  )
}
function PlusIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
      <line x1="12" y1="5" x2="12" y2="19" />
      <line x1="5" y1="12" x2="19" y2="12" />
    </svg>
  )
}
function TrashIcon() {
  return (
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 6h18M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2M19 6l-1 14a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1L5 6" />
    </svg>
  )
}

function fileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function PaperclipIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M21.44 11.05l-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48" />
    </svg>
  )
}
function MicIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="9" y="2" width="6" height="12" rx="3" />
      <path d="M5 10a7 7 0 0 0 14 0" />
      <line x1="12" y1="19" x2="12" y2="22" />
    </svg>
  )
}
function PlayIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
      <path d="M8 5v14l11-7z" />
    </svg>
  )
}
function PauseIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
      <rect x="6" y="5" width="4" height="14" rx="1" />
      <rect x="14" y="5" width="4" height="14" rx="1" />
    </svg>
  )
}
function XIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
      <line x1="6" y1="6" x2="18" y2="18" />
      <line x1="18" y1="6" x2="6" y2="18" />
    </svg>
  )
}
function CopyIcon() {
  return (
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="9" y="9" width="13" height="13" rx="2" />
      <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
    </svg>
  )
}
function CheckIcon() {
  return (
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
      <polyline points="20 6 9 17 4 12" />
    </svg>
  )
}
