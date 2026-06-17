import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { cancelRun, streamChat, transcribe, type ChatMessage, type StreamHandlers } from './api'
import { formatDuration, toWav16k, type RecordedAudio } from './audio'

type Part =
  | { kind: 'reasoning'; text: string }
  | { kind: 'content'; text: string }
  | { kind: 'tool'; name: string; args: string; result?: string }

interface AudioNote {
  peaks: number[]
  duration: number
  url?: string // blob URL — playback in-session only (not persisted)
}

interface UiMessage {
  role: 'user' | 'assistant'
  content: string
  parts: Part[]
  streaming: boolean
  error?: string
  runId?: string
  pending?: boolean
  audio?: AudioNote // a voice note (user messages)
  transcribing?: boolean
}

const DEFAULT_SYSTEM =
  'You are a smart, concise, helpful and friendly assistant. Your goal is to help the user achieve ' +
  'their request by thoroughly using the tools provided. You are relentless and always try to find a way.'
const STORAGE_KEY = 'smarty-chat:v2'
const REC_BARS = 56

const EXAMPLES = [
  'What is the current system status?',
  'List the files in the current directory.',
  'How much disk space is free?',
]

interface SavedState {
  messages?: UiMessage[]
}

const saved: SavedState = loadState()

function loadState(): SavedState {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return {}
    const data = JSON.parse(raw) as SavedState
    data.messages = (data.messages ?? []).map((m) => ({ ...m, parts: m.parts ?? [], streaming: false }))
    return data
  } catch {
    return {}
  }
}

function wireOf(msgs: UiMessage[]): ChatMessage[] {
  return msgs.map((m) => ({
    role: m.role,
    content:
      m.role === 'user'
        ? m.content
        : m.parts
            .filter((p): p is Extract<Part, { kind: 'content' }> => p.kind === 'content')
            .map((p) => p.text)
            .join(''),
  }))
}

