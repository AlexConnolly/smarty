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
export async function transcribe(wav: Blob): Promise<string> {
  const form = new FormData()
  form.append('audio', wav, 'note.wav')
  const res = await fetch('/api/transcribe', { method: 'POST', body: form })
  const data = await res.json().catch(() => ({}))
  if (!res.ok || data.error) throw new Error(data.error ?? `HTTP ${res.status}`)
  return (data.text ?? '').trim()
}

export interface SessionHandlers {
  onMsgStart?: (id: number, role: string) => void
  onContent?: (id: number, text: string) => void
  onReasoning?: (id: number, text: string) => void
  onMsgEnd?: (id: number, text?: string) => void
  onWorking?: (id: string, task: string) => void
  onWorkingDone?: (id: string) => void
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

/**
 * Open the session's persistent event stream and keep it open, transparently reconnecting on drops.
 * Everything the assistant says — instant acks AND results pushed back asynchronously from background
 * workers — arrives here. The stream never ends on its own; it runs until `signal` is aborted.
 */
export async function openSessionStream(
  sessionId: string,
  handlers: SessionHandlers,
  signal: AbortSignal,
): Promise<void> {
  let received = 0

  while (!signal.aborted) {
    try {
      const res = await fetch(`/api/session/${sessionId}?from=${received}`, { signal })
      if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`)

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
          dispatch(ev, handlers)
        }
      }
    } catch {
      if (signal.aborted) return
    }
    if (signal.aborted) return
    await delay(500) // the session lives on; reconnect from where we left off
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

function dispatch(ev: Frame, h: SessionHandlers): void {
  const d = ev.data as { id: number; role: string; text: string; task: string }
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
      h.onWorking?.(taskId, d.task)
      break
    case 'working_done':
      h.onWorkingDone?.(taskId)
      break
  }
}
