import { useEffect, useReducer, useRef } from 'react'
import {
  ActivityFrame,
  ConversationSummary,
  RunSummary,
  StreamSnapshot,
  openControlStream,
} from './api'

// A reconstructed item in a conversation's live timeline — the shape the UI renders.
export type TimelineItem =
  | { kind: 'message'; key: string; role: string; text: string; live: boolean }
  | { kind: 'reasoning'; key: string; text: string }
  | { kind: 'tool'; key: string; taskId: string; name: string; args?: string; result?: string; done: boolean }
  | { kind: 'task'; key: string; taskId: string; desc: string; status: string }
  | { kind: 'question'; key: string; taskId: string; question: string; options: string[] }
  | { kind: 'file'; key: string; name: string; caption?: string }

export interface ControlState {
  connected: boolean
  conversations: Record<string, ConversationSummary>
  runs: Record<string, RunSummary>
  timelines: Record<string, TimelineItem[]>
  version: number
}

type Action =
  | { type: 'connected'; ok: boolean }
  | { type: 'snapshot'; snap: StreamSnapshot }
  | { type: 'activity'; frame: ActivityFrame }

const MAX_TIMELINE = 300

const str = (d: Record<string, unknown>, k: string): string =>
  d[k] == null ? '' : typeof d[k] === 'string' ? (d[k] as string) : String(d[k])

function reduce(state: ControlState, action: Action): ControlState {
  switch (action.type) {
    case 'connected':
      return { ...state, connected: action.ok }

    case 'snapshot': {
      const conversations: Record<string, ConversationSummary> = {}
      for (const c of action.snap.conversations) conversations[c.id] = c
      const runs: Record<string, RunSummary> = { ...state.runs }
      for (const r of action.snap.runs) runs[r.id] = r
      return { ...state, conversations, runs, version: state.version + 1 }
    }

    case 'activity': {
      const f = action.frame
      const id = f.conversationId
      const conversations = { ...state.conversations }
      const prev = conversations[id]
      conversations[id] = {
        ...(prev ?? blankConversation(id, f)),
        status: f.status,
        title: f.title ?? prev?.title ?? '(new conversation)',
        project: f.project ?? prev?.project ?? null,
        surface: f.surface,
        lastActivityAt: f.ts,
      }

      const timelines = { ...state.timelines }
      const items = applyToTimeline(timelines[id] ? [...timelines[id]] : [], f)
      timelines[id] = items.length > MAX_TIMELINE ? items.slice(items.length - MAX_TIMELINE) : items

      const runs = applyToRuns({ ...state.runs }, f)

      return { ...state, conversations, timelines, runs, version: state.version + 1 }
    }
  }
}

function blankConversation(id: string, f: ActivityFrame): ConversationSummary {
  return {
    id,
    surface: f.surface,
    title: f.title ?? '(new conversation)',
    status: f.status,
    messageCount: 0,
    startedAt: f.ts,
    lastActivityAt: f.ts,
  }
}

