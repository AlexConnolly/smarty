import { useEffect, useRef, useState } from 'react'
import {
  ConversationDetail as Detail,
  answerTask,
  cancelTask,
  fetchConversation,
  timeAgo,
} from '../api'
import type { ControlState } from '../store'
import { Button, Markdown, Pill, Spinner, StatusDot, SurfaceBadge, cx } from '../ui'
import { Timeline } from '../Timeline'

export function ConversationDetail({
  id,
  state,
  onClose,
}: {
  id: string
  state: ControlState
  onClose: () => void
}) {
  const [detail, setDetail] = useState<Detail | null>(null)
  const [loading, setLoading] = useState(true)
  const scroller = useRef<HTMLDivElement>(null)

  const conv = state.conversations[id]
  const live = state.timelines[id] ?? []
  const hasLive = live.length > 0
  const isChat = (conv?.surface ?? detail?.summary.surface) === 'chat'

  const load = async () => {
    setDetail(await fetchConversation(id))
    setLoading(false)
  }
  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  // Follow the live stream to the bottom while it grows.
  useEffect(() => {
    const el = scroller.current
    if (el && hasLive) el.scrollTop = el.scrollHeight
  }, [state.version, hasLive])

  const title = conv?.title ?? detail?.summary.title ?? '(conversation)'
  const surface = conv?.surface ?? detail?.summary.surface ?? 'chat'
  const status = conv?.status ?? detail?.summary.status ?? 'idle'

  const onAnswer = async (taskId: string, text: string) => {
    if (await answerTask(id, taskId, text)) {
      /* the answer rides back on the live stream */
    }
  }
  const onCancel = async (taskId: string) => {
    await cancelTask(id, taskId)
    void load()
  }

  return (
    <div className="fixed inset-0 z-30 flex flex-col bg-bg sm:left-auto sm:w-[min(680px,100vw)] sm:border-l sm:border-line sm:shadow-ambient">
      <header className="flex items-center gap-3 border-b border-line bg-surface/90 px-4 py-3 backdrop-blur">
        <Button variant="ghost" size="sm" onClick={onClose} className="shrink-0">
          ✕
        </Button>
        <div className="min-w-0 flex-1">
          <div className="truncate font-semibold">{title}</div>
          <div className="flex items-center gap-2 text-xs text-ink-mute">
            <SurfaceBadge surface={surface} />
            <StatusDot status={status} label />
          </div>
        </div>
      </header>

      <div ref={scroller} className="flex-1 overflow-y-auto px-4 py-4">
        {/* Runs / tasks summary */}
        {detail && detail.runs.length > 0 && (
          <div className="mb-4 space-y-2">
            {detail.runs.map((r) => (
              <div key={r.id} className="rounded-xl border border-line bg-surface px-3 py-2.5">
                <div className="flex items-center gap-2">
                  <Pill
                    tone={
                      r.status === 'running' ? 'live' : r.status === 'waiting' ? 'wait' : r.status === 'done' ? 'neutral' : 'danger'
                    }
                  >
                    {r.status}
                  </Pill>
                  {r.persona && <Pill tone="accent">{r.persona}</Pill>}
                  <span className="min-w-0 flex-1 truncate text-sm">{r.task}</span>
                  {(r.status === 'running' || r.status === 'waiting') && isChat && (
                    <Button variant="danger" size="sm" onClick={() => onCancel(r.taskId)}>
                      Stop
                    </Button>
                  )}
                </div>
                {r.pendingQuestion && <div className="mt-1 text-xs text-wait">❓ {r.pendingQuestion}</div>}
              </div>
            ))}
          </div>
        )}

        {/* The conversation itself: live timeline if we have it, else the reconstructed history. */}
        {hasLive ? (
          <Timeline items={live} onAnswer={onAnswer} />
        ) : loading ? (
          <div className="flex justify-center py-10">
            <Spinner />
          </div>
        ) : (
          <HistoryView detail={detail} />
        )}
      </div>
    </div>
  )
}

function HistoryView({ detail }: { detail: Detail | null }) {
  if (!detail || (detail.transcript.length === 0 && detail.runs.every((r) => r.steps.length === 0)))
    return <div className="py-10 text-center text-sm text-ink-mute">No recorded history for this conversation.</div>

  return (
    <div className="space-y-3">
      {detail.transcript.map((m, i) => (
        <div key={i} className={cx('flex', m.role === 'user' ? 'justify-end' : 'justify-start')}>
          <div
            className={cx(
              'max-w-[88%] rounded-2xl px-3.5 py-2.5',
              m.role === 'user' ? 'bg-surface-low' : 'border border-line/60 bg-surface shadow-card',
            )}
          >
            <Markdown>{m.text}</Markdown>
            <div className="mt-1 text-[0.65rem] text-ink-mute">{timeAgo(m.at)}</div>
          </div>
        </div>
      ))}

      {detail.runs.map(
        (r) =>
          r.steps.length > 0 && (
            <details key={r.id} className="rounded-xl border border-line bg-surface px-3 py-2">
              <summary className="cursor-pointer text-sm font-medium">
                Run steps — <span className="text-ink-mute">{r.task}</span>
              </summary>
              <div className="mt-2 space-y-1.5 text-xs">
                {r.steps.map((s, i) => (
                  <div key={i} className="rounded-lg bg-surface-low/60 p-2">
                    {s.kind === 'tool' ? (
                      <>
                        <span className="font-mono text-ink">{s.tool}</span>
                        {s.args && <div className="mt-0.5 truncate text-ink-mute">{s.args}</div>}
                        {s.result && <div className="mt-1 line-clamp-3 whitespace-pre-wrap text-ink-soft">{s.result}</div>}
                      </>
                    ) : (
                      <span className="text-ink-soft">{s.text}</span>
                    )}
                  </div>
                ))}
              </div>
            </details>
          ),
      )}
    </div>
  )
}
