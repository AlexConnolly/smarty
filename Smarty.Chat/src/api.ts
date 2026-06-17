export type Role = 'user' | 'assistant' | 'system'

export interface ChatMessage {
  role: Role
  content: string
}

export interface ChatRequest {
  system?: string
  model?: string
  enableTools?: boolean
  messages: ChatMessage[]
}

export interface StreamHandlers {
  onRun?: (runId: string) => void
  /** Fired once when a resume connection succeeds, before replay — clear and rebuild from it. */
  onResumed?: () => void
  onContent?: (text: string) => void
  onContentCleared?: () => void
  onReasoning?: (text: string) => void
  onToolStarted?: (name: string, args: string) => void
  onToolCompleted?: (name: string, result: string) => void
  onError?: (message: string) => void
  onDone?: () => void
}

/** Resume an existing background run instead of starting a new one. */
export interface Resume {
  runId: string
  from: number
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms))

/** Fetch the list of model names from the API (proxied to Ollama). */
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

/** Send a WAV voice note to the API and get back the transcribed text (local Whisper). */
export async function transcribe(wav: Blob): Promise<string> {
  const form = new FormData()
  form.append('audio', wav, 'note.wav')
  const res = await fetch('/api/transcribe', { method: 'POST', body: form })
  const data = await res.json().catch(() => ({}))
  if (!res.ok || data.error) throw new Error(data.error ?? `HTTP ${res.status}`)
  return (data.text ?? '').trim()
}

/** Ask the server to stop a background run (the Stop button). */
export async function cancelRun(runId: string): Promise<void> {
  try {
    await fetch(`/api/chat/${runId}`, { method: 'DELETE' })
  } catch {
    /* ignore */
  }
}

/**
 * Start (or resume) a background run and stream its events. The run lives on the server independent
 * of this connection, so a dropped network / backgrounded tab does NOT stop it — we just silently
 * reconnect from where we left off. A real server-side error surfaces via onError; onDone fires when
 * the run has truly finished.
 */
export async function streamChat(
  body: ChatRequest,
  handlers: StreamHandlers,
  signal?: AbortSignal,
  resume?: Resume,
): Promise<void> {
  let runId = resume?.runId ?? ''
  let received = resume?.from ?? 0

  // 1) Create the run (unless resuming an existing one).
  if (!runId) {
    let res: Response
    try {
      res = await fetch('/api/chat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
        signal,
      })
    } catch (err) {
      handlers.onError?.(err instanceof Error ? err.message : 'request failed')
      return
    }
    if (!res.ok) {
      handlers.onError?.(`HTTP ${res.status}`)
      return
    }
    try {
      runId = (await res.json()).runId
    } catch {
      handlers.onError?.('unexpected response')
      return
    }
    handlers.onRun?.(runId)
  }

  // 2) Subscribe, transparently reconnecting on transient drops, until the run is done or we abort.
  let firstConnect = true
  while (true) {
    let finished = false
    try {
      const res = await fetch(`/api/chat/${runId}?from=${received}`, { signal })
      if (res.status === 404) {
        // The run is gone (almost always: the API was restarted, or it aged out). This is not an
        // error worth shouting about — just stop quietly and leave the chat as it is.
        handlers.onDone?.()
        return
      }
      if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`)

      if (firstConnect) {
        firstConnect = false
        if (resume) handlers.onResumed?.() // rebuild the message from a clean replay
      }

      const reader = res.body.getReader()
      const decoder = new TextDecoder()
      let buffer = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })

        let split: number
        while ((split = buffer.indexOf('\n\n')) !== -1) {
          const frame = buffer.slice(0, split)
          buffer = buffer.slice(split + 2)
          const ev = parseFrame(frame)
          if (!ev) continue
          received++ // every buffered event counts, so reconnect offsets stay exact
          if (ev.event === 'done') {
            finished = true
            break
          }
          dispatch(ev, handlers)
        }
        if (finished) break
      }
    } catch (err) {
      if (signal?.aborted) return
      // Transient network drop — wait briefly and reconnect from `received`. The run kept going.
      await delay(700)
      continue
    }

    if (finished) {
      handlers.onDone?.()
      return
    }
    if (signal?.aborted) return
    // Connection closed without the terminal event (e.g. idle proxy timeout) — reconnect.
    await delay(300)
  }
}

interface Frame {
  event: string
  data: Record<string, string>
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

function dispatch(ev: Frame, h: StreamHandlers): void {
  switch (ev.event) {
    case 'content':
      h.onContent?.(ev.data.text)
      break
    case 'content_cleared':
      h.onContentCleared?.()
      break
    case 'reasoning':
      h.onReasoning?.(ev.data.text)
      break
    case 'tool_started':
      h.onToolStarted?.(ev.data.name, ev.data.arguments)
      break
    case 'tool_completed':
      h.onToolCompleted?.(ev.data.name, ev.data.result)
      break
    case 'error':
      h.onError?.(ev.data.message)
      break
    // 'completed' / 'cancelled' are informational; 'done' is handled by the caller.
  }
}
