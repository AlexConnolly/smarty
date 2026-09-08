import { useEffect, useState } from 'react'
import { useControlStore } from './store'
import { fetchSignInState, signIn, type SignInState } from './api'
import { cx } from './ui'
import { LiveView } from './views/LiveView'
import { ConversationDetail } from './views/ConversationDetail'
import { TasksView } from './views/TasksView'
import { FilesView } from './views/FilesView'
import { MemoriesView } from './views/MemoriesView'
import { BrainView } from './views/BrainView'
import { PersonasView } from './views/PersonasView'
import { McpView } from './views/McpView'
import { PluginsView } from './views/PluginsView'
import { WatchView } from './views/WatchView'
import { ProactView } from './views/ProactView'
import { SpendView } from './views/SpendView'

type Tab = 'live' | 'tasks' | 'proact' | 'watch' | 'files' | 'brain' | 'memories' | 'personas' | 'plugins' | 'mcp' | 'spend'

const TABS: { id: Tab; label: string; glyph: string }[] = [
  { id: 'live', label: 'Live', glyph: '◉' },
  { id: 'tasks', label: 'Tasks', glyph: '⚙' },
  { id: 'proact', label: 'Proact', glyph: '✨' },
  { id: 'watch', label: 'Watching', glyph: '📡' },
  { id: 'files', label: 'Files', glyph: '🗂' },
  { id: 'brain', label: 'Brain', glyph: '🕸' },
  { id: 'memories', label: 'Memory', glyph: '🧠' },
  { id: 'personas', label: 'Personas', glyph: '🧩' },
  { id: 'plugins', label: 'Plugins', glyph: '🧱' },
  { id: 'mcp', label: 'MCP', glyph: '🔌' },
  { id: 'spend', label: 'Spend', glyph: '💸' },
]

export default function App() {
  return <Locked />
}

/**
 * The door, in front of the command centre.
 *
 * The same gate the chat app has, for the same reason and then some: this screen shows conversations, memory, spend and
 * every connected integration. A live stream of somebody's assistant is not something to serve to whoever opened the
 * URL.
 */
function Locked() {
  const [state, setState] = useState<SignInState | null>(null)
  const [password, setPassword] = useState('')
  const [why, setWhy] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let live = true
    fetchSignInState().then((found) => live && setState(found))
    return () => {
      live = false
    }
  }, [])

  if (state === null) return null
  if (state.signedIn || !state.wanted) return <Control />

  const submit = async () => {
    if (password.length === 0 || busy) return
    setBusy(true)
    const failed = await signIn(password)
    setBusy(false)
    if (failed) {
      setWhy(failed)
      setPassword('')
      return
    }
    // The store opens a websocket on mount, so a reload is the shortest correct way to start everything with a session.
    window.location.reload()
  }

  return (
    <div className="flex min-h-screen items-center justify-center px-6">
      <div className="w-full max-w-sm">
        <div className="flex items-center gap-2">
          <span className="grid h-8 w-8 place-items-center rounded-xl bg-accent text-on-accent">◍</span>
          <div className="font-semibold">Smarty.Control</div>
        </div>
        <p className="mt-4 text-sm text-ink-soft">Enter your password to carry on.</p>
        <form
          className="mt-4 space-y-3"
          onSubmit={(e) => {
            e.preventDefault()
            void submit()
          }}
        >
          <input
            type="password"
            autoFocus
            autoComplete="current-password"
            value={password}
            onChange={(e) => {
              setPassword(e.target.value)
              setWhy(null)
            }}
            placeholder="Password"
            className="w-full rounded-xl border border-line bg-surface px-3.5 py-2.5 text-[1rem] outline-none transition focus:border-accent"
          />
          {why && <p className="text-xs text-danger">{why}</p>}
          <button
            type="submit"
            disabled={busy || password.length === 0}
            className="w-full rounded-xl bg-accent px-4 py-2.5 text-sm font-medium text-on-accent transition disabled:opacity-40"
          >
            {busy ? 'Checking…' : 'Continue'}
          </button>
        </form>
      </div>
    </div>
  )
}

