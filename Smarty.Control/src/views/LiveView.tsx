import type { ControlState } from '../store'
import { timeAgo } from '../api'
import { Card, EmptyState, Pill, StatusDot, SurfaceBadge } from '../ui'
import { Timeline } from '../Timeline'

export function LiveView({ state, onOpen }: { state: ControlState; onOpen: (id: string) => void }) {
  const convs = Object.values(state.conversations).sort(
    (a, b) => new Date(b.lastActivityAt).getTime() - new Date(a.lastActivityAt).getTime(),
  )
  const active = convs.filter((c) => c.status !== 'idle')
  const quiet = convs.filter((c) => c.status === 'idle')

  if (convs.length === 0)
    return (
      <EmptyState
        icon="◍"
        title="No conversations yet"
        hint="When Smarty handles a chat or a Slack thread, it shows up here — live, as it happens."
      />
    )

  return (
    <div className="space-y-6">
      {active.length > 0 && (
        <section>
          <div className="mb-2 flex items-center gap-2 px-1">
            <h2 className="text-sm font-semibold uppercase tracking-wide text-ink-mute">Happening now</h2>
            <Pill tone="live">{active.length} live</Pill>
          </div>
          <div className="space-y-3">
            {active.map((c) => {
              const items = (state.timelines[c.id] ?? []).slice(-6)
              return (
                <Card key={c.id} className="overflow-hidden">
                  <button onClick={() => onOpen(c.id)} className="flex w-full items-center gap-2 px-4 pt-3.5 text-left">
                    <StatusDot status={c.status} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-semibold">{c.title}</span>
                      <span className="flex items-center gap-2 text-xs text-ink-mute">
                        <SurfaceBadge surface={c.surface} />
                        {c.subtitle && <span className="truncate">{c.subtitle}</span>}
                        {c.project && <Pill tone="accent">{c.project}</Pill>}
                      </span>
                    </span>
                    <span className="shrink-0 text-xs text-ink-mute">{timeAgo(c.lastActivityAt)}</span>
                  </button>
                  <div className="mt-2 max-h-80 overflow-y-auto px-4 pb-4">
                    {items.length > 0 ? (
                      <Timeline items={items} />
                    ) : (
                      <div className="py-2 text-sm text-ink-mute">Working…</div>
                    )}
                  </div>
                </Card>
              )
            })}
          </div>
        </section>
      )}

      {quiet.length > 0 && (
        <section>
          <h2 className="mb-2 px-1 text-sm font-semibold uppercase tracking-wide text-ink-mute">Recent</h2>
          <div className="space-y-2">
            {quiet.map((c) => (
              <Card key={c.id} onClick={() => onOpen(c.id)} className="flex items-center gap-3 px-4 py-3">
                <StatusDot status={c.status} />
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-medium">{c.title}</span>
                  <span className="flex items-center gap-2 text-xs text-ink-mute">
                    <SurfaceBadge surface={c.surface} />
                    {c.subtitle && <span className="truncate">{c.subtitle}</span>}
                  </span>
                </span>
                <span className="shrink-0 text-xs text-ink-mute">{timeAgo(c.lastActivityAt)}</span>
              </Card>
            ))}
          </div>
        </section>
      )}
    </div>
  )
}