export default function App() {
  const [messages, setMessages] = useState<UiMessage[]>(saved.messages ?? [])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [recording, setRecording] = useState(false)
  const [recPeaks, setRecPeaks] = useState<number[]>([])
  const [recSeconds, setRecSeconds] = useState(0)

  const system = DEFAULT_SYSTEM
  const model = 'qwen3.5:latest'
  const enableTools = true

  const abortRef = useRef<AbortController | null>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const taRef = useRef<HTMLTextAreaElement>(null)
  const atBottomRef = useRef(true)
  const saveTimer = useRef<number>()
  const lastSave = useRef(0)
  const recRef = useRef<{ mr: MediaRecorder; ctx: AudioContext; sampler: number } | null>(null)
  const cancelledRef = useRef(false)

  useEffect(() => {
    const last = saved.messages?.[saved.messages.length - 1]
    if (last && last.role === 'assistant' && last.pending && last.runId) resume(last.runId)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    const save = () => {
      lastSave.current = Date.now()
      try {
        const data: SavedState = {
          messages: messages.map((m) => ({
            ...m,
            streaming: false,
            // Drop the blob URL — it's dead after a reload; keep peaks + transcript.
            audio: m.audio ? { peaks: m.audio.peaks, duration: m.audio.duration } : undefined,
          })),
        }
        localStorage.setItem(STORAGE_KEY, JSON.stringify(data))
      } catch {
        /* ignore */
      }
    }
    const since = Date.now() - lastSave.current
    clearTimeout(saveTimer.current)
    if (since >= 400) save()
    else saveTimer.current = window.setTimeout(save, 400 - since)
    return () => clearTimeout(saveTimer.current)
  }, [messages])

  function onScroll() {
    const el = scrollRef.current
    if (!el) return
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80
  }
  useLayoutEffect(() => {
    const el = scrollRef.current
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight
  }, [messages])

  useEffect(() => {
    const el = taRef.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = Math.min(Math.max(el.scrollHeight, 64), 220) + 'px'
  }, [input])

  function updateLast(fn: (m: UiMessage) => UiMessage) {
    setMessages((prev) => {
      if (!prev.length) return prev
      const next = prev.slice()
      next[next.length - 1] = fn(next[next.length - 1])
      return next
    })
  }

  function appendPart(kind: 'reasoning' | 'content', text: string) {
    updateLast((m) => {
      const parts = m.parts.slice()
      const last = parts[parts.length - 1]
      if (last && last.kind === kind) parts[parts.length - 1] = { ...last, text: last.text + text }
      else parts.push({ kind, text })
      return { ...m, parts }
    })
  }

  function handlers(): StreamHandlers {
    return {
      onRun: (runId) => updateLast((m) => ({ ...m, runId, pending: true })),
      onResumed: () => updateLast((m) => ({ ...m, parts: [], error: undefined })),
      onContent: (t) => appendPart('content', t),
      onContentCleared: () =>
        updateLast((m) => {
          const parts = m.parts.slice()
          if (parts.length && parts[parts.length - 1].kind === 'content') parts.pop()
          return { ...m, parts }
        }),
      onReasoning: (t) => appendPart('reasoning', t),
      onToolStarted: (name, args) =>
        updateLast((m) => ({ ...m, parts: [...m.parts, { kind: 'tool', name, args }] })),
      onToolCompleted: (name, result) =>
        updateLast((m) => {
          const parts = m.parts.slice()
          for (let i = parts.length - 1; i >= 0; i--) {
            const p = parts[i]
            if (p.kind === 'tool' && p.name === name && p.result === undefined) {
              parts[i] = { ...p, result }
              break
            }
          }
          return { ...m, parts }
        }),
      onError: (msg) => updateLast((m) => ({ ...m, error: msg, pending: false })),
      onDone: () => {
        updateLast((m) => ({ ...m, streaming: false, pending: false }))
        setBusy(false)
        abortRef.current = null
      },
    }
  }

  async function startRun(base: UiMessage[], userMsg: UiMessage) {
    setBusy(true)
    atBottomRef.current = true
    setMessages([...base, userMsg, { role: 'assistant', content: '', parts: [], streaming: true }])

    const controller = new AbortController()
    abortRef.current = controller
    await streamChat(
      { system, model, enableTools, messages: wireOf([...base, userMsg]) },
      handlers(),
      controller.signal,
    )
  }

  async function resume(runId: string) {
    setBusy(true)
    updateLast((m) => ({ ...m, streaming: true, pending: true, runId }))
    const controller = new AbortController()
    abortRef.current = controller
    await streamChat({ system, model, enableTools, messages: [] }, handlers(), controller.signal, {
      runId,
      from: 0,
    })
  }

  function send(text?: string) {
    const body = (text ?? input).trim()
    if (!body || busy) return
    setInput('')
    startRun(messages, { role: 'user', content: body, parts: [], streaming: false })
  }

  // Voice note: show the bubble immediately, transcribe via Whisper, then run the agent on the text.
  async function sendVoice(rec: RecordedAudio) {
    if (busy) return
    setBusy(true)
    const base = messages
    const audio: AudioNote = { peaks: rec.peaks, duration: rec.duration, url: URL.createObjectURL(rec.wav) }
    setMessages([...base, { role: 'user', content: '', parts: [], streaming: false, audio, transcribing: true }])

    let text = ''
    try {
      text = await transcribe(rec.wav)
    } catch {
      /* leave empty */
    }

    if (!text) {
      setMessages([
        ...base,
        { role: 'user', content: '(could not transcribe audio)', parts: [], streaming: false, audio },
      ])
      setBusy(false)
      return
    }

    setBusy(false)
    startRun(base, { role: 'user', content: text, parts: [], streaming: false, audio })
  }

  function retry() {
    let base = messages.slice()
    if (base.length && base[base.length - 1].role === 'assistant') base = base.slice(0, -1)
    const userMsg = base.length ? base[base.length - 1] : null
    if (!userMsg || userMsg.role !== 'user' || !userMsg.content) return
    base = base.slice(0, -1)
    startRun(base, userMsg)
  }

  function stop() {
    const last = messages[messages.length - 1]
    if (last?.runId) cancelRun(last.runId)
    abortRef.current?.abort()
    abortRef.current = null
    updateLast((m) => ({ ...m, streaming: false, pending: false }))
    setBusy(false)
  }

  function reset() {
    const last = messages[messages.length - 1]
    if (last?.runId && last.pending) cancelRun(last.runId)
    abortRef.current?.abort()
    abortRef.current = null
    setMessages([])
    setBusy(false)
  }

  // ---- recording ----

  async function startRecording() {
    if (busy || recording) return
    let stream: MediaStream
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true })
    } catch {
      return // mic denied / unavailable
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

  function stopRecording() {
    recRef.current?.mr.stop()
  }
  function cancelRecording() {
    cancelledRef.current = true
    recRef.current?.mr.stop()
  }

  return (
    <div className="flex h-full flex-col overflow-x-hidden bg-slate-950 text-slate-100">
      <header className="flex items-center gap-3 border-b border-white/5 bg-slate-900/60 px-4 py-2.5 backdrop-blur">
        <span className="grid h-7 w-7 place-items-center rounded-lg bg-gradient-to-br from-indigo-500 to-violet-600 text-sm font-bold text-white shadow-lg shadow-indigo-500/20">
          S
        </span>
        <h1 className="text-sm font-semibold tracking-tight">Smarty</h1>
        <span className="hidden text-xs text-slate-500 sm:inline">agent tester</span>

        <button
          onClick={reset}
          className="ml-auto rounded-lg border border-white/10 px-2.5 py-1.5 text-xs text-slate-300 hover:bg-white/5"
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
              {messages.map((m, i) => (
                <MessageRow key={i} message={m} onRetry={retry} />
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
              <button
                onClick={cancelRecording}
                title="Cancel"
                className="grid h-9 w-9 shrink-0 place-items-center rounded-xl text-slate-300 hover:bg-white/10"
              >
                <XIcon />
              </button>
              <button
                onClick={stopRecording}
                title="Send voice note"
                className="grid h-9 w-9 shrink-0 place-items-center rounded-xl bg-gradient-to-br from-indigo-500 to-violet-600 text-white hover:brightness-110"
              >
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
                  placeholder="Message the agent…"
                  className="max-h-56 flex-1 resize-none bg-transparent text-[15px] leading-relaxed text-slate-100 outline-none placeholder:text-slate-500"
                />
              </div>
              {busy ? (
                <button
                  onClick={stop}
                  className="grid h-11 w-11 shrink-0 place-items-center rounded-2xl bg-white/10 text-slate-200 hover:bg-white/20"
                  title="Stop"
                >
                  <span className="h-3 w-3 rounded-sm bg-slate-200" />
                </button>
              ) : input.trim() ? (
                <button
                  onClick={() => send()}
                  className="grid h-11 w-11 shrink-0 place-items-center rounded-2xl bg-gradient-to-br from-indigo-500 to-violet-600 text-white shadow-lg shadow-indigo-500/20 transition hover:brightness-110"
                  title="Send"
                >
                  <ArrowUp />
                </button>
              ) : (
                <button
                  onClick={startRecording}
                  className="grid h-11 w-11 shrink-0 place-items-center rounded-2xl border border-white/10 text-slate-300 transition hover:bg-white/5 hover:text-white"
                  title="Record a voice note"
                >
                  <MicIcon />
                </button>
              )}
            </>
          )}
        </div>
      </footer>
    </div>
  )
}

function Empty({ onPick }: { onPick: (prompt: string) => void }) {
  return (
    <div className="flex flex-col items-center pt-20 text-center">
      <span className="grid h-12 w-12 place-items-center rounded-2xl bg-gradient-to-br from-indigo-500 to-violet-600 text-lg font-bold text-white shadow-xl shadow-indigo-500/25">
        S
      </span>
      <h2 className="mt-4 text-lg font-semibold text-slate-200">What can I help you check?</h2>
      <p className="mt-1 max-w-sm text-sm text-slate-500">
        A live agent backed by your local model. Type, or hit the mic to send a voice note.
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

function MessageRow({ message, onRetry }: { message: UiMessage; onRetry: () => void }) {
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

  const last = message.parts[message.parts.length - 1]
  // The active reasoning block hosts the "thinking…" indicator itself; only show a standalone loader
  // when there's no active thought to host it (e.g. between a tool call and the next thought).
  const working = message.streaming && last?.kind !== 'reasoning' && last?.kind !== 'content'
  const contentText = message.parts
    .filter((p): p is Extract<Part, { kind: 'content' }> => p.kind === 'content')
    .map((p) => p.text)
    .join('')

  return (
    <div className="flex gap-3">
      <span className="mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-gradient-to-br from-indigo-500 to-violet-600 text-xs font-bold text-white">
        S
      </span>
      <div className="min-w-0 flex-1 space-y-2">
        {message.parts.map((part, i) => {
          const isLast = i === message.parts.length - 1
          if (part.kind === 'reasoning')
            return <ReasoningPart key={i} text={part.text} active={isLast && message.streaming} />
          if (part.kind === 'tool') return <ToolChip key={i} tool={part} />
          return (
            <div
              key={i}
              className="rounded-2xl rounded-tl-md border border-white/5 bg-white/[0.04] px-4 py-2.5 text-sm leading-relaxed text-slate-100"
            >
              <Markdown text={part.text} />
            </div>
          )
        })}

        {working && (
          <div className="py-1">
            <Thinking />
          </div>
        )}

        {contentText && !message.streaming && (
          <div className="pt-0.5">
            <CopyButton text={contentText} />
          </div>
        )}

        {message.error && (
          <div className="flex items-center gap-3 rounded-xl border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-300">
            <span>{message.error}</span>
            <button
              onClick={onRetry}
              className="ml-auto rounded-md bg-amber-500/20 px-2 py-1 font-medium text-amber-200 hover:bg-amber-500/30"
            >
              Retry
            </button>
          </div>
        )}
      </div>
    </div>
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
      <div className="max-w-[80%] rounded-2xl rounded-br-md bg-gradient-to-br from-indigo-500 to-violet-600 px-3 py-2.5 text-white shadow-lg shadow-indigo-500/10">
        <div className="flex items-center gap-3">
          <button
            onClick={toggle}
            disabled={!audio.url}
            className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-white/20 hover:bg-white/30 disabled:opacity-50"
            title={audio.url ? (playing ? 'Pause' : 'Play') : 'Audio not available after reload'}
          >
            {playing ? <PauseIcon /> : <PlayIcon />}
          </button>
          <div className="w-44 sm:w-56">
            <Waveform peaks={audio.peaks} progress={progress} idleClass="bg-white/40" activeClass="bg-white" />
          </div>
          <span className="shrink-0 text-[11px] tabular-nums opacity-80">{formatDuration(audio.duration)}</span>
        </div>

        {message.transcribing ? (
          <div className="mt-1.5 text-[11px] italic opacity-75">transcribing…</div>
        ) : message.content ? (
          <div className="mt-1.5 text-xs opacity-90">{message.content}</div>
        ) : null}

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

function Waveform({
  peaks,
  progress,
  idleClass,
  activeClass,
}: {
  peaks: number[]
  progress: number
  idleClass: string
  activeClass: string
}) {
  // Bars flex to share the container width, so they always fit and never overflow their holder.
  return (
    <div className="flex h-7 items-center gap-px overflow-hidden">
      {peaks.map((p, i) => {
        const on = peaks.length ? (i + 1) / peaks.length <= progress : false
        return (
          <div
            key={i}
            className={`min-w-0 flex-1 rounded-full ${on ? activeClass : idleClass}`}
            style={{ height: `${Math.max(12, p * 100)}%` }}
          />
        )
      })}
    </div>
  )
}

// A reasoning segment, collapsed by default. While it's the active block the header is the live
// "thinking…" indicator (open it to watch the stream); once done it's a plain "thought".
function ReasoningPart({ text, active }: { text: string; active?: boolean }) {
  return (
    <details className="py-0.5 text-xs text-slate-500 marker:text-slate-600">
      <summary className="cursor-pointer select-none text-slate-400">
        {active ? <Thinking /> : 'thought'}
      </summary>
      <pre className="mt-2 whitespace-pre-wrap font-sans leading-relaxed text-slate-500">{text}</pre>
    </details>
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
      /* clipboard blocked */
    }
  }
  return (
    <button
      onClick={copy}
      className="inline-flex items-center gap-1.5 rounded-md px-1.5 py-1 text-[11px] text-slate-500 transition hover:bg-white/5 hover:text-slate-300"
      title="Copy response"
    >
      {copied ? <CheckIcon /> : <CopyIcon />}
      {copied ? 'copied' : 'copy'}
    </button>
  )
}

function Markdown({ text }: { text: string }) {
  return (
    <div className="prose prose-sm prose-invert max-w-none prose-p:my-2 prose-pre:my-2 prose-pre:border prose-pre:border-white/5 prose-pre:bg-black/40 prose-code:text-emerald-200 prose-headings:text-slate-100 prose-a:text-indigo-300 prose-strong:text-slate-100 prose-li:my-0.5">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          a: (props) => <a {...props} target="_blank" rel="noreferrer noopener" />,
        }}
      >
        {text}
      </ReactMarkdown>
    </div>
  )
}

function ToolChip({ tool }: { tool: Extract<Part, { kind: 'tool' }> }) {
  const running = tool.result === undefined
  const failed =
    !running &&
    (tool.result!.startsWith('[exit code') || /error|denied|not recognized/i.test(tool.result!))
  return (
    <div className={`flex items-center gap-2 text-xs ${running ? 'animate-pulse' : ''}`}>
      <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-amber-400/50" />
      <span className={`font-mono ${failed ? 'text-rose-400/70' : 'text-slate-400'}`}>{tool.name}</span>
    </div>
  )
}

const THINKING_WORDS = [
  'thinking', 'pondering', 'contemplating', 'ruminating', 'mulling it over', 'cogitating',
  'deliberating', 'noodling on it', 'musing', 'reasoning it out', 'crunching the numbers',
  'doing the math', 'connecting the dots', 'weighing the options', 'considering the angles',
  'untangling the threads', 'piecing it together', 'following the logic', 'chasing a tangent',
  'questioning the future', 'questioning everything', 'doubting its training data',
  'interrogating reality', 'staring into the void', 'consulting the oracle',
  'searching the latent space', 'warming up the neurons', 'herding the tokens',
  'reticulating splines', 'aligning the qubits', 'spinning up ideas', 'brewing a response',
  'assembling an answer', 'sharpening the logic', 'triangulating the truth', 'divining an answer',
  'summoning a thought', 'parsing the cosmos', 'gazing into the matrix', 'recalculating',
  'second-guessing itself', 'overthinking it', 'thinking very hard', 'meditating on it',
  'wrangling probabilities', 'following the breadcrumbs', 'rolling the dice',
  'counting backwards from infinity', 'having a quiet word with itself', 'consulting the void',
]

function Thinking() {
  const [word, setWord] = useState(THINKING_WORDS[0])
  useEffect(() => {
    const id = setInterval(() => {
      setWord((prev) => {
        let next = prev
        while (next === prev) next = THINKING_WORDS[Math.floor(Math.random() * THINKING_WORDS.length)]
        return next
      })
    }, 10000)
    return () => clearInterval(id)
  }, [])
  return (
    <span className="inline-flex items-center gap-1.5 text-sm text-slate-500">
      {word}
      <span className="inline-flex gap-0.5">
        <span className="h-1 w-1 rounded-full bg-slate-500 blink" />
        <span className="h-1 w-1 rounded-full bg-slate-500 blink" style={{ animationDelay: '0.2s' }} />
        <span className="h-1 w-1 rounded-full bg-slate-500 blink" style={{ animationDelay: '0.4s' }} />
      </span>
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
