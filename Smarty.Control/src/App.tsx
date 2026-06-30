import { useState } from 'react'
import { useControlStore } from './store'
import { cx } from './ui'
import { LiveView } from './views/LiveView'
import { ConversationDetail } from './views/ConversationDetail'
import { TasksView } from './views/TasksView'
import { FilesView } from './views/FilesView'
import { MemoriesView } from './views/MemoriesView'
import { PersonasView } from './views/PersonasView'

type Tab = 'live' | 'tasks' | 'files' | 'memories' | 'personas'

const TABS: { id: Tab; label: string; glyph: string }[] = [
  { id: 'live', label: 'Live', glyph: '◉' },
  { id: 'tasks', label: 'Tasks', glyph: '⚙' },
  { id: 'files', label: 'Files', glyph: '🗂' },
  { id: 'memories', label: 'Memory', glyph: '🧠' },
  { id: 'personas', label: 'Personas', glyph: '🧩' },
]

export default function App() {
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

        <main className="mx-auto w-full max-w-reading flex-1 px-4 py-5 pb-24 sm:pb-8">
          <h1 className="mb-4 hidden text-xl font-semibold capitalize sm:block">{tab === 'live' ? 'Live' : tab}</h1>
          {tab === 'live' && <LiveView state={state} onOpen={setOpenId} />}
          {tab === 'tasks' && <TasksView state={state} onOpen={setOpenId} />}
          {tab === 'files' && <FilesView />}
          {tab === 'memories' && <MemoriesView />}
          {tab === 'personas' && <PersonasView />}
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