function Control() {
  const state = useControlStore()
  const [tab, setTab] = useState<Tab>('live')
  const [openId, setOpenId] = useState<string | null>(null)

  const liveCount = Object.values(state.conversations).filter((c) => c.status !== 'idle').length

  return (
    <div className="min-h-full sm:flex">
      {/* Desktop sidebar */}
      <aside className="hidden w-56 shrink-0 flex-col border-r border-line bg-surface px-3 py-5 sm:flex">
        <Brand connected={state.connected} />
        <nav className="mt-6 space-y-1">
          {TABS.map((t) => (
            <button
              key={t.id}
              onClick={() => setTab(t.id)}
              className={cx(
                'flex w-full items-center gap-3 rounded-xl px-3 py-2 text-sm font-medium transition',
                tab === t.id ? 'bg-accent-soft text-accent' : 'text-ink-soft hover:bg-surface-mid',
              )}
            >
              <span className="text-base">{t.glyph}</span>
              {t.label}
              {t.id === 'live' && liveCount > 0 && (
                <span className="ml-auto rounded-full bg-live px-1.5 py-0.5 text-[0.65rem] font-semibold text-white">
                  {liveCount}
                </span>
              )}
            </button>
          ))}
        </nav>
      </aside>

      {/* Main */}
      <div className="flex min-w-0 flex-1 flex-col">
        {/* Mobile header */}
        <header className="sticky top-0 z-10 flex items-center justify-between border-b border-line bg-surface/90 px-4 py-3 backdrop-blur sm:hidden">
          <Brand connected={state.connected} />
        </header>

        {/* Every view is a reading column except the graph, which is the one thing here that wants the whole window. */}
        <main
          className={cx(
            'mx-auto w-full flex-1 px-4 py-5 pb-24 sm:pb-8',
            tab === 'brain' ? 'max-w-none' : 'max-w-reading',
          )}
        >
          {/* The nav's own label, not the tab id: "watch" and "Watching" being two different words for the same
              screen is the sort of thing that reads as two screens. */}
          <h1 className="mb-4 hidden text-xl font-semibold capitalize sm:block">
            {TABS.find((t) => t.id === tab)?.label ?? tab}
          </h1>
          {tab === 'live' && <LiveView state={state} onOpen={setOpenId} />}
          {tab === 'tasks' && <TasksView state={state} onOpen={setOpenId} />}
          {tab === 'proact' && <ProactView />}
          {tab === 'watch' && <WatchView onOpen={setOpenId} />}
          {tab === 'files' && <FilesView />}
          {tab === 'brain' && <BrainView />}
          {tab === 'memories' && <MemoriesView />}
          {tab === 'personas' && <PersonasView />}
          {tab === 'plugins' && <PluginsView />}
          {tab === 'mcp' && <McpView />}
          {tab === 'spend' && <SpendView />}
        </main>
      </div>

      {/* Mobile bottom nav */}
      <nav className="fixed inset-x-0 bottom-0 z-10 flex border-t border-line bg-surface/95 backdrop-blur sm:hidden">
        {TABS.map((t) => (
          <button
            key={t.id}
            onClick={() => setTab(t.id)}
            className={cx(
              'flex flex-1 flex-col items-center gap-0.5 py-2 text-[0.65rem] font-medium',
              tab === t.id ? 'text-accent' : 'text-ink-mute',
            )}
          >
            <span className="relative text-lg">
              {t.glyph}
              {t.id === 'live' && liveCount > 0 && (
                <span className="absolute -right-2 -top-1 h-2 w-2 rounded-full bg-live" />
              )}
            </span>
            {t.label}
          </button>
        ))}
      </nav>

      {openId && <ConversationDetail id={openId} state={state} onClose={() => setOpenId(null)} />}
    </div>
  )
}

function Brand({ connected }: { connected: boolean }) {
  return (
    <div className="flex items-center gap-2">
      <span className="grid h-8 w-8 place-items-center rounded-xl bg-accent text-on-accent">◍</span>
      <div className="leading-tight">
        <div className="font-semibold">Smarty.Control</div>
        <div className="flex items-center gap-1 text-[0.65rem] text-ink-mute">
          <span className={cx('h-1.5 w-1.5 rounded-full', connected ? 'bg-live' : 'bg-idle')} />
          {connected ? 'live' : 'connecting…'}
        </div>
      </div>
    </div>
  )
}
