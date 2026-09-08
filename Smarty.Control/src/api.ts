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

/** An MCP server's live state. `error` is why it isn't connected — the most useful field on the page. */
export interface McpServerStatus {
  name: string
  command: string
  enabled: boolean
  connected: boolean
  serverName?: string | null
  serverVersion?: string | null
  protocolVersion?: string | null
  toolPrefix: string
  tools: string[]
  offeredTools: string[]
  functions: string[]
  error?: string | null
}

/** The editable form. Env KEYS come back; values never do, so a saved token isn't re-sent to the browser. */
export interface McpServerConfigView {
  name: string
  command: string
  args: string[]
  cwd?: string | null
  envKeys: string[]
  enabled: boolean
  prefix?: string | null
  tools: string[]
  functions: string[]
  promptHint?: string | null
  startupTimeoutSeconds: number
  callTimeoutSeconds: number
  maxResultChars: number
}

/** A server Smarty already knows how to run. `ready` = the machine-specific bit was found, so it's a plain toggle. */
export interface McpCatalogueEntry {
  key: string
  name: string
  summary: string
  function: string
  command: string
  requires: string
  homepage?: string | null
  pathArgument?: string | null
  discoveredPath?: string | null
  ready: boolean
  installed: boolean
  enabled: boolean
  connected: boolean
  toolCount: number
  error?: string | null
}

