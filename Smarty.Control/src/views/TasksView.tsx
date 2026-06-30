import { useState } from 'react'
import { cancelTask, timeAgo } from '../api'
import type { ControlState } from '../store'
import { Button, Card, EmptyState, Pill, SurfaceBadge } from '../ui'

const ACTIVE = new Set(['running', 'waiting'])

export function TasksView({ state, onOpen }: { state: ControlState; onOpen: (id: string) => void }) {
  const [filter, setFilter] = useState<'all' | 'active' | 'done'>('all')

  let runs = Object.values(state.runs)
  if (filter === 'active') runs = runs.filter((r) => ACTIVE.has(r.status))
  if (filter === 'done') runs = runs.filter((r) => !ACTIVE.has(r.status))
  runs.sort((a, b) => {
    const aw = ACTIVE.has(a.status) ? 1 : 0
    const bw = ACTIVE.has(b.status) ? 1 : 0
    if (aw !== bw) return bw - aw
    return new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime()
  })

  const tone = (s: string) =>
    s === 'running' ? 'live' : s === 'waiting' ? 'wait' : s === 'done' ? 'neutral' : 'danger'

  return (
    <div className="space-y-3">
      <div className="flex gap-2">
        {(['all', 'active', 'done'] as const).map((f) => (
          <button
            key={f}
            onClick={() => setFilter(f)}
            className={
              'rounded-full px-3 py-1 text-xs font-medium capitalize ' +
              (filter === f ? 'bg-accent text-on-accent' : 'bg-surface-low text-ink-soft hover:bg-surface-mid')
            }
          >
            {f}
          </button>
        ))}
      </div>

      {runs.length === 0 ? (
        <EmptyState icon="⚙" title="No tasks" hint="Background work Smarty delegates — running and past — appears here." />
      ) : (
        runs.map((r) => (
          <Card key={r.id} className="px-4 py-3">
            <div className="flex items-center gap-2">
              <Pill tone={tone(r.status) as 'live' | 'wait' | 'neutral' | 'danger'}>{r.status}</Pill>
              {r.persona && <Pill tone="accent">{r.persona}</Pill>}
              <SurfaceBadge surface={r.surface} />
              <span className="ml-auto text-xs text-ink-mute">
                {r.endedAt ? `ended ${timeAgo(r.endedAt)}` : `started ${timeAgo(r.startedAt)}`}
              </span>
            </div>
            <div className="mt-1.5 text-sm">{r.task || '(no description)'}</div>
            {r.latestNote && <div className="mt-1 text-xs text-ink-mute">{r.latestNote}</div>}
            {r.pendingQuestion && <div className="mt-1 text-xs text-wait">❓ {r.pendingQuestion}</div>}
            <div className="mt-2 flex items-center gap-3 text-xs text-ink-mute">
              <button onClick={() => onOpen(r.conversationId)} className="text-accent hover:underline">
                open conversation →
              </button>
              <span>{r.steps} step{r.steps === 1 ? '' : 's'}</span>
              {ACTIVE.has(r.status) && r.surface === 'chat' && (
                <Button variant="danger" size="sm" className="ml-auto" onClick={() => cancelTask(r.conversationId, r.taskId)}>
                  Stop
                </Button>
              )}
            </div>
          </Card>
        ))
      )}
    </div>
  )
}
