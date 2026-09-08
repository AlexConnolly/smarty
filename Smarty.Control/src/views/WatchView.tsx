import { useEffect, useState } from 'react'
import {
  FeedView,
  WatchFireView,
  WatcherView,
  deleteFeed,
  deleteWatcher,
  fetchFeeds,
  fetchWatchers,
  pollFeed,
  setFeedPaused,
  setWatcherPaused,
} from '../api'
import { Button, Card, EmptyState, Pill, SectionTitle, Spinner, cx } from '../ui'

/**
 * What arrives, and what happens about it.
 *
 * This page exists because both halves fail SILENTLY. A feed whose source moved keeps its cadence and produces
 * nothing; a watcher whose filter never matches is indistinguishable from one patiently waiting for the right thing.
 * Neither raises anything, and the first sign of trouble is a week later, when somebody notices they were never told.
 *
 * So the numbers are the page: how many items a feed has ever produced, what it last said if it failed, how many times
 * a watcher has fired, and which conversations it started. Everything here is read off the same stores the tick uses,
 * so what is on screen is what is actually running.
 */
export function WatchView({ onOpen }: { onOpen?: (sessionId: string) => void }) {
  const [feeds, setFeeds] = useState<FeedView[] | null>(null)
  const [watchers, setWatchers] = useState<WatcherView[]>([])
  const [fires, setFires] = useState<WatchFireView[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [said, setSaid] = useState<string | null>(null)

  const load = async () => {
    const [f, w] = await Promise.all([fetchFeeds(), fetchWatchers()])
    setFeeds(f)
    setWatchers(w.watchers)
    setFires(w.fires)
  }

  useEffect(() => {
    void load()
    // Polled rather than streamed: a feed's cadence is minutes, so a refresh every fifteen seconds is as live as the
    // thing being watched.
    const timer = setInterval(() => void load(), 15000)
    return () => clearInterval(timer)
  }, [])

  const look = async (feed: FeedView) => {
    setBusy(feed.id)
    const result = await pollFeed(feed.id)
    setBusy(null)
    setSaid(
      result === null
        ? `Couldn't reach ${feed.name}.`
        : result.error
          ? `${feed.name}: ${result.error}`
          : `${feed.name} — ${result.items} item(s) on the page.`,
    )
    await load()
  }

  const pauseFeed = async (feed: FeedView) => {
    setBusy(feed.id)
    await setFeedPaused(feed.id, !feed.paused)
    setBusy(null)
    await load()
  }

  const dropFeed = async (feed: FeedView) => {
    const watching = watchers.filter((w) => w.feed === feed.id)
    // Named rather than counted: "2 watchers" tells you nothing about whether you mind losing what they were for.
    const warning = watching.length > 0
      ? `\n\nThese are watching it and will stop: ${watching.map((w) => w.name).join(', ')}.`
      : ''
    if (!confirm(`Delete the feed "${feed.name}"?${warning}`)) return
    setBusy(feed.id)
    await deleteFeed(feed.id)
    setBusy(null)
    await load()
  }

  const pauseWatcher = async (w: WatcherView) => {
    setBusy(w.id)
    await setWatcherPaused(w.id, !w.paused)
    setBusy(null)
    await load()
  }

  const dropWatcher = async (w: WatcherView) => {
    if (!confirm(`Stop watching for "${w.name}"?`)) return
    setBusy(w.id)
    await deleteWatcher(w.id)
    setBusy(null)
    await load()
  }

  if (feeds === null) {
    return (
      <div className="flex justify-center py-16">
        <Spinner />
      </div>
    )
  }

  if (feeds.length === 0 && watchers.length === 0) {
    return (
      <EmptyState
        icon="📡"
        title="Nothing is being watched"
        hint="Ask in a chat to be told when something happens — a reply, a price, a status page — and the feed and the watcher are set up in the background."
      />
    )
  }

  return (
    <div className="space-y-6">
      {said && (
        <Card className="border-accent/40 bg-accent-soft px-4 py-2.5">
          <div className="flex items-center justify-between gap-3">
            <span className="text-sm text-ink-soft">{said}</span>
            <Button variant="ghost" size="sm" onClick={() => setSaid(null)}>
              Dismiss
            </Button>
          </div>
        </Card>
      )}

      <section>
        <SectionTitle>Watching for</SectionTitle>
        {watchers.length === 0 ? (
          <Card className="px-4 py-3 text-sm text-ink-mute">
            Feeds are arriving, but nothing is watching them — so nothing will happen when something does.
          </Card>
        ) : (
          <div className="space-y-3">
            {watchers.map((w) => (
              <Card key={w.id} className="px-4 py-3">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-semibold">{w.name}</span>
                      {w.paused ? <Pill tone="neutral">paused</Pill> : <Pill tone="live">on</Pill>}
                      {w.fired > 0 ? (
                        <Pill tone="accent">fired {w.fired}×</Pill>
                      ) : (
                        <Pill tone="neutral">never fired</Pill>
                      )}
                    </div>

                    {/* What it does, which is the half a person actually cares about. */}
                    <p className="mt-1 text-sm text-ink-soft">{w.act}</p>

                    {/* And the filter, in the order it runs: feed, topic, tests — free — then a judgement, which is
                        the only part that costs anything. */}
                    <div className="mt-2 flex flex-wrap items-center gap-1.5 text-[0.7rem] text-ink-mute">
                      <span className="font-medium">{w.feedName ?? (w.feed ? w.feed : 'every feed')}</span>
                      {w.topic && <Pill>topic: {w.topic}</Pill>}
                      {w.when.map((t, i) => (
                        <Pill key={i}>
                          {t.field || 'anything'} {t.op} {t.value}
                        </Pill>
                      ))}
                      {w.about && <Pill tone="wait">reads it: {w.about}</Pill>}
                      <span>· at most {w.limit}/hour</span>
                    </div>

                    {w.lastError && <p className="mt-1 text-xs text-danger">{w.lastError}</p>}
                    {w.lastFiredAt && (
                      <p className="mt-1 text-xs text-ink-mute">last fired {when(w.lastFiredAt)}</p>
                    )}
                  </div>

                  <div className="flex shrink-0 items-center gap-2">
                    <Button variant="soft" size="sm" onClick={() => void pauseWatcher(w)}>
                      {w.paused ? 'Resume' : 'Pause'}
                    </Button>
                    <Button variant="danger" size="sm" onClick={() => void dropWatcher(w)}>
                      Delete
                    </Button>
                  </div>
                </div>
              </Card>
            ))}
          </div>
        )}
      </section>

      <section>
        <SectionTitle>Feeds</SectionTitle>
        {feeds.length === 0 ? (
          <Card className="px-4 py-3 text-sm text-ink-mute">No feeds yet.</Card>
        ) : (
          <div className="space-y-3">
            {feeds.map((f) => (
              <Card key={f.id} className="px-4 py-3">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-semibold">{f.name}</span>
                      <Pill tone="accent">{f.source}</Pill>
                      {f.paused && <Pill tone="neutral">paused</Pill>}
                      {f.error ? <Pill tone="danger">failing</Pill> : <Pill tone="live">{f.seen} seen</Pill>}
                    </div>

                    {f.url && <p className="mt-1 truncate font-mono text-xs text-ink-mute">{f.url}</p>}

                    <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-ink-mute">
                      <span>{f.every}</span>
                      <span>split by {f.split}</span>
                      {f.polledAt && <span>looked {when(f.polledAt)}</span>}
                      {!f.paused && f.nextPoll && <span>next {when(f.nextPoll)}</span>}
                      {watchers.filter((w) => w.feed === f.id).length === 0 && (
                        <span className="text-wait">nothing is watching this</span>
                      )}
                      {/* A split that is not splitting. Publishing refuses this outright, but a source can stop
                          carrying the field later — and then every item quietly files under the feed's own name and
                          every watcher on a topic goes deaf. */}
                      {f.split === 'field' && f.seen > 0 && f.topics.length <= 1 && f.topics[0] === f.name && (
                        <span className="text-danger">its topic field has stopped arriving — one topic for everything</span>
                      )}
                    </div>

                    {f.error && <p className="mt-1 text-xs text-danger">{f.error}</p>}

                    {/* The topics, because they are what a watcher can be scoped to — and a feed whose topics are all
                        one string is a feed whose split is not working. */}
                    {f.topics.length > 0 && (
                      <div className="mt-2 flex flex-wrap gap-1.5">
                        {f.topics.slice(0, 10).map((t) => (
                          <Pill key={t}>{t}</Pill>
                        ))}
                        {f.topics.length > 10 && <Pill tone="neutral">+{f.topics.length - 10} more</Pill>}
                      </div>
                    )}

                    {/* And what it has actually brought in, which is the only way to tell a working feed from a
                        well-configured one. */}
                    {f.items.length > 0 && (
                      <ul className="mt-2 space-y-0.5">
                        {f.items.slice(0, 4).map((i) => (
                          <li key={i.key} className="flex items-baseline gap-2 text-xs">
                            <span className="shrink-0 text-ink-mute">{i.topic}</span>
                            <span className="min-w-0 flex-1 truncate text-ink-soft">{i.title || i.key}</span>
                            <span className="shrink-0 text-ink-mute">{when(i.arrived)}</span>
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>

                  <div className="flex shrink-0 items-center gap-2">
                    <Button variant="soft" size="sm" onClick={() => void look(f)} disabled={busy === f.id}>
                      {busy === f.id ? 'Looking…' : 'Look now'}
                    </Button>
                    <Button variant="soft" size="sm" onClick={() => void pauseFeed(f)}>
                      {f.paused ? 'Resume' : 'Pause'}
                    </Button>
                    <Button variant="danger" size="sm" onClick={() => void dropFeed(f)}>
                      Delete
                    </Button>
                  </div>
                </div>
              </Card>
            ))}
          </div>
        )}
      </section>

      {fires.length > 0 && (
        <section>
          <SectionTitle>What it has started</SectionTitle>
          <div className="space-y-2">
            {fires.slice(0, 15).map((fire) => (
              <Card
                key={`${fire.session}-${fire.at}`}
                className={cx('px-4 py-2.5', !fire.opened && 'border-accent/40')}
                onClick={onOpen ? () => onOpen(fire.session) : undefined}
              >
                <div className="flex flex-wrap items-baseline justify-between gap-2">
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-sm text-ink">{fire.title || fire.topic}</div>
                    <div className="text-xs text-ink-mute">{fire.watcherName}</div>
                  </div>
                  <div className="flex shrink-0 items-center gap-2">
                    {!fire.opened && <Pill tone="accent">unread</Pill>}
                    <span className="text-xs text-ink-mute">{when(fire.at)}</span>
                  </div>
                </div>
              </Card>
            ))}
          </div>
        </section>
      )}
    </div>
  )
}

/** Relative for anything recent, a time for today, a date beyond that — and future instants read as "in …". */
function when(iso: string): string {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return ''
  const secs = (then - Date.now()) / 1000
  const ahead = secs > 0
  const s = Math.abs(secs)
  const say =
    s < 60 ? 'less than a minute'
      : s < 3600 ? `${Math.round(s / 60)}m`
      : s < 86400 ? `${Math.round(s / 3600)}h`
      : `${Math.round(s / 86400)}d`
  return ahead ? `in ${say}` : `${say} ago`
}