export interface McpSaveResult {
  saved: boolean
  connected: boolean
  tools: string[]
  offered: string[]
  error?: string | null
  message: string
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

/** One field of the setup stage a plugin is waiting on. */
export interface PluginFieldView {
  name: string
  type: string
  description: string
  required: boolean
  secret: boolean
}

/** The step a plugin is waiting on. What has already been answered is never sent back. */
export interface PluginStageView {
  id: string
  title: string
  instruction?: string | null
  fields: PluginFieldView[]
  problem?: string | null
}

export interface PluginParamView {
  name: string
  type: string
  description: string
  required: boolean
}

/** A command, and the tool name the model will actually call it by. */
export interface PluginCommandView {
  name: string
  tool: string
  description: string
  parameters: PluginParamView[]
}

/** An installed plugin's whole state: whether it loaded, why not if it didn't, what it wants told, what it does. */
export interface PluginStatus {
  id: string
  name: string
  description: string
  enabled: boolean
  loaded: boolean
  /** Setup is finished, so its commands and persona are live. */
  ready: boolean
  error?: string | null
  capabilityId: string
  personaId: string
  /** What setup still wants. Null once there is nothing left to ask. */
  setup?: PluginStageView | null
  commands: PluginCommandView[]
  installed: string
}

export interface PluginResult {
  ok: boolean
  message: string
  plugin?: PluginStatus | null
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
export const fetchMcpServers = () => getJson<McpServerStatus[]>('/api/control/mcp', [])
export const fetchMcpConfig = (name: string) =>
  getJson<McpServerConfigView | null>(`/api/control/mcp/${encodeURIComponent(name)}/config`, null)

export async function saveMcpServer(body: {
  name: string
  command: string
  args: string[]
  env: Record<string, string>
  cwd?: string
  enabled: boolean
  prefix?: string
  tools: string[]
  functions: string[]
  promptHint?: string
}): Promise<McpSaveResult> {
  try {
    const res = await fetch('/api/control/mcp', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    const data = await res.json()
    if (!res.ok) return { saved: false, connected: false, tools: [], offered: [], message: data?.error ?? `HTTP ${res.status}` }
    return data as McpSaveResult
  } catch (e) {
    return { saved: false, connected: false, tools: [], offered: [], message: String(e) }
  }
}

export const deleteMcpServer = (name: string) =>
  fetch(`/api/control/mcp/${encodeURIComponent(name)}`, { method: 'DELETE' })

export const fetchMcpCatalogue = () => getJson<McpCatalogueEntry[]>('/api/control/mcp/catalogue', [])

/** Turn a built-in on. `path` is only needed when the server reported `ready: false`. */
export async function installMcpBuiltIn(key: string, path?: string): Promise<McpSaveResult> {
  try {
    const res = await fetch(`/api/control/mcp/catalogue/${encodeURIComponent(key)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path: path ?? null }),
    })
    const data = await res.json()
    if (!res.ok)
      return { saved: false, connected: false, tools: [], offered: [], message: data?.error ?? `HTTP ${res.status}` }
    return data as McpSaveResult
  } catch (e) {
    return { saved: false, connected: false, tools: [], offered: [], message: String(e) }
  }
}

/** Flip a server on or off without discarding it or any edits made to it. */
export async function setMcpEnabled(name: string, enabled: boolean): Promise<{ message: string; connected: boolean }> {
  try {
    const res = await fetch(`/api/control/mcp/${encodeURIComponent(name)}/enabled`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    })
    if (!res.ok) return { message: `HTTP ${res.status}`, connected: false }
    return await res.json()
  } catch (e) {
    return { message: String(e), connected: false }
  }
}

export async function reconnectMcpServer(name: string): Promise<{ connected: boolean; tools: string[]; error?: string | null }> {
  try {
    const res = await fetch(`/api/control/mcp/${encodeURIComponent(name)}/reconnect`, { method: 'POST' })
    if (!res.ok) return { connected: false, tools: [], error: `HTTP ${res.status}` }
    return await res.json()
  } catch (e) {
    return { connected: false, tools: [], error: String(e) }
  }
}

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

export const fetchPlugins = () => getJson<PluginStatus[]>('/api/control/plugins', [])

/** Upload a packaged plugin. The reply is the whole outcome — including the reason a zip was rejected. */
export async function uploadPlugin(file: File): Promise<PluginResult> {
  try {
    const form = new FormData()
    form.append('file', file, file.name)
    const res = await fetch('/api/control/plugins', { method: 'POST', body: form })
    const data = await res.json()
    return { ok: false, message: `HTTP ${res.status}`, ...data } as PluginResult
  } catch (e) {
    return { ok: false, message: String(e) }
  }
}

/** Answer the setup stage a plugin is waiting on. */
export async function submitPluginSetup(
  id: string,
  stage: string,
  values: Record<string, string>,
): Promise<PluginResult> {
  return post(`/api/control/plugins/${encodeURIComponent(id)}/setup`, { stage, values })
}

/** Forget what it learned and start setup again. */
export async function resetPluginSetup(id: string): Promise<PluginResult> {
  return post(`/api/control/plugins/${encodeURIComponent(id)}/setup/reset`, {})
}

export async function setPluginEnabled(id: string, enabled: boolean): Promise<PluginResult> {
  return post(`/api/control/plugins/${encodeURIComponent(id)}/enabled`, { enabled })
}

export async function deletePlugin(id: string): Promise<PluginResult> {
  try {
    const res = await fetch(`/api/control/plugins/${encodeURIComponent(id)}`, { method: 'DELETE' })
    const data = await res.json()
    return { ok: false, message: `HTTP ${res.status}`, ...data } as PluginResult
  } catch (e) {
    return { ok: false, message: String(e) }
  }
}

async function post(url: string, body: unknown): Promise<PluginResult> {
  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    const data = await res.json()
    return { ok: false, message: `HTTP ${res.status}`, ...data } as PluginResult
  } catch (e) {
    return { ok: false, message: String(e) }
  }
}

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

      // A proxy that buffers event-streams (a Cloudflare quick tunnel does) leaves this hanging forever with an
      // empty live view. The server writes padding immediately, so no first byte means no streaming — fall back
      // to refreshing the snapshot on a timer, which is less live but actually shows something.
      const opening = await Promise.race([reader.read(), delay(StreamProbeMs).then(() => 'timeout' as const)])
      if (opening === 'timeout') {
        void reader.cancel().catch(() => {})
        await pollControlSnapshot(handlers, signal)
        return
      }
      if (opening.done) throw new Error('stream closed immediately')
      buffer += decoder.decode(opening.value, { stream: true })

      const drain = () => {
        let split: number
        while ((split = buffer.indexOf('\n\n')) !== -1) {
          const frame = buffer.slice(0, split)
          buffer = buffer.slice(split + 2)
          dispatch(frame, handlers)
        }
      }
      drain()

      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        drain()
      }
    } catch {
      if (signal.aborted) return
    }
    handlers.onConnected?.(false)
    if (signal.aborted) return
    await delay(800)
  }
}

/** How long to wait for the first byte before deciding this connection isn't really streaming. */
const StreamProbeMs = 4000

/**
 * The fallback when streaming is being buffered: re-read the same snapshot the stream opens with, on a timer.
 * Every view here is driven by conversations + runs, so a periodic snapshot keeps the whole dashboard usable —
 * it just updates in steps rather than live.
 */
async function pollControlSnapshot(handlers: StreamHandlers, signal: AbortSignal): Promise<void> {
  while (!signal.aborted) {
    const [conversations, runs] = await Promise.all([fetchConversations(), fetchTasks()])
    handlers.onSnapshot?.({ conversations, runs })
    handlers.onConnected?.(true)
    await delay(2500)
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

/**
 * A slug written the way a person reads it: hyphens back to spaces, first letter up.
 *
 * For values that ARE slugs — a project key, a node id — never for a title somebody typed. A real title may contain a
 * hyphen on purpose, and rewriting that is changing what they wrote rather than tidying it up.
 */
export function readable(slug: string | null | undefined): string {
  const text = (slug ?? '').replace(/[-_]/g, ' ').trim()
  return text.length === 0 ? '' : text[0].toUpperCase() + text.slice(1)
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

// ===== spend =====

export interface SpendByModel {
  model: string
  priceKnown: boolean
  price?: { inputPerMillion: number; outputPerMillion: number; cachedInputPerMillion: number } | null
  runs: number
  calls: number
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  reasoningTokens: number
  cost: number
}

export interface SpendRun {
  id: string
  taskId: string
  task: string
  surface: string
  persona?: string | null
  status: string
  startedAt: string
  cost: number
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  models: {
    model: string
    calls: number
    cost: number
    priceKnown: boolean
    inputTokens: number
    cachedInputTokens: number
    outputTokens: number
    reasoningTokens: number
  }[]
}

export interface SpendReport {
  total: {
    cost: number
    costKnown: boolean
    runs: number
    inputTokens: number
    cachedInputTokens: number
    outputTokens: number
    reasoningTokens: number
    cacheHitRate: number
  }
  byModel: SpendByModel[]
  topRuns: SpendRun[]
}

export const fetchSpend = () =>
  getJson<SpendReport | null>('/api/control/spend', null)

/** Money, at the scale agent runs actually cost — fractions of a cent are the norm, so don't round them away. */
export function money(usd: number): string {
  if (usd === 0) return '$0'
  if (usd < 0.01) return `$${usd.toFixed(4)}`
  if (usd < 1) return `$${usd.toFixed(3)}`
  return `$${usd.toFixed(2)}`
}

export function tokens(n: number): string {
  if (n < 1000) return String(n)
  if (n < 1_000_000) return `${(n / 1000).toFixed(1)}k`
  return `${(n / 1_000_000).toFixed(2)}M`
}

// ---- the brain, as something to look at ----

export type BrainNode = {
  id: string
  name: string
  kind: string
  aliases: string[]
  created: string
  connections: number
}

export type BrainEdge = {
  id: string
  from: string
  fromId: string
  relation: string
  to: string | null
  toId: string | null
  value: string
  note: string | null
  /** active | superseded | ended — the dead are included on purpose; they carry the reason. */
  state: string
  because: string | null
  asserted: string
  ended: string | null
  source: string | null
  audience: string | null
}

/**
 * Two names that might be one thing, waiting on an answer only the user can give.
 *
 * "My brother is Matthew", and later "Matt says he can drive". Inventing a second person splits every question about him
 * in half; assuming they are one records facts against somebody who may not exist. So the graph asks — and holds the
 * question on the node, which is why it is still here after a restart.
 */
export interface BrainMaybe {
  id: string
  name: string
  kind: string
  connections: number
  otherId: string
  other: string
  otherConnections: number
}

export type BrainView = {
  kinds: { kind: string; count: number }[]
  nodes: BrainNode[]
  edges: BrainEdge[]
  maybes: BrainMaybe[]
  loose: { id: string; name: string }[]
  /** Files and notes kept against a node. Returned by the endpoint all along and simply never typed here. */
  context: {
    id: string
    node: string
    sort: string
    name: string
    readable: boolean
    bytes: number
    added: string
  }[]
  duplicates: { a: string; b: string; why: string }[]
  waiting: { id: string; text: string; retract: boolean; filed: string; tried: number; error: string | null }[]
}

export const fetchBrain = () =>
  getJson<BrainView | null>('/api/control/brain', null)

/**
 * Delete a node and every edge touching it.
 *
 * Erased rather than retired, unlike forgetting a single fact: a node removed by hand is one that should not have
 * existed, and keeping its edges as history would leave the graph still answering with it.
 */
export async function deleteBrainNode(id: string): Promise<{ name: string; edges: number } | null> {
  try {
    const res = await fetch(`/api/control/brain/node/${encodeURIComponent(id)}`, { method: 'DELETE' })
    if (!res.ok) return null
    return (await res.json()) as { name: string; edges: number }
  } catch {
    return null
  }
}

/** Empty the brain. The confirm phrase is required by the server, so this cannot happen by a stray call. */
export async function wipeBrain(): Promise<{ nodes: number; edges: number; waiting: number; files: number } | null> {
  try {
    const res = await fetch('/api/control/brain?confirm=wipe', { method: 'DELETE' })
    if (!res.ok) return null
    return (await res.json()) as { nodes: number; edges: number; waiting: number; files: number }
  } catch {
    return null
  }
}

// ---- Feeds and watchers -------------------------------------------------------------------------------------
//
// The ingest half. A feed is an address on a timer that produces items with topics; a watcher is a filter over those
// items and an instruction to carry out when one matches. This page exists because both fail QUIETLY: a feed whose
// source moved keeps its cadence and produces nothing, and a watcher whose filter never matches looks identical to one
// that is patiently waiting. The counts and the last error are the whole point.

export interface FeedItemView {
  key: string
  topic: string
  title: string
  url?: string | null
  at?: string | null
  arrived: string
}

export interface FeedView {
  id: string
  name: string
  source: string
  url?: string | null
  every: string
  split: string
  paused: boolean
  /** How many items it has ever produced — the honest measure of whether it works. */
  seen: number
  /** Every topic it has produced, which is what a watcher can be scoped to. */
  topics: string[]
  polledAt?: string | null
  nextPoll?: string | null
  error?: string | null
  items: FeedItemView[]
}

export interface WatchTestView {
  field: string
  op: string
  value: string
}

export interface WatcherView {
  id: string
  name: string
  feed?: string | null
  feedName?: string | null
  topic?: string | null
  when: WatchTestView[]
  /** A judgement, when a test could not express it. Costs a model call per candidate item. */
  about?: string | null
  /** What it does when it fires. */
  act: string
  paused: boolean
  limit: number
  fired: number
  lastFiredAt?: string | null
  lastError?: string | null
}

/** One time a watcher went off, and the conversation it started. */
export interface WatchFireView {
  watcher: string
  watcherName: string
  session: string
  title: string
  topic: string
  url?: string | null
  at: string
  opened: boolean
}

export const fetchFeeds = () => getJson<FeedView[]>('/api/feeds', [])

export const fetchWatchers = () =>
  getJson<{ watchers: WatcherView[]; fires: WatchFireView[] }>('/api/watchers', { watchers: [], fires: [] })

/** Look now, whatever the cadence says — and act on anything new, exactly as the tick would. */
export async function pollFeed(id: string): Promise<{ items: number; error?: string | null } | null> {
  try {
    const res = await fetch(`/api/feeds/${encodeURIComponent(id)}/poll`, { method: 'POST' })
    if (!res.ok) return null
    return (await res.json()) as { items: number; error?: string | null }
  } catch {
    return null
  }
}

export async function setFeedPaused(id: string, paused: boolean): Promise<boolean> {
  try {
    const res = await fetch(`/api/feeds/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ paused }),
    })
    return res.ok
  } catch {
    return false
  }
}

