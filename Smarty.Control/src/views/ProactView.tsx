import { useEffect, useState } from 'react'
import {
  clearProact,
  fetchProact,
  pauseProact,
  proactLookNow,
  setProactEvery,
  setProactOn,
  type ProactView as Proact,
} from '../api'
import { Button, Card, Pill, SectionTitle, Spinner, cx } from '../ui'

/**
 * Proact's settings, and whether it is still alive.
 *
 * What it DID is not here. That moved to the front page of the chat app, where the person actually is — nobody opens
 * an admin surface to find out that two things in their Thursday are in different cities, and a decision waiting on
 * a tick belongs in front of them rather than behind a tab they visit when something is broken.
 *
 * What belongs here is what Control is for: the switch, the cadence, and the two operational questions nothing else
 * can answer.
 *
 *  - IS IT ALIVE? A silent Proact and a broken one look identical from outside, and this system has already shipped
 *    that confusion twice. Hence every look it took, whether it did anything or not.
 *  - IS IT DOING TOO MUCH? Nothing caps how much it may do, so the count is the instrument rather than a statistic.
 *    A figure climbing here is the only early warning of the failure that actually kills the feature.
 */
export function ProactView() {
  const [state, setState] = useState<Proact | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [said, setSaid] = useState<string | null>(null)

  const load = async () => setState(await fetchProact())

  useEffect(() => {
    void load()
    // Polled rather than streamed: the fastest cadence on offer is five minutes.
    const timer = setInterval(() => void load(), 15000)
    return () => clearInterval(timer)
  }, [])

  /**
   * Wipe the day and let it start again.
   *
   * Confirmed first, because it destroys the record — and worth having because every restraint is built on that
   * record: a subject it covered this morning stays covered, useful or not.
   */
  const clear = async () => {
    if (!window.confirm("Clear today's Proact record? It loses what it did and what it looked at, and it will be "
        + 'free to revisit anything from today.')) return

    setBusy('clear')
    const gone = await clearProact()
    setBusy(null)
    setSaid(gone === null
      ? "Couldn't clear it."
      : `Cleared ${gone.actions} thing(s) it did and ${gone.ticks} look(s). It can start again from here.`)
    await load()
  }

  const look = async (deep: boolean) => {
    setBusy(deep ? 'deep' : 'look')
    const acted = await proactLookNow(deep)
    setBusy(null)
    setSaid(
      acted === null
        ? "Couldn't look just now."
        : acted
          ? 'It found something and has gone to do it — it will show up on the home page.'
          : 'It looked, and there was nothing worth doing.',
    )
    await load()
  }

  if (!state) return <Spinner />

  return (
    <div className="space-y-6">
      {said && (
        <Card className="flex items-center justify-between gap-3">
          <span className="text-sm text-ink">{said}</span>
          <Button variant="ghost" size="sm" onClick={() => setSaid(null)}>
            Dismiss
          </Button>
        </Card>
      )}

      <Card className="space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <h2 className="text-base font-semibold text-ink">Proact</h2>
              <Pill tone={state.running ? 'live' : 'neutral'}>
                {state.running ? 'On' : state.on ? 'Paused' : 'Off'}
              </Pill>
            </div>
            <p className="mt-1 text-xs text-ink-mute">
              Does things without being asked. It never sends, buys, books or cancels anything — those go to the home
              page as a yes or a no.
            </p>
          </div>
          <Button
            variant={state.on ? 'soft' : 'primary'}
            size="sm"
            onClick={async () => {
              await setProactOn(!state.on)
              await load()
            }}
          >
            {state.on ? 'Turn off' : 'Turn on'}
          </Button>
        </div>

        {/* The dial. With no cap on how much it may do, this is the rate control — and it belongs to the person who
            has to read the output rather than to a constant in the source. */}
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs font-medium text-ink-soft">Goes out</span>
          {state.intervals.map((i) => (
            <button
              key={i}
              onClick={async () => {
                await setProactEvery(i)
                await load()
              }}
              className={cx(
                'rounded-full px-3 py-1 text-xs font-medium transition',
                i === state.every ? 'bg-accent text-white' : 'bg-surface-low text-ink-soft hover:text-ink',
              )}
            >
              {i.replace('every ', '')}
            </button>
          ))}
        </div>

        <div className="flex flex-wrap items-center gap-4 border-t border-line pt-3 text-xs text-ink-mute">
          <span>
            <span className="font-semibold tabular-nums text-ink">{state.todayCount}</span> today
          </span>
          {state.waiting > 0 && (
            <span>
              <span className="font-semibold tabular-nums text-ink">{state.waiting}</span> waiting on the home page
            </span>
          )}
          {state.roundupHour != null && <span>rounds up at {String(state.roundupHour).padStart(2, '0')}:00</span>}
          {state.pausedUntil && <span>paused until {new Date(state.pausedUntil).toLocaleTimeString()}</span>}

          <div className="ml-auto flex gap-2">
            <Button variant="soft" size="sm" onClick={() => void look(false)} disabled={busy !== null}>
              {busy === 'look' ? 'Looking…' : 'Look now'}
            </Button>
            {/* The deep look is otherwise only reachable by waiting six hours for it to come round. */}
            <Button variant="soft" size="sm" onClick={() => void look(true)} disabled={busy !== null}>
              {busy === 'deep' ? 'Digging…' : 'Have a proper dig'}
            </Button>
            {state.on && !state.pausedUntil && (
              <Button
                variant="ghost"
                size="sm"
                onClick={async () => {
                  await pauseProact(12)
                  await load()
                }}
              >
                Not today
              </Button>
            )}
            {state.pausedUntil && (
              <Button
                variant="ghost"
                size="sm"
                onClick={async () => {
                  await pauseProact(0)
                  await load()
                }}
              >
                Resume
              </Button>
            )}
            {/* Destructive, so it sits here with the settings rather than on the timeline. */}
            <Button variant="danger" size="sm" onClick={() => void clear()} disabled={busy !== null}>
              {busy === 'clear' ? 'Clearing…' : "Clear today"}
            </Button>
          </div>
        </div>
      </Card>

      {/* Liveness, which is the whole reason this page still exists. */}
      <section className="space-y-3">
        <SectionTitle>Every time it looked</SectionTitle>
        <Card className="divide-y divide-line p-0">
          {state.ticks.length === 0 ? (
            <p className="p-4 text-xs text-ink-mute">
              {state.on ? 'It has not looked yet.' : 'Proact is off, so it is not looking.'}
            </p>
          ) : (
            state.ticks.map((t, i) => (
              <div key={i} className="flex items-baseline gap-3 px-4 py-2 text-xs">
                <span className="tabular-nums text-ink-mute">{new Date(t.at).toLocaleTimeString()}</span>
                {t.mode === 'discover' && <Pill tone="neutral">deep</Pill>}
                <span className={cx('flex-1', t.error ? 'text-danger' : 'text-ink-soft')}>
                  {t.error ??
                    (t.acted > 0 ? t.note : !t.changed ? 'nothing had changed' : (t.note ?? 'nothing worth doing'))}
                </span>
                {t.acted > 0 && <Pill tone="accent">acted</Pill>}
              </div>
            ))
          )}
        </Card>
      </section>
    </div>
  )
}