function applyToTimeline(items: TimelineItem[], f: ActivityFrame): TimelineItem[] {
  const d = f.data
  const cid = f.conversationId
  const replace = (key: string, fn: (it: TimelineItem) => TimelineItem) => {
    const i = items.findIndex((it) => it.key === key)
    if (i >= 0) items[i] = fn(items[i])
    return i >= 0
  }

  switch (f.event) {
    case 'msg_start': {
      const key = `m:${cid}:${str(d, 'id')}`
      if (!items.some((it) => it.key === key))
        items.push({ kind: 'message', key, role: str(d, 'role') || 'assistant', text: '', live: true })
      break
    }
    case 'content': {
      const key = `m:${cid}:${str(d, 'id')}`
      if (!replace(key, (it) => (it.kind === 'message' ? { ...it, text: it.text + str(d, 'text') } : it)))
        items.push({ kind: 'message', key, role: 'assistant', text: str(d, 'text'), live: true })
      break
    }
    case 'reasoning': {
      const key = `r:${cid}:${str(d, 'id')}`
      if (!replace(key, (it) => (it.kind === 'reasoning' ? { ...it, text: it.text + str(d, 'text') } : it)))
        items.push({ kind: 'reasoning', key, text: str(d, 'text') })
      break
    }
    case 'msg_end': {
      const key = `m:${cid}:${str(d, 'id')}`
      const text = str(d, 'text')
      replace(key, (it) =>
        it.kind === 'message' ? { ...it, text: text.length >= it.text.length ? text : it.text, live: false } : it,
      )
      break
    }
    case 'working': {
      const taskId = str(d, 'id')
      const key = `t:${cid}:${taskId}`
      if (!replace(key, (it) => (it.kind === 'task' ? { ...it, status: 'running' } : it)))
        items.push({ kind: 'task', key, taskId, desc: str(d, 'task'), status: 'running' })
      break
    }
    case 'working_done': {
      const taskId = str(d, 'id')
      replace(`t:${cid}:${taskId}`, (it) => (it.kind === 'task' ? { ...it, status: str(d, 'status') } : it))
      break
    }
    case 'tool_started': {
      items.push({
        kind: 'tool',
        key: `tool:${cid}:${f.seq}`,
        taskId: str(d, 'id'),
        name: str(d, 'name'),
        args: str(d, 'arguments'),
        done: false,
      })
      break
    }
    case 'tool_completed': {
      const taskId = str(d, 'id')
      const name = str(d, 'name')
      // Resolve the most recent matching unfinished tool call.
      for (let i = items.length - 1; i >= 0; i--) {
        const it = items[i]
        if (it.kind === 'tool' && it.taskId === taskId && it.name === name && !it.done) {
          items[i] = { ...it, result: str(d, 'result'), done: true }
          return items
        }
      }
      items.push({ kind: 'tool', key: `tool:${cid}:${f.seq}`, taskId, name, result: str(d, 'result'), done: true })
      break
    }
    case 'question': {
      const taskId = str(d, 'id')
      const options = Array.isArray(d.options) ? (d.options as string[]) : []
      items.push({ kind: 'question', key: `q:${cid}:${f.seq}`, taskId, question: str(d, 'question'), options })
      break
    }
    case 'file': {
      items.push({ kind: 'file', key: `file:${cid}:${f.seq}`, name: str(d, 'name'), caption: str(d, 'caption') })
      break
    }
  }
  return items
}

function applyToRuns(runs: Record<string, RunSummary>, f: ActivityFrame): Record<string, RunSummary> {
  const d = f.data
  const taskId = str(d, 'id')
  if (!taskId && f.event !== 'tool_started' && f.event !== 'tool_completed') return runs
  const runId = `${f.conversationId}#${taskId}`
  const existing = runs[runId]

  const ensure = (): RunSummary =>
    existing ?? {
      id: runId,
      conversationId: f.conversationId,
      surface: f.surface,
      taskId,
      task: '',
      project: f.project ?? null,
      persona: null,
      status: 'running',
      startedAt: f.ts,
      endedAt: null,
      steps: 0,
    }

  switch (f.event) {
    case 'working':
      runs[runId] = { ...ensure(), task: str(d, 'task') || ensure().task, persona: str(d, 'persona') || existing?.persona || null, status: 'running', endedAt: null }
      break
    case 'progress':
      runs[runId] = { ...ensure(), latestNote: str(d, 'note') }
      break
    case 'tool_started':
    case 'tool_completed':
      if (existing) runs[runId] = { ...existing, steps: existing.steps + (f.event === 'tool_started' ? 1 : 0) }
      break
    case 'question':
      runs[runId] = { ...ensure(), status: 'waiting', pendingQuestion: str(d, 'question') }
      break
    case 'working_done': {
      const status = str(d, 'status')
      runs[runId] = { ...ensure(), status, endedAt: status === 'waiting' ? null : f.ts }
      break
    }
  }
  return runs
}

const initial: ControlState = {
  connected: false,
  conversations: {},
  runs: {},
  timelines: {},
  version: 0,
}

/** Subscribe to the live control stream for the lifetime of the app, maintaining derived state. */
export function useControlStore(): ControlState {
  const [state, dispatch] = useReducer(reduce, initial)
  const started = useRef(false)

  useEffect(() => {
    if (started.current) return
    started.current = true
    const ac = new AbortController()
    openControlStream(
      {
        onConnected: (ok) => dispatch({ type: 'connected', ok }),
        onSnapshot: (snap) => dispatch({ type: 'snapshot', snap }),
        onActivity: (frame) => dispatch({ type: 'activity', frame }),
      },
      ac.signal,
    )
    return () => ac.abort()
  }, [])

  return state
}