export async function deleteFeed(id: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/feeds/${encodeURIComponent(id)}`, { method: 'DELETE' })
    return res.ok
  } catch {
    return false
  }
}

export async function setWatcherPaused(id: string, paused: boolean): Promise<boolean> {
  try {
    const res = await fetch(`/api/watchers/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ paused }),
    })
    return res.ok
  } catch {
    return false
  }
}

export async function deleteWatcher(id: string): Promise<boolean> {
  try {
    const res = await fetch(`/api/watchers/${encodeURIComponent(id)}`, { method: 'DELETE' })
    return res.ok
  } catch {
    return false
  }
}

/**
 * Answer an identity question.
 *
 * Yes merges them and the name in doubt becomes another name for the survivor, so it resolves for ever afterwards. No
 * records that they are different, so it is never asked again — which is what makes asking bearable at all.
 */
export async function settleSame(id: string, other: string, same: boolean): Promise<boolean> {
  try {
    const res = await fetch('/api/control/brain/same', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id, other, same }),
    })
    return res.ok
  } catch {
    return false
  }
}

// ---- Signing in -------------------------------------------------------------------------------------------

export interface SignInState {
  wanted: boolean
  signedIn: boolean
  waiting: boolean
}

export const fetchSignInState = () =>
  getJson<SignInState>('/api/auth/state', { wanted: true, signedIn: false, waiting: false })

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

