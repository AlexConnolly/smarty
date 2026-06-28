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

// ---- Projects ----

export interface ProjectSummary {
  slug: string
  title: string
  description: string
  runs: number
  facts: number
}

export interface ProjectMemory {
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

export interface ProjectDetail {
  slug: string
  title: string
  description: string
  status: string
  summary?: string | null
  memories: ProjectMemory[]
  runs: ProjectRun[]
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
}

export interface GateRequest {
  taskId: string
  gateRequestId: string
  action: string
  description: string
}

export interface SessionHandlers {
  onMsgStart?: (id: number, role: string) => void
  onContent?: (id: number, text: string) => void
  onReasoning?: (id: number, text: string) => void
  onMsgEnd?: (id: number, text?: string) => void
  onWorking?: (id: string, task: string) => void
  onWorkingDone?: (id: string, status?: string) => void
  onQuestion?: (q: WorkerQuestion) => void
  onGateRequest?: (g: GateRequest) => void
  onGateResolved?: (taskId: string, gateRequestId: string, approved: boolean) => void
  onTaskProgressDigest?: (digest: { id: string; text: string; items: { id: string; description: string; summary: string }[] }) => void
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
export async function answerTask(sessionId: string, taskId: string, content: string): Promise<void> {
  const res = await fetch(`/api/session/${sessionId}/task/${encodeURIComponent(taskId)}/answer`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ content }),
  })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
}

/** Resolve a pending gate request for a task. */
export async function resolveGate(
  sessionId: string,
  taskId: string,
  gateRequestId: string,
  approved: boolean,
  rememberForTask = false,
): Promise<void> {
  const res = await fetch(
    `/api/session/${sessionId}/task/${encodeURIComponent(taskId)}/gate/${encodeURIComponent(gateRequestId)}/resolve`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ approved, rememberForTask }),
    },
  )
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
      h.onWorking?.(taskId, d.task)
      break
    case 'working_done':
      h.onWorkingDone?.(taskId, d.status)
      break
    case 'question': {
      const q = ev.data as {
        id: string
        question: string
        options?: string[]
        project?: string | null
      }
      h.onQuestion?.({
        taskId: String(q.id),
        question: q.question,
        options: q.options ?? [],
        project: q.project ?? null,
      })
      break
    }
    case 'gate_request': {
      const g = ev.data as {
        id: string
        gateRequestId: string
        action: string
        description: string
      }
      h.onGateRequest?.({
        taskId: String(g.id),
        gateRequestId: g.gateRequestId,
        action: g.action,
        description: g.description,
      })
      break
    }
    case 'gate_resolved': {
      const g = ev.data as {
        id: string
        gateRequestId: string
        approved: boolean
      }
      h.onGateResolved?.(String(g.id), g.gateRequestId, g.approved)
      break
    }
    case 'task_progress_digest': {
      const d = ev.data as {
        id: string
        text: string
        items: { id: string; description: string; summary: string }[]
      }
      h.onTaskProgressDigest?.(d)
      break;
    }
  }
}

// ---- Command Centre Interfaces & APIs ----

export interface TokenUsage {
  input: number
  output: number
  total: number
}

export interface ProgressEntry {
  timestamp: string
  message: string
}

export interface TaskDetail {
  id: string
  sessionId: string
  description: string
  project?: string | null
  persona?: string | null
  status: string
  startedAt: string
  latestThought?: string | null
  result?: string | null
  progressLog: ProgressEntry[]
}

export interface CapabilityDetail {
  id: string
  displayName: string
  requiredConfig: string[]
  optionalConfig: string[]
  promptHint?: string
  isConnected: boolean
}

export async function fetchSettings(): Promise<Record<string, string>> {
  const res = await fetch('/api/settings')
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return res.json()
}

export async function saveSettings(settings: Record<string, string>): Promise<Record<string, string>> {
  const res = await fetch('/api/settings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settings)
  })
  if (!res.ok) {
    const err = await res.json().catch(() => ({}))
    throw new Error(err.error ?? `HTTP ${res.status}`)
  }
  return res.json()
}

export async function fetchTokens(): Promise<TokenUsage> {
  const res = await fetch('/api/tokens')
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return res.json()
}

export async function resetTokens(): Promise<TokenUsage> {
  const res = await fetch('/api/tokens/reset', { method: 'POST' })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return res.json()
}

export async function fetchTasks(): Promise<TaskDetail[]> {
  const res = await fetch('/api/tasks')
  if (!res.ok) return []
  return res.json()
}

export async function cancelTaskGlobal(taskId: string): Promise<void> {
  const res = await fetch(`/api/tasks/${encodeURIComponent(taskId)}`, {
    method: 'DELETE'
  })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
}

export async function fetchCapabilities(): Promise<CapabilityDetail[]> {
  const res = await fetch('/api/capabilities')
  if (!res.ok) return []
  return res.json()
}

export interface PersonaDetail {
  id: string
  name: string
  description: string
  systemPrompt: string
  capabilityIds: string[]
}

export async function fetchPersonas(): Promise<PersonaDetail[]> {
  const res = await fetch('/api/personas')
  if (!res.ok) return []
  return res.json()
}
