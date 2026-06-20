import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import {
  cancelTask,
  fetchProject,
  fetchProjects,
  openSessionStream,
  sendFeedback,
  sendMessage,
  transcribe,
  type ProjectDetail,
  type ProjectRun,
  type ProjectSummary,
  type RunStep,
  type SessionHandlers,
} from './api'
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
}

const SESSION_KEY = 'smarty-session-id'
const REC_BARS = 56

const EXAMPLES = ['What is the current system status?', "What's the latest news?", 'How much disk space is free?']

function getSessionId(): string {
  try {
    // ?s=<id> opens a specific session (handy for reopening or sharing a conversation).
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

export default function App() {
  const [messages, setMessages] = useState<UiMessage[]>([])
  const [working, setWorking] = useState<{ id: string; task: string }[]>([])
  const [tasksOpen, setTasksOpen] = useState(false)
  const [input, setInput] = useState('')
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [projects, setProjects] = useState<ProjectSummary[]>([])
  const [activeProject, setActiveProject] = useState<ProjectDetail | null>(null)
  const [projectLoading, setProjectLoading] = useState(false)
  const [recording, setRecording] = useState(false)
  const [recPeaks, setRecPeaks] = useState<number[]>([])
  const [recSeconds, setRecSeconds] = useState(0)

  const sessionId = useRef(getSessionId())
  const scrollRef = useRef<HTMLDivElement>(null)
  const taRef = useRef<HTMLTextAreaElement>(null)
  const atBottomRef = useRef(true)
  const recRef = useRef<{ mr: MediaRecorder; ctx: AudioContext; sampler: number } | null>(null)
  const cancelledRef = useRef(false)
  const pendingAudio = useRef<AudioNote | null>(null)

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

  // The single persistent connection to the session. Everything the assistant says arrives here.
  useEffect(() => {
    const controller = new AbortController()
    const handlers: SessionHandlers = {
      onMsgStart: (id, role) => {
        const audio = role === 'user' && pendingAudio.current ? pendingAudio.current : undefined
        if (audio) pendingAudio.current = null
        upsert(id, (m) => ({ ...m, role: role as 'user' | 'assistant', streaming: true, audio: audio ?? m.audio }), role as 'user' | 'assistant')
      },
      onContent: (id, text) => upsert(id, (m) => ({ ...m, content: m.content + text })),
      onReasoning: (id, text) => upsert(id, (m) => ({ ...m, reasoning: m.reasoning + text })),
      // Snap to the server's authoritative full text — heals any delta dropped during live streaming.
      onMsgEnd: (id, text) =>
        upsert(id, (m) => ({ ...m, streaming: false, content: text && text.length > 0 ? text : m.content })),
      onWorking: (id, task) => setWorking((w) => (w.some((x) => x.id === id) ? w : [...w, { id, task }])),
      onWorkingDone: (id) => setWorking((w) => w.filter((x) => x.id !== id)),
    }
    openSessionStream(sessionId.current, handlers, controller.signal)
    return () => controller.abort()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  function onScroll() {
    const el = scrollRef.current
    if (!el) return
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80
  }
  useLayoutEffect(() => {
    const el = scrollRef.current
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight
  }, [messages, working])

  useEffect(() => {
    const el = taRef.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = Math.min(Math.max(el.scrollHeight, 64), 220) + 'px'
  }, [input])

  useEffect(() => {
    if (working.length === 0) setTasksOpen(false)
  }, [working.length])

  function openDrawer() {
    setDrawerOpen(true)
    fetchProjects().then(setProjects)
  }

  async function openProject(slug: string) {
    setDrawerOpen(false)
    setProjectLoading(true)
    setActiveProject(null)
    const detail = await fetchProject(slug)
    setActiveProject(detail)
    setProjectLoading(false)
  }

  function send(text?: string) {
    const body = (text ?? input).trim()
    if (!body) return
    setInput('')
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
    // restart the stream on the new session
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
    <div className="flex h-full flex-col overflow-x-hidden bg-slate-950 text-slate-100">
      <header className="relative flex items-center gap-3 border-b border-white/5 bg-slate-900/60 px-4 py-2.5 backdrop-blur">
        <button
          onClick={openDrawer}
          title="Projects"
          aria-label="Open projects"
          className="group grid h-7 w-7 place-items-center rounded-lg bg-gradient-to-br from-indigo-500 to-violet-600 text-sm font-bold text-white shadow-lg shadow-indigo-500/20 transition hover:brightness-110"
        >
          S
        </button>
        <h1 className="text-sm font-semibold tracking-tight">Smarty</h1>
        <span className="hidden text-xs text-slate-500 sm:inline">personal assistant</span>
        <TaskPill
          tasks={working}
          open={tasksOpen}
          onToggle={() => setTasksOpen((open) => !open)}
          onClose={() => setTasksOpen(false)}
          onCancel={cancelRunningTask}
        />
        <button
          onClick={newChat}
          className="rounded-lg border border-white/10 px-2.5 py-1.5 text-xs text-slate-300 hover:bg-white/5"
        >
          New chat
        </button>
      </header>

      <main ref={scrollRef} onScroll={onScroll} className="flex-1 overflow-y-auto">
        <div className="mx-auto max-w-3xl px-4 py-6">
          {messages.length === 0 ? (
            <Empty onPick={(p) => send(p)} />
          ) : (
            <div className="space-y-5">
              {messages.map((m) => (
                <MessageRow key={m.id} message={m} sessionId={sessionId.current} />
              ))}
            </div>
          )}
        </div>
      </main>

      <footer className="border-t border-white/5 bg-slate-900/60 px-4 py-3 backdrop-blur">
        <div className="mx-auto flex max-w-3xl items-end gap-2">
          {recording ? (
            <div className="flex flex-1 items-center gap-3 rounded-2xl border border-rose-400/40 bg-rose-500/10 px-3 py-2.5">
              <span className="h-2.5 w-2.5 shrink-0 animate-pulse rounded-full bg-rose-500" />
              <span className="shrink-0 text-xs tabular-nums text-rose-200">{formatDuration(recSeconds)}</span>
              <div className="min-w-0 flex-1 overflow-hidden">
                <Waveform peaks={recPeaks} progress={1} idleClass="bg-rose-300/70" activeClass="bg-rose-300/70" />
              </div>
              <button onClick={cancelRecording} title="Cancel" className="grid h-9 w-9 shrink-0 place-items-center rounded-xl text-slate-300 hover:bg-white/10">
                <XIcon />
              </button>
              <button onClick={stopRecording} title="Send voice note" className="grid h-9 w-9 shrink-0 place-items-center rounded-xl bg-gradient-to-br from-indigo-500 to-violet-600 text-white hover:brightness-110">
                <ArrowUp />
              </button>
            </div>
          ) : (
            <>
              <div className="flex flex-1 items-end rounded-2xl border border-white/10 bg-white/5 px-3.5 py-2.5 focus-within:border-indigo-400/60">
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
                  rows={2}
                  placeholder="Message your assistant…"
                  className="max-h-56 flex-1 resize-none bg-transparent text-[15px] leading-relaxed text-slate-100 outline-none placeholder:text-slate-500"
                />
              </div>
              {input.trim() ? (
                <button onClick={() => send()} title="Send" className="grid h-11 w-11 shrink-0 place-items-center rounded-2xl bg-gradient-to-br from-indigo-500 to-violet-600 text-white shadow-lg shadow-indigo-500/20 transition hover:brightness-110">
                  <ArrowUp />
                </button>
              ) : (
                <button onClick={startRecording} title="Record a voice note" className="grid h-11 w-11 shrink-0 place-items-center rounded-2xl border border-white/10 text-slate-300 transition hover:bg-white/5 hover:text-white">
                  <MicIcon />
                </button>
              )}
            </>
          )}
        </div>
      </footer>

      <ProjectsDrawer
        open={drawerOpen}
        projects={projects}
        onClose={() => setDrawerOpen(false)}
        onPick={openProject}
      />

      {(projectLoading || activeProject) && (
        <ProjectOverview
          project={activeProject}
          loading={projectLoading}
          onClose={() => setActiveProject(null)}
        />
      )}
    </div>
  )
}

// The slide-out bar of projects, pulled out by clicking the Smarty logo.
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
        className={`fixed inset-0 z-30 bg-black/50 transition-opacity ${open ? 'opacity-100' : 'pointer-events-none opacity-0'}`}
        onClick={onClose}
      />
      <aside
        className={`fixed left-0 top-0 z-40 flex h-full w-80 max-w-[85vw] flex-col border-r border-white/10 bg-slate-950 shadow-2xl shadow-black/50 transition-transform duration-200 ${open ? 'translate-x-0' : '-translate-x-full'}`}
        aria-hidden={!open}
      >
        <div className="flex items-center justify-between border-b border-white/10 px-4 py-3">
          <div>
            <div className="text-sm font-semibold text-slate-100">Projects</div>
            <div className="text-xs text-slate-500">The things I'm helping you with.</div>
          </div>
          <button onClick={onClose} className="grid h-8 w-8 place-items-center rounded-lg text-slate-400 hover:bg-white/5 hover:text-slate-200">
            <XIcon />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto p-2">
          {projects.length === 0 ? (
            <div className="px-3 py-8 text-center text-sm text-slate-500">
              No projects yet. They appear here once you start planning something ongoing.
            </div>
          ) : (
            projects.map((p) => (
              <button
                key={p.slug}
                onClick={() => onPick(p.slug)}
                className="mb-1 w-full rounded-xl px-3 py-2.5 text-left hover:bg-white/[0.05]"
              >
                <div className="truncate text-sm font-medium text-slate-100">{p.title}</div>
                {p.description && <div className="mt-0.5 line-clamp-2 text-xs text-slate-500">{p.description}</div>}
                <div className="mt-1.5 flex gap-2 text-[11px] text-slate-500">
                  <span className="rounded-full bg-white/5 px-2 py-0.5">{p.runs} {p.runs === 1 ? 'run' : 'runs'}</span>
                  <span className="rounded-full bg-white/5 px-2 py-0.5">{p.facts} {p.facts === 1 ? 'note' : 'notes'}</span>
                </div>
              </button>
            ))
          )}
        </div>
      </aside>
    </>
  )
}

// Full project view: everything remembered about it + what each background worker did (read-only).
function ProjectOverview({
  project,
  loading,
  onClose,
}: {
  project: ProjectDetail | null
  loading: boolean
  onClose: () => void
}) {
  const [showAllRuns, setShowAllRuns] = useState(false)
  const runs = project?.runs ?? []
  const visibleRuns = showAllRuns ? runs : runs.slice(0, 3)
  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-slate-950">
      <header className="flex items-center gap-3 border-b border-white/5 bg-slate-900/60 px-4 py-2.5 backdrop-blur">
        <button
          onClick={onClose}
          title="Back to chat"
          className="grid h-8 w-8 place-items-center rounded-lg text-slate-300 hover:bg-white/5"
        >
          <BackIcon />
        </button>
        <div className="min-w-0">
          <h1 className="truncate text-sm font-semibold tracking-tight text-slate-100">
            {project?.title ?? (loading ? 'Loading…' : 'Project')}
          </h1>
          {project?.description && <p className="truncate text-xs text-slate-500">{project.description}</p>}
        </div>
      </header>

      <main className="flex-1 overflow-y-auto">
        <div className="mx-auto max-w-3xl space-y-8 px-4 py-6">
          {loading || !project ? (
            <div className="pt-20 text-center text-sm text-slate-500">Loading project…</div>
          ) : (
            <>
              <section>
                {project.readme ? (
                  <div className="rounded-2xl border border-white/5 bg-white/[0.03] px-5 py-4">
                    <Markdown text={project.readme} />
                  </div>
                ) : (
                  <div className="rounded-2xl border border-dashed border-white/10 px-5 py-4 text-sm text-slate-500">
                    A summary will appear here once there's something to catch up on.
                  </div>
                )}
              </section>

              <section>
                <h2 className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">
                  Recent activity
                </h2>
                {runs.length === 0 ? (
                  <p className="text-sm text-slate-500">No background work has run for this project yet.</p>
                ) : (
                  <>
                    <div className="space-y-2">
                      {visibleRuns.map((r) => (
                        <RunCard key={r.id} run={r} />
                      ))}
                    </div>
                    {runs.length > 3 && (
                      <button
                        onClick={() => setShowAllRuns((v) => !v)}
                        className="mt-2 text-xs font-medium text-indigo-300 hover:text-indigo-200"
                      >
                        {showAllRuns ? 'Show less' : `View all ${runs.length} runs`}
                      </button>
                    )}
                  </>
                )}
              </section>

              <section>
                <h2 className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">
                  What I know ({project.memories.length})
                </h2>
                {project.memories.length === 0 ? (
                  <p className="text-sm text-slate-500">Nothing recorded for this project yet.</p>
                ) : (
                  <div className="space-y-1.5">
                    {project.memories.map((m, i) => (
                      <div key={i} className="rounded-xl border border-white/5 bg-white/[0.03] px-3.5 py-2.5">
                        <div className="text-sm text-slate-200">
                          <span className="text-slate-400">{m.key}:</span> {m.value}
                        </div>
                        {m.context && <div className="mt-0.5 text-xs text-slate-500">{m.context}</div>}
                      </div>
                    ))}
                  </div>
                )}
              </section>
            </>
          )}
        </div>
      </main>
    </div>
  )
}

// One worker run — collapsed to its task + result, expandable to the full thinking/tool timeline.
function RunCard({ run }: { run: ProjectRun }) {
  const [open, setOpen] = useState(false)
  const when = new Date(run.startedAt)
  return (
    <div className="overflow-hidden rounded-xl border border-white/5 bg-white/[0.03]">
      <button onClick={() => setOpen((o) => !o)} className="flex w-full items-start gap-3 px-3.5 py-3 text-left hover:bg-white/[0.02]">
        <StatusDot status={run.status} />
        <div className="min-w-0 flex-1">
          <div className="text-sm font-medium leading-snug text-slate-200">{run.task}</div>
          {run.result && !open && (
            <div className="mt-0.5 line-clamp-2 text-xs leading-snug text-slate-400">{run.result}</div>
          )}
          <div className="mt-1 flex items-center gap-2 text-[11px] text-slate-500">
            <span className="uppercase tracking-wide">{run.status}</span>
            <span>·</span>
            <span>{when.toLocaleString()}</span>
            <span>·</span>
            <span>{run.steps.length} steps</span>
            <span>·</span>
            <span className="text-indigo-300/80">{open ? 'hide detail' : 'see what it did'}</span>
          </div>
        </div>
        <span className={`mt-1 shrink-0 text-slate-500 transition-transform ${open ? 'rotate-90' : ''}`}>
          <ChevronIcon />
        </span>
      </button>
      {open && (
        <div className="space-y-2 border-t border-white/5 px-3.5 py-3">
          {run.steps.map((s, i) => (
            <StepRow key={i} step={s} />
          ))}
          {run.result && (
            <div className="mt-2 rounded-lg border border-emerald-400/15 bg-emerald-400/[0.06] px-3 py-2">
              <div className="mb-1 text-[10px] font-semibold uppercase tracking-wide text-emerald-300/80">Result</div>
              <div className="whitespace-pre-wrap text-xs leading-relaxed text-slate-200">{run.result}</div>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function StepRow({ step }: { step: RunStep }) {
  if (step.kind === 'thinking') {
    return (
      <div className="flex gap-2.5">
        <span className="mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-slate-600" />
        <pre className="min-w-0 flex-1 whitespace-pre-wrap font-sans text-xs leading-relaxed text-slate-500">{step.text}</pre>
      </div>
    )
  }
  if (step.kind === 'tool') {
    return (
      <div className="flex gap-2.5">
        <span className="mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-indigo-400" />
        <div className="min-w-0 flex-1">
          <div className="font-mono text-xs text-indigo-300">
            {step.tool}
            {step.args && step.args !== '{}' && <span className="text-slate-500"> {step.args}</span>}
          </div>
          {step.result && (
            <pre className="mt-1 max-h-40 overflow-y-auto whitespace-pre-wrap rounded-md bg-black/30 px-2.5 py-1.5 font-mono text-[11px] leading-relaxed text-slate-400">
              {step.result}
            </pre>
          )}
        </div>
      </div>
    )
  }
  // answer
  return (
    <div className="flex gap-2.5">
      <span className="mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-slate-300" />
      <div className="min-w-0 flex-1 whitespace-pre-wrap text-xs leading-relaxed text-slate-300">{step.text}</div>
    </div>
  )
}

function StatusDot({ status }: { status: string }) {
  const color =
    status === 'done' ? 'bg-emerald-400' : status === 'failed' ? 'bg-rose-400' : status === 'cancelled' ? 'bg-slate-500' : 'bg-amber-400'
  return <span className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${color}`} />
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

function Empty({ onPick }: { onPick: (prompt: string) => void }) {
  return (
    <div className="flex flex-col items-center pt-20 text-center">
      <span className="grid h-12 w-12 place-items-center rounded-2xl bg-gradient-to-br from-indigo-500 to-violet-600 text-lg font-bold text-white shadow-xl shadow-indigo-500/25">
        S
      </span>
      <h2 className="mt-4 text-lg font-semibold text-slate-200">How can I help?</h2>
      <p className="mt-1 max-w-sm text-sm text-slate-500">
        Ask me anything — I'll go and sort it out for you. Type, or hit the mic for a voice note.
      </p>
      <div className="mt-6 flex flex-wrap justify-center gap-2">
        {EXAMPLES.map((e) => (
          <button
            key={e}
            onClick={() => onPick(e)}
            className="rounded-full border border-white/10 bg-white/5 px-3.5 py-1.5 text-xs text-slate-300 hover:border-indigo-400/40 hover:bg-indigo-500/10 hover:text-indigo-200"
          >
            {e}
          </button>
        ))}
      </div>
    </div>
  )
}

function MessageRow({ message, sessionId }: { message: UiMessage; sessionId: string }) {
  if (message.role === 'user') {
    if (message.audio) return <VoiceBubble message={message} />
    return (
      <div className="flex justify-end">
        <div className="max-w-[80%] whitespace-pre-wrap rounded-2xl rounded-br-md bg-gradient-to-br from-indigo-500 to-violet-600 px-4 py-2.5 text-sm leading-relaxed text-white shadow-lg shadow-indigo-500/10">
          {message.content}
        </div>
      </div>
    )
  }

  return (
    <div className="flex gap-3">
      <span className="mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-gradient-to-br from-indigo-500 to-violet-600 text-xs font-bold text-white">
        S
      </span>
      <div className="min-w-0 flex-1 space-y-2">
        {message.reasoning &&
          (() => {
            // While the answer hasn't started and the turn is still streaming, the model is still thinking
            // — keep the block labelled "Thinking…" (with a spinner) until that reasoning block closes.
            const thinking = message.streaming && message.content.length === 0
            return (
              <details className="py-0.5 text-xs text-slate-500 marker:text-slate-600">
                <summary className="inline-flex cursor-pointer select-none items-center gap-1.5 text-slate-400">
                  {thinking && (
                    <span className="h-3 w-3 shrink-0 animate-spin rounded-full border-2 border-slate-600 border-t-slate-300" />
                  )}
                  {thinking ? 'Thinking…' : 'thought'}
                </summary>
                <pre className="mt-2 whitespace-pre-wrap font-sans leading-relaxed text-slate-500">{message.reasoning}</pre>
              </details>
            )
          })()}
        {message.content ? (
          <div className="rounded-2xl rounded-tl-md border border-white/5 bg-white/[0.04] px-4 py-2.5 text-sm leading-relaxed text-slate-100">
            <Markdown text={message.content} />
          </div>
        ) : message.streaming && !message.reasoning ? (
          // Only show the dots when there's no reasoning block yet — once it's "Thinking…", that line is
          // the indicator, so we don't double up.
          <div className="py-1">
            <TypingDots />
          </div>
        ) : null}
        {message.content && !message.streaming && (
          <div className="flex items-center gap-1 pt-0.5">
            <CopyButton text={message.content} />
            <FeedbackButtons sessionId={sessionId} messageId={message.id} />
          </div>
        )}
      </div>
    </div>
  )
}

// Thumbs up/down — labels the logged interaction as a good/bad training example.
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
        className={`grid h-7 w-7 place-items-center rounded-md transition hover:bg-white/5 ${rated === 'up' ? 'text-emerald-400' : 'text-slate-500 hover:text-slate-300'}`}
      >
        <ThumbIcon up />
      </button>
      <button
        onClick={() => rate('down')}
        title="Bad response"
        className={`grid h-7 w-7 place-items-center rounded-md transition hover:bg-white/5 ${rated === 'down' ? 'text-rose-400' : 'text-slate-500 hover:text-slate-300'}`}
      >
        <ThumbIcon />
      </button>
    </span>
  )
}

function ThumbIcon({ up }: { up?: boolean }) {
  return (
    <svg
      width="13"
      height="13"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      style={up ? undefined : { transform: 'rotate(180deg)' }}
    >
      <path d="M7 10v12" />
      <path d="M15 5.88 14 10h5.83a2 2 0 0 1 1.92 2.56l-2.33 8A2 2 0 0 1 17.5 22H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h2.76a2 2 0 0 0 1.79-1.11L12 2a3.13 3.13 0 0 1 3 3.88Z" />
    </svg>
  )
}

function TaskPill({
  tasks,
  open,
  onToggle,
  onClose,
  onCancel,
}: {
  tasks: { id: string; task: string }[]
  open: boolean
  onToggle: () => void
  onClose: () => void
  onCancel: (id: string) => void
}) {
  if (tasks.length === 0) return <div className="ml-auto" />

  return (
    <div className="relative ml-auto">
      <button
        onClick={onToggle}
        className="inline-flex items-center gap-2 rounded-full border border-amber-300/20 bg-amber-400/10 px-3 py-1.5 text-xs font-medium text-amber-100 shadow-sm shadow-amber-950/20 hover:bg-amber-400/15"
        aria-expanded={open}
        aria-label={`${tasks.length} running ${tasks.length === 1 ? 'task' : 'tasks'}`}
      >
        <Spinner />
        <span>{tasks.length} running</span>
      </button>

      {open && (
        <>
          <button className="fixed inset-0 z-10 cursor-default" aria-label="Close tasks" onClick={onClose} />
          <div className="absolute right-0 top-10 z-20 w-80 max-w-[calc(100vw-2rem)] overflow-hidden rounded-2xl border border-white/10 bg-slate-950 shadow-2xl shadow-black/40">
            <div className="flex items-center justify-between border-b border-white/10 px-4 py-3">
              <div>
                <div className="text-sm font-semibold text-slate-100">Running tasks</div>
                <div className="text-xs text-slate-500">Cancel anything you no longer need.</div>
              </div>
              <button onClick={onClose} className="grid h-8 w-8 place-items-center rounded-lg text-slate-400 hover:bg-white/5 hover:text-slate-200">
                <XIcon />
              </button>
            </div>
            <div className="max-h-80 overflow-y-auto p-2">
              {tasks.map((task) => (
                <div key={task.id} className="rounded-xl px-3 py-2.5 hover:bg-white/[0.04]">
                  <div className="flex gap-3">
                    <div className="pt-1">
                      <Spinner />
                    </div>
                    <div className="min-w-0 flex-1">
                      <div className="text-[11px] font-medium uppercase tracking-wide text-slate-500">Task #{task.id}</div>
                      <div className="mt-0.5 line-clamp-3 text-sm leading-snug text-slate-200">{task.task}</div>
                    </div>
                  </div>
                  <button
                    onClick={() => onCancel(task.id)}
                    className="mt-2 rounded-lg border border-rose-300/20 px-2.5 py-1.5 text-xs text-rose-200 hover:bg-rose-400/10"
                  >
                    Cancel
                  </button>
                </div>
              ))}
            </div>
          </div>
        </>
      )}
    </div>
  )
}

function Spinner() {
  return <span className="h-3.5 w-3.5 shrink-0 animate-spin rounded-full border-2 border-amber-200/25 border-t-amber-200" />
}

/*
function OldWorkingRow({ task }: { task: string }) {
  return (
    <div className="flex gap-3">
      <span className="mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-gradient-to-br from-indigo-500 to-violet-600 text-xs font-bold text-white">
        S
      </span>
      <div className="flex min-w-0 flex-1 items-center gap-2 py-1 text-xs text-slate-500">
        <span className="inline-flex gap-0.5">
          <span className="h-1.5 w-1.5 rounded-full bg-amber-400/70 blink" />
          <span className="h-1.5 w-1.5 rounded-full bg-amber-400/70 blink" style={{ animationDelay: '0.2s' }} />
          <span className="h-1.5 w-1.5 rounded-full bg-amber-400/70 blink" style={{ animationDelay: '0.4s' }} />
        </span>
        <span className="truncate italic">on it — {task.charAt(0).toLowerCase() + task.slice(1)}</span>
      </div>
    </div>
  )
}

*/
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
      <div className="max-w-[80%] rounded-2xl rounded-br-md bg-gradient-to-br from-indigo-500 to-violet-600 px-3 py-2.5 text-white shadow-lg shadow-indigo-500/10">
        <div className="flex items-center gap-3">
          <button onClick={toggle} disabled={!audio.url} className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-white/20 hover:bg-white/30 disabled:opacity-50">
            {playing ? <PauseIcon /> : <PlayIcon />}
          </button>
          <div className="w-44 sm:w-56">
            <Waveform peaks={audio.peaks} progress={progress} idleClass="bg-white/40" activeClass="bg-white" />
          </div>
          <span className="shrink-0 text-[11px] tabular-nums opacity-80">{formatDuration(audio.duration)}</span>
        </div>
        {message.content && <div className="mt-1.5 text-xs opacity-90">{message.content}</div>}
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
    <div className="prose prose-sm prose-invert max-w-none prose-p:my-2 prose-pre:my-2 prose-pre:border prose-pre:border-white/5 prose-pre:bg-black/40 prose-code:text-emerald-200 prose-headings:text-slate-100 prose-a:text-indigo-300 prose-strong:text-slate-100 prose-li:my-0.5">
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
    <button onClick={copy} className="inline-flex items-center gap-1.5 rounded-md px-1.5 py-1 text-[11px] text-slate-500 transition hover:bg-white/5 hover:text-slate-300" title="Copy">
      {copied ? <CheckIcon /> : <CopyIcon />}
      {copied ? 'copied' : 'copy'}
    </button>
  )
}

function TypingDots() {
  return (
    <span className="inline-flex gap-1 py-1">
      <span className="h-1.5 w-1.5 rounded-full bg-slate-400 blink" />
      <span className="h-1.5 w-1.5 rounded-full bg-slate-400 blink" style={{ animationDelay: '0.2s' }} />
      <span className="h-1.5 w-1.5 rounded-full bg-slate-400 blink" style={{ animationDelay: '0.4s' }} />
    </span>
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
