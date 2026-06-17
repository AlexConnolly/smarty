import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { cancelRun, streamChat, type ChatMessage, type StreamHandlers } from './api'

// An assistant message is an ordered timeline of parts, so think → tool → think → answer is shown
// in the real sequence it happened, not flattened into separate buckets.
type Part =
  | { kind: 'reasoning'; text: string }
  | { kind: 'content'; text: string }
  | { kind: 'tool'; name: string; args: string; result?: string }

interface UiMessage {
  role: 'user' | 'assistant'
  content: string // user text (empty for assistant)
  parts: Part[] // assistant timeline (empty for user)
  streaming: boolean
  error?: string
  runId?: string
  pending?: boolean
}

const DEFAULT_SYSTEM = 'You are a helpful assistant. Use tools when they help answer accurately.'
const STORAGE_KEY = 'smarty-chat:v2'

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

  // Fixed defaults — no longer surfaced as header controls.
  const system = DEFAULT_SYSTEM
  const model = 'qwen3:latest' // the 8B: slower per token, but smarter → fewer retries, quicker to a good answer
  const enableTools = true

  const abortRef = useRef<AbortController | null>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const taRef = useRef<HTMLTextAreaElement>(null)
  const atBottomRef = useRef(true)
  const saveTimer = useRef<number>()
  const lastSave = useRef(0)

  // Reattach to a still-pending background run after a reload.
  useEffect(() => {
    const last = saved.messages?.[saved.messages.length - 1]
    if (last && last.role === 'assistant' && last.pending && last.runId) resume(last.runId)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Persist the chat (throttled with a guaranteed flush, so an in-flight run survives a reload).
  useEffect(() => {
    const save = () => {
      lastSave.current = Date.now()
      try {
        const data: SavedState = { messages: messages.map((m) => ({ ...m, streaming: false })) }
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
    el.style.height = Math.min(el.scrollHeight, 200) + 'px'
  }, [input])

  function updateLast(fn: (m: UiMessage) => UiMessage) {
    setMessages((prev) => {
      if (!prev.length) return prev
      const next = prev.slice()
      next[next.length - 1] = fn(next[next.length - 1])
      return next
    })
  }

  // Append streamed text to the last part if it's the same kind, else start a new part — this is
  // what builds the interleaved timeline.
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

  async function runFrom(base: UiMessage[], userText: string) {
    if (!userText || busy) return
    setBusy(true)
    atBottomRef.current = true

    const userMsg: UiMessage = { role: 'user', content: userText, parts: [], streaming: false }
    setMessages([...base, userMsg, { role: 'assistant', content: '', parts: [], streaming: true }])

    const controller = new AbortController()
    abortRef.current = controller
    await streamChat(
      { system, model, enableTools, messages: wireOf([...base, userMsg]) },
      handlers(),
      controller.signal,
    )
  }

  // Reattach to a background run. We don't wipe the message up front — if it can't be reattached
  // (e.g. the API restarted), the chat is just left as it was; the parts are cleared and rebuilt
  // only once the replay actually connects (onResumed).
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
    runFrom(messages, body)
  }

  function retry() {
    let base = messages.slice()
    if (base.length && base[base.length - 1].role === 'assistant') base = base.slice(0, -1)
    let userText = ''
    if (base.length && base[base.length - 1].role === 'user') {
      userText = base[base.length - 1].content
      base = base.slice(0, -1)
    }
    if (userText) runFrom(base, userText)
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

  return (
    <div className="flex h-full flex-col bg-slate-950 text-slate-100">
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
          <div className="flex flex-1 items-end rounded-2xl border border-white/10 bg-white/5 px-3 py-2 focus-within:border-indigo-400/60">
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
              placeholder="Message the agent…"
              className="max-h-48 flex-1 resize-none bg-transparent text-sm text-slate-100 outline-none placeholder:text-slate-500"
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
          ) : (
            <button
              onClick={() => send()}
              disabled={!input.trim()}
              className="grid h-11 w-11 shrink-0 place-items-center rounded-2xl bg-gradient-to-br from-indigo-500 to-violet-600 text-white shadow-lg shadow-indigo-500/20 transition hover:brightness-110 disabled:opacity-30 disabled:shadow-none"
              title="Send"
            >
              <ArrowUp />
            </button>
          )}
        </div>
        <p className="mx-auto mt-1.5 max-w-3xl text-center text-[11px] text-slate-600">
          Enter to send · Shift+Enter for newline
        </p>
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
        A live agent backed by your local model. With shell tools on it can run real commands to
        answer.
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
    return (
      <div className="flex justify-end">
        <div className="max-w-[80%] whitespace-pre-wrap rounded-2xl rounded-br-md bg-gradient-to-br from-indigo-500 to-violet-600 px-4 py-2.5 text-sm leading-relaxed text-white shadow-lg shadow-indigo-500/10">
          {message.content}
        </div>
      </div>
    )
  }

  const last = message.parts[message.parts.length - 1]
  // Show a standalone "working" indicator when the agent is busy but not actively producing a
  // reasoning/content part (e.g. right after a tool call, before the next thought begins).
  const working = message.streaming && (!last || last.kind === 'tool')

  return (
    <div className="flex gap-3">
      <span className="mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-gradient-to-br from-indigo-500 to-violet-600 text-xs font-bold text-white">
        S
      </span>
      <div className="min-w-0 flex-1 space-y-2">
        {message.parts.map((part, i) => {
          const isLast = i === message.parts.length - 1
          if (part.kind === 'reasoning')
            return <ReasoningPart key={i} text={part.text} live={isLast && message.streaming} />
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
          <div className="rounded-2xl rounded-tl-md border border-white/5 bg-white/[0.04] px-4 py-2.5">
            <Thinking />
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

// A reasoning segment: the live one (currently streaming) shows the animated header + auto-scroll;
// finished segments collapse into an expandable "thought" so the chain is visible but not a wall.
function ReasoningPart({ text, live }: { text: string; live: boolean }) {
  if (live) return <LiveReasoning text={text} />
  return (
    <details className="rounded-xl border border-white/5 bg-black/20 px-3 py-2 text-xs text-slate-500">
      <summary className="cursor-pointer select-none text-slate-400">thought</summary>
      <pre className="mt-2 whitespace-pre-wrap font-sans leading-relaxed">{text}</pre>
    </details>
  )
}

// Live markdown — rendered from the first token. Incomplete markdown mid-stream renders fine.
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
  'thinking',
  'pondering',
  'contemplating',
  'ruminating',
  'mulling it over',
  'cogitating',
  'deliberating',
  'noodling on it',
  'musing',
  'reasoning it out',
  'crunching the numbers',
  'doing the math',
  'connecting the dots',
  'weighing the options',
  'considering the angles',
  'untangling the threads',
  'piecing it together',
  'following the logic',
  'chasing a tangent',
  'questioning the future',
  'questioning everything',
  'doubting its training data',
  'interrogating reality',
  'staring into the void',
  'consulting the oracle',
  'searching the latent space',
  'warming up the neurons',
  'herding the tokens',
  'reticulating splines',
  'aligning the qubits',
  'spinning up ideas',
  'brewing a response',
  'assembling an answer',
  'sharpening the logic',
  'triangulating the truth',
  'divining an answer',
  'summoning a thought',
  'parsing the cosmos',
  'gazing into the matrix',
  'recalculating',
  'second-guessing itself',
  'overthinking it',
  'thinking very hard',
  'meditating on it',
  'wrangling probabilities',
  'following the breadcrumbs',
  'rolling the dice',
  'counting backwards from infinity',
  'having a quiet word with itself',
  'consulting the void',
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

function LiveReasoning({ text }: { text: string }) {
  const ref = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    const el = ref.current
    if (el) el.scrollTop = el.scrollHeight
  }, [text])
  return (
    <div className="rounded-2xl rounded-tl-md border border-white/5 bg-white/[0.03] px-4 py-2.5">
      <Thinking />
      {text && (
        <div
          ref={ref}
          className="mt-1.5 max-h-32 overflow-y-auto whitespace-pre-wrap text-xs leading-relaxed text-slate-500"
        >
          {text}
        </div>
      )}
    </div>
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
