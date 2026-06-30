// ===== Smarty.Control API client: types, REST calls, and the live SSE stream. =====

export type Surface = 'chat' | 'slack'
export type ConvStatus = 'idle' | 'thinking' | 'working' | 'waiting'

export interface ConversationSummary {
  id: string
  surface: Surface
  title: string
  subtitle?: string | null
  project?: string | null
  persona?: string | null
  userName?: string | null
  status: ConvStatus
  messageCount: number
  startedAt: string
  lastActivityAt: string
}

export interface RunSummary {
  id: string
  conversationId: string
  surface: Surface
  taskId: string
  task: string
  project?: string | null
  persona?: string | null
  status: string
  latestNote?: string | null
  pendingQuestion?: string | null
  result?: string | null
  startedAt: string
  endedAt?: string | null
  steps: number
}

export interface RunStepView {
  kind: string
  text?: string | null
  tool?: string | null
  args?: string | null
  result?: string | null
}

export interface ConversationDetail {
  summary: ConversationSummary
  files: string[]
  transcript: { role: string; text: string; at: string }[]
  runs: {
    id: string
    taskId: string
    task: string
    persona?: string | null
    status: string
    latestNote?: string | null
    pendingQuestion?: string | null
    result?: string | null
    startedAt: string
    endedAt?: string | null
    steps: RunStepView[]
  }[]
}

export interface MemoryFactView {
  id: string
  type: string
  key: string
  value: string
  context?: string | null
  scope?: string | null
  asserted: string
}

export interface ToolParamMeta {
  name: string
  type: string
  description: string
  required: boolean
}
export interface ToolMeta {
  name: string
  description: string
  parameters: ToolParamMeta[]
}
export interface CapabilityMeta {
  id: string
  displayName: string
  configured: boolean
  requiredConfig: string[]
  tools: ToolMeta[]
}
export interface PersonaView {
  id: string
  name: string
  description: string
  builtin: boolean
  capabilityIds: string[]
  tools: ToolMeta[]
}

export interface BucketFile {
  name: string
  size: number
  modified: string
}
export interface BucketInfo {
  kind: string
  id: string
  label: string
  files: BucketFile[]
}

async function getJson<T>(url: string, fallback: T): Promise<T> {
  try {
    const res = await fetch(url)
    if (!res.ok) return fallback
    return (await res.json()) as T
  } catch {
    return fallback
  }
}

export const fetchConversations = () =>
  getJson<ConversationSummary[]>('/api/control/conversations', [])
export const fetchConversation = (id: string) =>
  getJson<ConversationDetail | null>(`/api/control/conversations/${encodeURIComponent(id)}`, null)
export const fetchTasks = (status?: string) =>
  getJson<RunSummary[]>(`/api/control/tasks${status ? `?status=${encodeURIComponent(status)}` : ''}`, [])
export const fetchMemories = () => getJson<MemoryFactView[]>('/api/control/memories', [])
export const fetchPersonas = () => getJson<PersonaView[]>('/api/control/personas', [])
export const fetchCapabilities = () => getJson<CapabilityMeta[]>('/api/control/capabilities', [])
export const fetchBuckets = () => getJson<BucketInfo[]>('/api/control/buckets', [])

export async function cancelTask(conversationId: string, taskId: string): Promise<void> {
  await fetch(
    `/api/control/conversations/${encodeURIComponent(conversationId)}/tasks/${encodeURIComponent(taskId)}`,
    { method: 'DELETE' },
  )
}

/** Answer a worker that paused to ask a question (local chat conversations only — reuses the session API). */
export async function answerTask(conversationId: string, taskId: string, content: string): Promise<boolean> {
  try {
    const res = await fetch(
      `/api/session/${encodeURIComponent(conversationId)}/task/${encodeURIComponent(taskId)}/answer`,
      { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ content }) },
    )
    return res.ok
  } catch {
    return false
  }
}