// ---- Proact -------------------------------------------------------------------------------------------------
//
// Its own area rather than a corner of an existing one: the home page is live state and a nudge means "the thing you
// asked to be told about", where this is a history of what it did on its own initiative. See PROACT_SPEC.md.

export interface ProactActionView {
  id: string
  at: string
  /** noticed | prepared | proposed | looked */
  kind: string
  /** attend | discover — so the deep dives are visible as such. */
  mode: string
  what: string
  why: string
  produced?: string | null
  /** Proposals only: the steps that run if it is ticked. */
  plan?: string | null
  /** Proposals only: what cannot be undone, and by when it has to be decided. */
  risk?: string | null
  expires?: string | null
  /** pending | ticked | crossed | expired */
  answer?: string | null
  answeredAt?: string | null
  /** What came of a ticked proposal. A click that appears to do nothing teaches you never to click again. */
  outcome?: string | null
  session?: string | null
  task?: string | null
  seen: boolean
}

export interface ProactTickView {
  at: string
  mode: string
  /** False means the tick cost nothing at all — which is how a five-minute cadence stays affordable. */
  changed: boolean
  acted: number
  note?: string | null
  error?: string | null
}

export interface ProactView {
  on: boolean
  every: string
  intervals: string[]
  pausedUntil?: string | null
  running: boolean
  /** With no cap on how much it may do, this is the instrument rather than a statistic. */
  todayCount: number
  waiting: number
  /** The hour the day gets rounded up, local. Null means the roundup is off. */
  roundupHour?: number | null
  actions: ProactActionView[]
  ticks: ProactTickView[]
}

