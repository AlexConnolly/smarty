import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import {
  answerTask,
  cancelTask,
  fetchProject,
  fetchProjects,
  openSessionStream,
  pinSessionToProject,
  sendFeedback,
  sendMessage,
  transcribe,
  resolveGate,
  type ProjectDetail,
  type ProjectMemory,
  type ProjectRun,
  type ProjectSummary,
  type RunStep,
  type SessionHandlers,
  type WorkerQuestion,
  type GateRequest,
} from './api'
import { formatDuration, toWav16k, type RecordedAudio } from './audio'
import CommandCenter from './CommandCenter'

interface AudioNote {
  peaks: number[]
  duration: number
  url?: string
}

interface UiMessage {
  id: number | string
  role: 'user' | 'assistant' | 'progress_digest'
  content: string
  reasoning: string
  streaming: boolean
  audio?: AudioNote
  thinkStart?: number
  thinkMs?: number
  digestItems?: { id: string; description: string; summary: string }[]
}

type Working = { id: string; task: string; startedAt: number }

const SESSION_KEY = 'smarty-session-id'
const VIEW_KEY = 'smarty-view'
const REC_BARS = 56
const EXAMPLES = ['Plan a weekend in Lisbon', "What's the latest tech news?", 'Remember I live in London']

function getSessionId(): string {
  try {
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

function greetingText(): string {
  const h = new Date().getHours()
  return h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening'
}

function initialView(): 'home' | 'chat' | 'settings' {
  try {
    const v = localStorage.getItem(VIEW_KEY)
    return v === 'chat' ? 'chat' : v === 'settings' ? 'settings' : 'home'
  } catch {
    return 'home'
  }
}

export default function App() {
  // Remember whether you were on the home or in the conversation, so a refresh lands you back where you were.
  const [view, setView] = useState<'home' | 'chat' | 'settings'>(initialView)
  const [messages, setMessages] = useState<UiMessage[]>([])
  const [working, setWorking] = useState<Working[]>([])
  // Workers that paused to ask the user something. They sit "in your face" until answered.
  const [questions, setQuestions] = useState<WorkerQuestion[]>([])
  // Permission gate requests from workers.
  const [gates, setGates] = useState<GateRequest[]>([])
  const [tasksOpen, setTasksOpen] = useState(false)
  const [input, setInput] = useState('')
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [projects, setProjects] = useState<ProjectSummary[]>([])
  const [activeProject, setActiveProject] = useState<ProjectDetail | null>(null)
  const [projectLoading, setProjectLoading] = useState(false)
  const [recording, setRecording] = useState(false)
  const [recPeaks, setRecPeaks] = useState<number[]>([])
  const [recSeconds, setRecSeconds] = useState(0)
  const [, setNow] = useState(Date.now())

  const sessionId = useRef(getSessionId())
  const greeting = useRef(greetingText())
  const scrollRef = useRef<HTMLDivElement>(null)
  const taRef = useRef<HTMLTextAreaElement>(null)
  const atBottomRef = useRef(true)
  const recRef = useRef<{ mr: MediaRecorder; ctx: AudioContext; sampler: number } | null>(null)
  const cancelledRef = useRef(false)
  const pendingAudio = useRef<AudioNote | null>(null)

  const refreshProjects = () => fetchProjects().then(setProjects)

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

  // The single persistent connection to the session — everything the assistant says (and results pushed
  // back from background workers) arrives here, whichever view you're looking at.
  useEffect(() => {
    const controller = new AbortController()
    const handlers: SessionHandlers = {
      onMsgStart: (id, role) => {
        const audio = role === 'user' && pendingAudio.current ? pendingAudio.current : undefined
        if (audio) pendingAudio.current = null
        upsert(id, (m) => ({ ...m, role: role as 'user' | 'assistant', streaming: true, audio: audio ?? m.audio }), role as 'user' | 'assistant')
      },
      onContent: (id, text) =>
        upsert(id, (m) => ({ ...m, content: m.content + text, thinkMs: m.thinkMs ?? (m.thinkStart ? Date.now() - m.thinkStart : undefined) })),
      onReasoning: (id, text) => upsert(id, (m) => ({ ...m, reasoning: m.reasoning + text, thinkStart: m.thinkStart ?? Date.now() })),
      onMsgEnd: (id, text) =>
        upsert(id, (m) => ({
          ...m,
          streaming: false,
          content: text && text.length > 0 ? text : m.content,
          thinkMs: m.thinkMs ?? (m.thinkStart ? Date.now() - m.thinkStart : undefined),
        })),
      onWorking: (id, task) => {
        // Resuming (or starting) clears any pending question or gate for this task.
        setQuestions((q) => q.filter((x) => x.taskId !== id))
        setGates((g) => g.filter((x) => x.taskId !== id))
        setWorking((w) => (w.some((x) => x.id === id) ? w : [...w, { id, task, startedAt: Date.now() }]))
      },
      onWorkingDone: (id) => setWorking((w) => w.filter((x) => x.id !== id)),
      onQuestion: (q) => setQuestions((prev) => (prev.some((x) => x.taskId === q.taskId) ? prev : [...prev, q])),
      onGateRequest: (g) => setGates((prev) => (prev.some((x) => x.gateRequestId === g.gateRequestId) ? prev : [...prev, g])),
      onGateResolved: (_, gateRequestId) => setGates((prev) => prev.filter((x) => x.gateRequestId !== gateRequestId)),
      onTaskProgressDigest: (digest) => {
        setMessages((prev) => {
          if (prev.some((m) => m.id === digest.id)) return prev
          return [
            ...prev,
            {
              id: digest.id,
              role: 'progress_digest',
              content: digest.text,
              reasoning: '',
              streaming: false,
              digestItems: digest.items,
            }
          ]
        })
      },
    }
    openSessionStream(sessionId.current, handlers, controller.signal)
    refreshProjects()
    return () => controller.abort()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Deep linking: open whatever project the URL points at on load, and keep in sync with back/forward.
  useEffect(() => {
    const initial = projectFromPath()
    if (initial) openProject(initial, false)
    const onPop = () => {
      const slug = projectFromPath()
      if (slug) openProject(slug, false)
      else closeProject(false)
    }
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Tick once a second while anything is running, so elapsed timers stay live.
  useEffect(() => {
    if (working.length === 0) return
    const t = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(t)
  }, [working.length])

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

  useEffect(() => {
    try {
      localStorage.setItem(VIEW_KEY, view)
    } catch {
      /* ignore */
    }
  }, [view])

  function goHome() {
    setView('home')
    refreshProjects()
  }
  function openDrawer() {
    setDrawerOpen(true)
    refreshProjects()
  }
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

  // Sending anything (typed or voice) drops you into the conversation.
  function send(text?: string) {
    const body = (text ?? input).trim()
    if (!body) return
    setInput('')
    setView('chat')
    atBottomRef.current = true
    sendMessage(sessionId.current, body)
  }

  async function sendVoice(rec: RecordedAudio) {
    let text = ''
    try {
      text = await transcribe(rec.wav)
    } catch {
      /* ignore */
    }
    if (!text) return
    pendingAudio.current = { peaks: rec.peaks, duration: rec.duration, url: URL.createObjectURL(rec.wav) }
    setView('chat')
    atBottomRef.current = true
    sendMessage(sessionId.current, text)
  }

  function newChat() {
    const id = crypto.randomUUID()
    sessionId.current = id
    try {
      localStorage.setItem(SESSION_KEY, id)
    } catch {
      /* ignore */
    }
    setMessages([])
    setWorking([])
    try {
      localStorage.setItem(VIEW_KEY, 'home') // a fresh chat starts you back at the home
    } catch {
      /* ignore */
    }
    window.location.reload()
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
  function answerQuestion(taskId: string, text: string) {
    const body = text.trim()
    if (!body) return
    setQuestions((q) => q.filter((x) => x.taskId !== taskId))
    answerTask(sessionId.current, taskId, body).catch(() => {})
  }

  function handleResolveGate(taskId: string, gateRequestId: string, approved: boolean, rememberForTask = false) {
    setGates((g) => g.filter((x) => x.gateRequestId !== gateRequestId))
    resolveGate(sessionId.current, taskId, gateRequestId, approved, rememberForTask).catch(() => {})
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
      if (e.data.size) chunks.push(e.data)
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
    mr.start()
    setRecording(true)
  }
  const stopRecording = () => recRef.current?.mr.stop()
  const cancelRecording = () => {
    cancelledRef.current = true
    recRef.current?.mr.stop()
  }

  return (
    <div className="flex h-full flex-col overflow-x-hidden bg-bg text-ink">
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
          <span className="text-[15px] font-semibold tracking-tight text-ink">Smarty</span>
        </button>
        <div className="ml-auto flex items-center gap-1.5">
          {working.length > 0 && <RunningPill count={working.length} onOpen={() => setTasksOpen(true)} />}
          <button
            onClick={() => setView('settings')}
            className={`rounded-md px-2.5 py-1.5 text-xs font-medium transition ${
              view === 'settings'
                ? 'bg-surface-mid text-ink font-semibold'
                : 'text-ink-soft hover:bg-surface-mid hover:text-ink'
            }`}
          >
            Command Centre
          </button>
          <button onClick={newChat} className="rounded-md px-2.5 py-1.5 text-xs font-medium text-ink-soft transition hover:bg-surface-mid hover:text-ink">
            New chat
          </button>
        </div>
      </header>

      <main ref={scrollRef} onScroll={onScroll} className="flex-1 overflow-y-auto">
        {view === 'settings' ? (
          <CommandCenter onClose={() => setView('home')} />
        ) : view === 'home' ? (
          <HomeView
            greeting={greeting.current}
            working={working}
            questions={questions}
            gates={gates}
            projects={projects}
            onOpenTasks={() => setTasksOpen(true)}
            onOpenProject={openProject}
            onPick={(p) => send(p)}
            onAnswer={answerQuestion}
            onResolveGate={handleResolveGate}
          />
        ) : (
          <div className="mx-auto max-w-reading px-4 py-6">
            <div className="space-y-6">
              {messages.map((m) => (
                <MessageRow key={m.id} message={m} sessionId={sessionId.current} />
              ))}
              {gates.map((g) => (
                <GateCard key={g.gateRequestId} g={g} onResolve={handleResolveGate} />
              ))}
              {questions.map((q) => (
                <QuestionCard key={q.taskId} q={q} onAnswer={answerQuestion} />
              ))}
            </div>
          </div>
        )}
      </main>

      {view !== 'settings' && (
        <footer className="border-t border-line bg-bg/80 px-4 py-3 backdrop-blur">
          <div className="mx-auto flex max-w-reading items-end gap-2">
            {recording ? (
              <div className="flex flex-1 items-center gap-3 rounded-xl border border-danger/30 bg-danger/[0.04] px-3 py-2.5">
                <span className="h-2.5 w-2.5 shrink-0 animate-pulse rounded-full bg-danger" />
                <span className="shrink-0 font-mono text-xs text-danger">{formatDuration(recSeconds)}</span>
                <div className="min-w-0 flex-1 overflow-hidden">
                  <Waveform peaks={recPeaks} progress={1} idleClass="bg-danger/40" activeClass="bg-danger/60" />
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
                    placeholder="Message Smarty…"
                    className="max-h-52 flex-1 resize-none bg-transparent py-1 text-[15px] leading-relaxed text-ink outline-none placeholder:text-ink-mute"
                  />
                </div>
                <button
                  onClick={() => send()}
                  disabled={!input.trim()}
                  title="Send"
                  className="grid h-11 w-11 shrink-0 place-items-center rounded-full bg-accent text-on-accent shadow-ambient transition enabled:hover:brightness-110 disabled:opacity-30"
                >
                  <ArrowUp />
                </button>
              </>
            )}
          </div>
        </footer>
      )}

      <ProjectsDrawer open={drawerOpen} projects={projects} onClose={() => setDrawerOpen(false)} onPick={openProject} />

      {(projectLoading || activeProject) && (
        <ProjectOverview project={activeProject} loading={projectLoading} onClose={() => closeProject()} mainSessionId={sessionId.current} />
      )}

      {tasksOpen && <TaskRunner tasks={working} onClose={() => setTasksOpen(false)} onCancel={cancelRunningTask} />}
    </div>
  )
}

// ---- Home: a calm "here's what's going on", with the quick chat box living in the shared footer ----
function HomeView({
  greeting,
  working,
  questions,
  gates,
  projects,
  onOpenTasks,
  onOpenProject,
  onPick,
  onAnswer,
  onResolveGate,
}: {
  greeting: string
  working: Working[]
  questions: WorkerQuestion[]
  gates: GateRequest[]
  projects: ProjectSummary[]
  onOpenTasks: () => void
  onOpenProject: (slug: string) => void
  onPick: (prompt: string) => void
  onAnswer: (taskId: string, text: string) => void
  onResolveGate: (taskId: string, gateRequestId: string, approved: boolean, rememberForTask?: boolean) => void
}) {
  const nothing = working.length === 0 && projects.length === 0 && questions.length === 0 && gates.length === 0
  return (
    <div className="mx-auto max-w-reading px-4 py-10 sm:py-14">
      <h1 className="animate-fade-in-up text-[26px] font-semibold tracking-tight text-ink sm:text-[32px]">{greeting}</h1>
      <p className="animate-fade-in-up text-[15px] text-ink-soft" style={{ animationDelay: '40ms' }}>
        {nothing ? 'What can I help you with?' : "Here's what's going on."}
      </p>

      {gates.length > 0 && (
        <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '50ms' }}>
          <SectionLabel>Permission Required</SectionLabel>
          <div className="mt-2.5 space-y-2.5">
            {gates.map((g) => (
              <GateCard key={g.gateRequestId} g={g} onResolve={onResolveGate} />
            ))}
          </div>
        </section>
      )}

      {questions.length > 0 && (
        <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '60ms' }}>
          <SectionLabel>Awaiting your answer</SectionLabel>
          <div className="mt-2.5 space-y-2.5">
            {questions.map((q) => (
              <QuestionCard key={q.taskId} q={q} onAnswer={onAnswer} />
            ))}
          </div>
        </section>
      )}

      {working.length > 0 && (
        <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '80ms' }}>
          <SectionLabel>Running now</SectionLabel>
          <div className="mt-2.5 space-y-2">
            {working.map((t) => (
              <button
                key={t.id}
                onClick={onOpenTasks}
                className="block w-full overflow-hidden rounded-lg border border-line bg-surface text-left shadow-card transition hover:bg-surface-low"
              >
                <div className="progress-line h-0.5 w-full" />
                <div className="flex items-center gap-3 px-4 py-3">
                  <span className="min-w-0 flex-1 truncate text-sm text-ink">{t.task}</span>
                  <span className="shrink-0 font-mono text-xs text-ink-mute">{elapsed(t.startedAt)}</span>
                </div>
              </button>
            ))}
          </div>
        </section>
      )}

      {projects.length > 0 && (
        <section className="mt-8 animate-fade-in-up" style={{ animationDelay: '120ms' }}>
          <SectionLabel>Projects</SectionLabel>
          <div className="mt-2.5 space-y-2">
            {projects.map((p) => (
              <button
                key={p.slug}
                onClick={() => onOpenProject(p.slug)}
                className="block w-full rounded-lg border border-line bg-surface px-4 py-3 text-left shadow-card transition hover:bg-surface-low"
              >
                <div className="flex items-center gap-2">
                  <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-accent" />
                  <span className="truncate text-[15px] font-medium text-ink">{p.title}</span>
                </div>
                {p.description && <div className="mt-0.5 line-clamp-1 pl-3.5 text-sm text-ink-soft">{p.description}</div>}
              </button>
            ))}
          </div>
        </section>
      )}

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
  return <div className="text-[11px] font-semibold uppercase tracking-wider text-ink-mute">{children}</div>
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
  onClose,
  onPick,
}: {
  open: boolean
  projects: ProjectSummary[]
  onClose: () => void
  onPick: (slug: string) => void
}) {
  return (
    <>
      <div
        className={`fixed inset-0 z-30 bg-ink/20 transition-opacity ${open ? 'opacity-100' : 'pointer-events-none opacity-0'}`}
        onClick={onClose}
      />
      <aside
        className={`fixed left-0 top-0 z-40 flex h-full w-80 max-w-[85vw] flex-col border-r border-line bg-surface shadow-ambient transition-transform duration-200 ${
          open ? 'translate-x-0' : '-translate-x-full'
        }`}
        aria-hidden={!open}
      >
        <div className="flex items-center justify-between border-b border-line px-4 py-3.5">
          <div className="text-sm font-semibold text-ink">Projects</div>
          <button onClick={onClose} className="grid h-8 w-8 place-items-center rounded-md text-ink-soft hover:bg-surface-mid hover:text-ink">
            <XIcon />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto p-2">
          {projects.length === 0 ? (
            <div className="px-3 py-8 text-center text-sm text-ink-mute">
              No projects yet. They appear here once you start planning something ongoing.
            </div>
          ) : (
            projects.map((p) => (
              <button key={p.slug} onClick={() => onPick(p.slug)} className="mb-0.5 w-full rounded-md px-3 py-2.5 text-left transition hover:bg-surface-low">
                <div className="truncate text-sm font-medium text-ink">{p.title}</div>
                {p.description && <div className="mt-0.5 line-clamp-2 text-xs text-ink-soft">{p.description}</div>}
              </button>
            ))
          )}
        </div>
      </aside>
    </>
  )
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
        <h1 className="text-[24px] font-semibold tracking-tight text-white drop-shadow-sm sm:text-[28px]">{project.title}</h1>
        {project.description && <p className="mt-0.5 line-clamp-1 text-sm text-white/85">{project.description}</p>}
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
  const [messages, setMessages] = useState<UiMessage[]>([])
  const [questions, setQuestions] = useState<WorkerQuestion[]>([])
  const [gates, setGates] = useState<GateRequest[]>([])
  const [input, setInput] = useState('')
  const runs = project?.runs ?? []
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
        onWorking: (id) => {
          setQuestions((q) => q.filter((x) => x.taskId !== id))
          setGates((g) => g.filter((x) => x.taskId !== id))
        },
        onQuestion: (q) => setQuestions((prev) => (prev.some((x) => x.taskId === q.taskId) ? prev : [...prev, q])),
        onGateRequest: (g) => setGates((prev) => (prev.some((x) => x.gateRequestId === g.gateRequestId) ? prev : [...prev, g])),
        onGateResolved: (_, gateRequestId) => setGates((prev) => prev.filter((x) => x.gateRequestId !== gateRequestId)),
        onTaskProgressDigest: (digest) => {
          setMessages((prev) => {
            if (prev.some((m) => m.id === digest.id)) return prev
            return [
              ...prev,
              {
                id: digest.id,
                role: 'progress_digest',
                content: digest.text,
                reasoning: '',
                streaming: false,
                digestItems: digest.items,
              }
            ]
          })
        },
      },
      controller.signal,
    )
    return () => controller.abort()
  }, [projSession, slug])

  function answerQuestion(taskId: string, text: string) {
    const body = text.trim()
    if (!body || !projSession) return
    setQuestions((q) => q.filter((x) => x.taskId !== taskId))
    setTab('chat')
    answerTask(projSession, taskId, body).catch(() => {})
  }

  function handleResolveGate(taskId: string, gateRequestId: string, approved: boolean, rememberForTask = false) {
    if (!projSession) return
    setGates((g) => g.filter((x) => x.gateRequestId !== gateRequestId))
    setTab('chat')
    resolveGate(projSession, taskId, gateRequestId, approved, rememberForTask).catch(() => {})
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
              {project.summary && <p className="mt-4 text-[16px] leading-relaxed text-ink-soft">{project.summary}</p>}
            </div>

            {project.memories.length === 0 && runs.length === 0 && (
              <p className="text-sm text-ink-mute">Nothing tracked yet — details will show up as things get sorted out.</p>
            )}

            {project.memories.length > 0 && (
              <section className="space-y-2.5">
                {orderedFacts(project.memories).map((m, i) => (
                  <FactCard key={i} fact={m} />
                ))}
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
            {messages.length === 0 && questions.length === 0 && gates.length === 0 ? (
              <div className="pt-16 text-center text-sm text-ink-mute">Ask me anything about {project.title} — I'll keep to this project.</div>
            ) : (
              <div className="space-y-6">
                {messages.map((m) => (
                  <MessageRow key={m.id} message={m} sessionId={projSession ?? ''} />
                ))}
                {gates.map((g) => (
                  <GateCard key={g.gateRequestId} g={g} onResolve={handleResolveGate} />
                ))}
                {questions.map((q) => (
                  <QuestionCard key={q.taskId} q={q} onAnswer={answerQuestion} />
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
                className="max-h-40 flex-1 resize-none bg-transparent py-1 text-[15px] leading-relaxed text-ink outline-none placeholder:text-ink-mute"
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
        <span className="shrink-0 font-mono text-[11px] text-ink-mute">{relTime(run.startedAt)}</span>
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
              <div className="mb-1 text-[10px] font-semibold uppercase tracking-wider text-ink-mute">Result</div>
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
            <pre className="mt-1 max-h-40 overflow-y-auto whitespace-pre-wrap rounded-md bg-surface-low px-2.5 py-1.5 font-mono text-[11px] leading-relaxed text-ink-soft">
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

function FactCard({ fact }: { fact: ProjectMemory }) {
  const kind = classifyFact(fact)
  const label = fact.key.replace(/[_-]+/g, ' ')
  const ctx = fact.context ? <div className="mt-0.5 text-sm text-ink-mute">{fact.context}</div> : null

  if (kind === 'location') {
    const q = encodeURIComponent(fact.value)
    return (
      <FactShell label={label} icon={<PinIcon />}>
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
      <FactShell label={label} icon={<MailIcon />}>
        <a href={`mailto:${fact.value}`} className="break-all text-lg font-medium text-accent hover:brightness-90">{fact.value}</a>
        {ctx}
      </FactShell>
    )
  if (kind === 'phone')
    return (
      <FactShell label={label} icon={<PhoneIcon />}>
        <a href={`tel:${fact.value.replace(/[^\d+]/g, '')}`} className="text-lg font-medium text-accent hover:brightness-90">{fact.value}</a>
        {ctx}
      </FactShell>
    )
  if (kind === 'url')
    return (
      <FactShell label={label} icon={<LinkIcon />}>
        <a href={fact.value} target="_blank" rel="noreferrer" className="break-all text-[15px] font-medium text-accent hover:brightness-90">{fact.value}</a>
        {ctx}
      </FactShell>
    )
  if (kind === 'date')
    return (
      <FactShell label={label} icon={<CalendarIcon />}>
        <div className="text-lg font-medium text-ink">{fact.value}</div>
        {ctx}
      </FactShell>
    )
  if (kind === 'money')
    return (
      <FactShell label={label} icon={<PoundIcon />}>
        <div className="text-lg font-medium text-ink">{fact.value}</div>
        {ctx}
      </FactShell>
    )
  if (kind === 'person') {
    const initial = (fact.value || '?').trim().charAt(0).toUpperCase()
    return (
      <FactShell label={label} icon={<span className="grid h-5 w-5 place-items-center rounded-full bg-accent text-[10px] font-bold text-on-accent">{initial}</span>}>
        <div className="text-lg font-medium text-ink">{fact.value}</div>
        {ctx}
      </FactShell>
    )
  }
  return (
    <FactShell label={label}>
      <div className="text-lg font-medium text-ink">{fact.value}</div>
      {ctx}
    </FactShell>
  )
}

function FactShell({ label, icon, children }: { label: string; icon?: ReactNode; children: ReactNode }) {
  return (
    <div className="rounded-lg border border-line bg-surface px-4 py-3.5 shadow-card">
      <div className="flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider text-accent">
        {icon}
        {label}
      </div>
      <div className="mt-1.5">{children}</div>
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
          <h2 className="text-[22px] font-semibold tracking-tight text-ink">Working on it</h2>
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
                      <div className="text-[11px] font-semibold uppercase tracking-wider text-accent">Task #{task.id}</div>
                      <div className="font-mono text-xs text-ink-mute">{elapsed(task.startedAt)}</div>
                    </div>
                    <div className="mt-1.5 text-[15px] leading-relaxed text-ink">{task.task}</div>
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

function elapsed(startedAt: number): string {
  const s = Math.max(0, Math.floor((Date.now() - startedAt) / 1000))
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  return `${m}m ${s % 60}s`
}

// ---- chat messages ----
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

  if (message.role === 'progress_digest') {
    return (
      <div className="flex gap-3 animate-fade-in-up items-start">
        <div className="relative flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-accent/10 text-accent">
          <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-accent/10 opacity-75"></span>
          <svg className="h-4 w-4 animate-spin text-accent" fill="none" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
          </svg>
        </div>
        <div className="min-w-0 flex-1 rounded-2xl bg-surface-low/60 backdrop-blur-sm border border-surface-mid/80 px-4 py-3.5 shadow-sm space-y-2">
          <div className="flex items-center gap-2 text-xs font-semibold tracking-wider uppercase text-ink-soft/90">
            <span>Still working</span>
            <span className="h-1 w-1 rounded-full bg-accent animate-pulse"></span>
            <span className="text-[10px] text-ink-mute font-normal normal-case ml-auto">Heartbeat Update</span>
          </div>
          <div className="space-y-1.5">
            {message.digestItems?.map((item, idx) => (
              <div key={idx} className="text-[14px] leading-relaxed text-ink-soft flex items-start gap-2">
                <span className="text-accent/80 font-medium select-none mt-[3px] text-xs">Task #{item.id}:</span>
                <span className="flex-1">{item.summary}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    )
  }

  if (message.role === 'user') {
    if (message.audio) return <VoiceBubble message={message} />
    return (
      <div className="flex justify-end animate-fade-in-up">
        <div className="max-w-[85%] whitespace-pre-wrap rounded-2xl rounded-br-md bg-surface-low px-4 py-2.5 text-[15px] leading-relaxed text-ink">
          {message.content}
        </div>
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
          <div className="text-[15px] leading-relaxed text-ink">
            <Markdown text={message.content} />
          </div>
        ) : message.streaming && !message.reasoning ? (
          <TypingDots />
        ) : null}
        {message.content && !message.streaming && (
          <div className="flex items-center gap-1 pt-1">
            <CopyButton text={message.content} />
            <FeedbackButtons sessionId={sessionId} messageId={message.id as number} />
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
          <span className="shrink-0 font-mono text-[11px] text-ink-mute">{formatDuration(audio.duration)}</span>
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

function Markdown({ text }: { text: string }) {
  return (
    <div className="prose prose-sm max-w-none prose-p:my-2 prose-headings:text-ink prose-p:text-ink prose-li:my-0.5 prose-li:text-ink prose-strong:text-ink prose-a:text-accent prose-pre:my-2 prose-pre:border prose-pre:border-line prose-pre:bg-surface-low prose-code:text-ink prose-code:before:content-none prose-code:after:content-none">
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={{ a: (props) => <a {...props} target="_blank" rel="noreferrer noopener" /> }}>
        {text}
      </ReactMarkdown>
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
    <button onClick={copy} className="inline-flex items-center gap-1.5 rounded-md px-1.5 py-1 text-[11px] text-ink-mute transition hover:bg-surface-mid hover:text-ink-soft" title="Copy">
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
function QuestionCard({ q, onAnswer }: { q: WorkerQuestion; onAnswer: (taskId: string, text: string) => void }) {
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
        <div className="flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider text-accent">
          <QuestionIcon />
          Needs your answer
        </div>
        <div className="mt-1.5 text-[15px] leading-relaxed text-ink">{q.question}</div>
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
function ShieldAlertIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" />
      <line x1="12" y1="8" x2="12" y2="12" />
      <line x1="12" y1="16" x2="12.01" y2="16" />
    </svg>
  )
}

// ---- interactive worker gate ----
function GateCard({ g, onResolve }: { g: GateRequest; onResolve: (taskId: string, gateRequestId: string, approved: boolean, rememberForTask?: boolean) => void }) {
  return (
    <div className="overflow-hidden rounded-xl border border-danger/30 bg-danger/[0.04] shadow-card animate-fade-in-up">
      <div className="px-4 py-3.5">
        <div className="flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider text-danger">
          <ShieldAlertIcon />
          Permission Required
        </div>
        <div className="mt-1.5 text-[15px] leading-relaxed text-ink">
          Smarty needs permission to perform the following action in task *#{g.taskId}*:
          <div className="mt-2.5 rounded-lg bg-surface/50 p-3 font-mono text-xs text-ink-soft border border-line overflow-x-auto whitespace-pre-wrap break-all">
            <span className="font-semibold text-ink">Action:</span> {g.action}
            <br />
            <span className="font-semibold text-ink">Details:</span> {g.description}
          </div>
        </div>
        <div className="mt-3.5 flex flex-wrap gap-2">
          <button
            onClick={() => onResolve(g.taskId, g.gateRequestId, true, true)}
            className="rounded-lg bg-danger text-white px-4 py-2 text-sm font-medium transition hover:brightness-110"
          >
            Approve for task
          </button>
          <button
            onClick={() => onResolve(g.taskId, g.gateRequestId, true, false)}
            className="rounded-lg border border-danger/40 bg-surface px-4 py-2 text-sm font-medium text-danger transition hover:bg-danger/[0.06]"
          >
            Approve once
          </button>
          <button
            onClick={() => onResolve(g.taskId, g.gateRequestId, false)}
            className="rounded-lg border border-line bg-surface px-4 py-2 text-sm text-ink transition hover:bg-surface-low"
          >
            Deny
          </button>
        </div>
      </div>
    </div>
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