export async function addMemory(body: {
  type: string
  key: string
  value: string
  context?: string
  scope?: string
}): Promise<void> {
  await fetch('/api/control/memories', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

export const retireMemory = (id: string) =>
  fetch(`/api/control/memories/${encodeURIComponent(id)}`, { method: 'DELETE' })

export async function savePersona(body: {
  id?: string
  name: string
  description: string
  capabilityIds: string[]
}): Promise<PersonaView | null> {
  const res = await fetch('/api/control/personas', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!res.ok) return null
  return (await res.json()) as PersonaView
}

export const deletePersona = (id: string) =>
  fetch(`/api/control/personas/${encodeURIComponent(id)}`, { method: 'DELETE' })

export async function uploadToBucket(
  kind: string,
  id: string,
  files: FileList | File[],
): Promise<boolean> {
  const form = new FormData()
  for (const f of Array.from(files)) form.append('files', f, f.name)
  const res = await fetch(
    `/api/control/buckets/${encodeURIComponent(kind)}/${encodeURIComponent(id || 'global')}/files`,
    { method: 'POST', body: form },
  )
  return res.ok
}

export const deleteBucketFile = (kind: string, id: string, name: string) =>
  fetch(
    `/api/control/buckets/${encodeURIComponent(kind)}/${encodeURIComponent(id || 'global')}/files/${name
      .split('/')
      .map(encodeURIComponent)
      .join('/')}`,
    { method: 'DELETE' },
  )

export const bucketFileUrl = (kind: string, id: string, name: string) =>
  `/api/control/buckets/${encodeURIComponent(kind)}/${encodeURIComponent(id || 'global')}/files/${name
    .split('/')
    .map(encodeURIComponent)
    .join('/')}`

// ===== live activity stream =====

export interface ActivityFrame {
  seq: number
  conversationId: string
  surface: Surface
  title?: string | null
  status: ConvStatus
  project?: string | null
  event: string
  data: Record<string, unknown>
  ts: string
}

export interface StreamSnapshot {
  conversations: ConversationSummary[]
  runs: RunSummary[]
}

export interface StreamHandlers {
  onSnapshot?: (s: StreamSnapshot) => void
  onActivity?: (f: ActivityFrame) => void
  onConnected?: (ok: boolean) => void
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms))

/** Open the global control stream and keep it open, reconnecting transparently. Runs until `signal` aborts. */
export async function openControlStream(handlers: StreamHandlers, signal: AbortSignal): Promise<void> {
  while (!signal.aborted) {
    try {
      const res = await fetch('/api/control/stream', { signal })
      if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`)
      handlers.onConnected?.(true)

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
          dispatch(frame, handlers)
        }
      }
    } catch {
      if (signal.aborted) return
    }
    handlers.onConnected?.(false)
    if (signal.aborted) return
    await delay(800)
  }
}

function dispatch(frame: string, h: StreamHandlers): void {
  let event = 'message'
  let raw = ''
  for (const line of frame.split('\n')) {
    if (line.startsWith('event:')) event = line.slice(6).trim()
    else if (line.startsWith('data:')) raw += line.slice(5).trim()
  }
  if (!raw) return
  try {
    const data = JSON.parse(raw)
    if (event === 'snapshot') h.onSnapshot?.(data as StreamSnapshot)
    else if (event === 'activity') h.onActivity?.(data as ActivityFrame)
  } catch {
    /* ignore malformed frame */
  }
}

export function timeAgo(iso: string): string {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return ''
  const s = Math.max(0, Math.round((Date.now() - then) / 1000))
  if (s < 5) return 'just now'
  if (s < 60) return `${s}s ago`
  const m = Math.round(s / 60)
  if (m < 60) return `${m}m ago`
  const h = Math.round(m / 60)
  if (h < 24) return `${h}h ago`
  return `${Math.round(h / 24)}d ago`
}