const NO_PROACT: ProactView = {
  on: false,
  every: 'every 10 minutes',
  intervals: [],
  running: false,
  todayCount: 0,
  waiting: 0,
  actions: [],
  ticks: [],
}

export const fetchProact = () => getJson<ProactView>('/api/proact', NO_PROACT)

async function patchProact(body: unknown): Promise<boolean> {
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

export const setProactOn = (on: boolean) => patchProact({ on })
export const setProactEvery = (every: string) => patchProact({ every })
/** 0 clears a pause; anything else is "not now" for that many hours. */
export const pauseProact = (hours: number) => patchProact({ pauseHours: hours })

/**
 * Look now, whatever the dial says. Returns whether it went off and did something.
 *
 * `deep` asks for the expensive look on purpose — otherwise it is only reachable by waiting six hours for it to come
 * round, which makes the mode where the interesting findings live effectively untestable.
 */
export async function proactLookNow(deep = false): Promise<boolean | null> {
  try {
    const res = await fetch(`/api/proact/look${deep ? '?deep=true' : ''}`, { method: 'POST' })
    if (!res.ok) return null
    return ((await res.json()) as { acted: boolean }).acted
  } catch {
    return null
  }
}

/** The tick or the cross. A tick hands the plan to the assistant; nothing else is offered. */
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

/**
 * Start the day again.
 *
 * Everything that stops Proact repeating itself is built on what it did earlier, so a wasted morning suppresses a
 * useful afternoon — a subject covered badly is covered all the same. Destructive, which is why it lives here on the
 * settings page and not on the timeline.
 */
export async function clearProact(all = false): Promise<{ actions: number; ticks: number } | null> {
  try {
    const res = await fetch('/api/proact/clear', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ all }),
    })
    if (!res.ok) return null
    return (await res.json()) as { actions: number; ticks: number }
  } catch {
    return null
  }
}

export async function markProactSeen(): Promise<void> {
  try {
    await fetch('/api/proact/seen', { method: 'POST' })
  } catch {
    /* best effort — a read receipt is not worth an error */
  }
}
