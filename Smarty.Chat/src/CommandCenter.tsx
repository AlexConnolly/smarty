import React, { useState, useEffect } from 'react'
import {
  fetchSettings, saveSettings, fetchTokens, resetTokens,
  fetchTasks, cancelTaskGlobal, fetchCapabilities, fetchModels, fetchPersonas,
  TaskDetail, CapabilityDetail, TokenUsage, PersonaDetail
} from './api'

interface CommandCenterProps { onClose: () => void }
type TabType = 'dashboard' | 'providers' | 'personas' | 'gateways'

export default function CommandCenter({ onClose }: CommandCenterProps) {
  const [activeTab, setActiveTab] = useState<TabType>('dashboard')
  const [settings, setSettings] = useState<Record<string, string>>({})
  const [tokens, setTokens] = useState<TokenUsage>({ input: 0, output: 0, total: 0 })
  const [tasks, setTasks] = useState<TaskDetail[]>([])
  const [capabilities, setCapabilities] = useState<CapabilityDetail[]>([])
  const [personas, setPersonas] = useState<PersonaDetail[]>([])
  const [ollamaModels, setOllamaModels] = useState<string[]>([])
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [saveStatus, setSaveStatus] = useState<'idle' | 'success' | 'error'>('idle')
  const [saveMessage, setSaveMessage] = useState('')
  const [expandedTask, setExpandedTask] = useState<string | null>(null)

  useEffect(() => {
    Promise.all([
      fetchSettings().then(setSettings),
      fetchTokens().then(setTokens),
      fetchTasks().then(setTasks),
      fetchCapabilities().then(setCapabilities),
      fetchPersonas().then(setPersonas),
      fetchModels().then(setOllamaModels),
    ]).catch(console.error).finally(() => setLoading(false))
  }, [])

  useEffect(() => {
    const id = setInterval(() => {
      fetchTasks().then(setTasks).catch(() => {})
      fetchTokens().then(setTokens).catch(() => {})
    }, 2500)
    return () => clearInterval(id)
  }, [])

  const handleSaveSettings = async (e: React.FormEvent, upd: Record<string, string>) => {
    e.preventDefault()
    setSaving(true); setSaveStatus('idle')
    try {
      const saved = await saveSettings(upd)
      setSettings(saved)
      setSaveStatus('success'); setSaveMessage('Configuration applied!')
      setCapabilities(await fetchCapabilities())
      setTimeout(() => setSaveStatus('idle'), 3000)
    } catch (err: any) {
      setSaveStatus('error'); setSaveMessage(err.message || 'Failed to save.')
    } finally { setSaving(false) }
  }

  const handleResetTokens = async () => {
    if (!confirm('Reset token counters?')) return
    try { setTokens(await resetTokens()) } catch { alert('Failed to reset.') }
  }

  const handleCancelTask = async (id: string) => {
    try { await cancelTaskGlobal(id); setTasks(await fetchTasks()) } catch (e) { alert(e) }
  }

  const elapsed = (s: string) => {
    const d = Math.max(0, Date.now() - new Date(s).getTime())
    const sec = Math.floor(d / 1000), min = Math.floor(sec / 60), hr = Math.floor(min / 60)
    return [hr > 0 && `${hr}h`, `${min % 60}m`, `${sec % 60}s`].filter(Boolean).join(' ')
  }

  if (loading) return (
    <div className="flex h-full items-center justify-center" style={{ background: 'var(--color-bg)' }}>
      <div className="flex flex-col items-center gap-3">
        <div className="h-8 w-8 animate-spin rounded-full border-2 border-accent border-t-transparent" />
        <p className="text-sm" style={{ color: 'var(--color-ink-soft)' }}>Loading Command Centre...</p>
      </div>
    </div>
  )

  const tabs: { id: TabType; label: string; Icon: React.FC<React.SVGProps<SVGSVGElement>> }[] = [
    { id: 'dashboard', label: 'Dashboard', Icon: DashboardIcon },
    { id: 'providers', label: 'Model Providers', Icon: CpuIcon },
    { id: 'personas', label: 'Personas', Icon: PersonasIcon },
    { id: 'gateways', label: 'Gateways', Icon: GatewaysIcon },
  ]

  return (
    <div className="flex h-full overflow-hidden" style={{ background: 'var(--color-bg)', color: 'var(--color-ink)' }}>
      {/* Sidebar */}
      <aside className="w-60 shrink-0 flex flex-col border-r" style={{ borderColor: 'var(--color-line)', background: 'var(--color-surface-mid)' }}>
        <div className="px-5 py-6 border-b" style={{ borderColor: 'var(--color-line)' }}>
          <div className="flex items-center gap-3">
            <span className="grid h-8 w-8 place-items-center rounded-lg font-bold text-base shadow-sm"
              style={{ background: 'var(--color-accent)', color: 'var(--color-on-accent)' }}>S</span>
            <div>
              <p className="text-sm font-bold">Smarty</p>
              <p className="text-[11px] font-semibold flex items-center gap-1.5" style={{ color: 'var(--color-accent)' }}>
                <span className="inline-block h-1.5 w-1.5 rounded-full bg-emerald-500 animate-pulse" />
                Command Centre
              </p>
            </div>
          </div>
        </div>

        <nav className="flex-1 p-3 space-y-1">
          {tabs.map(({ id, label, Icon }) => (
            <button key={id}
              onClick={() => { setActiveTab(id); setSaveStatus('idle') }}
              className="flex w-full items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium transition-all"
              style={activeTab === id
                ? { background: 'var(--color-accent)', color: 'var(--color-on-accent)' }
                : { color: 'var(--color-ink-soft)' }}
            >
              <Icon className="h-4 w-4" />
              {label}
            </button>
          ))}
        </nav>

        <div className="p-3">
          <button onClick={onClose}
            className="flex w-full items-center justify-center gap-2 rounded-lg border px-3 py-2.5 text-xs font-semibold transition"
            style={{ borderColor: 'var(--color-line)', color: 'var(--color-ink-soft)' }}>
            ← Back to Smarty
          </button>
        </div>
      </aside>

      {/* Main */}
      <main className="flex-1 overflow-y-auto">
        <div className="border-b px-8 py-5 flex items-center justify-between" style={{ borderColor: 'var(--color-line)' }}>
          <div>
            <h1 className="text-xl font-bold tracking-tight">{tabs.find(t => t.id === activeTab)?.label}</h1>
            <p className="text-xs mt-0.5" style={{ color: 'var(--color-ink-soft)' }}>
              {activeTab === 'dashboard' && 'Monitor running tasks and token consumption.'}
              {activeTab === 'providers' && 'Configure your AI model providers and endpoints.'}
              {activeTab === 'personas' && 'Set up tools and providers grouped by specialist persona.'}
              {activeTab === 'gateways' && 'Manage background communication gateways like Slack.'}
            </p>
          </div>
          <div className="flex items-center gap-2 rounded-full px-3 py-1 text-xs font-semibold bg-emerald-500/10 text-emerald-600">
            <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />API Operational
          </div>
        </div>

        <div className="p-8">
          {activeTab === 'dashboard' && (
            <DashboardTab tokens={tokens} tasks={tasks} onResetTokens={handleResetTokens}
              onCancelTask={handleCancelTask} elapsed={elapsed}
              expandedTask={expandedTask} setExpandedTask={setExpandedTask} />
          )}
          {activeTab === 'providers' && (
            <ProvidersTab settings={settings} ollamaModels={ollamaModels}
              saving={saving} saveStatus={saveStatus} saveMessage={saveMessage} onSave={handleSaveSettings} />
          )}
          {activeTab === 'personas' && (
            <PersonasTab settings={settings} personas={personas} capabilities={capabilities}
              saving={saving} saveStatus={saveStatus} saveMessage={saveMessage} onSave={handleSaveSettings} />
          )}
          {activeTab === 'gateways' && (
            <GatewaysTab settings={settings} capabilities={capabilities}
              saving={saving} saveStatus={saveStatus} saveMessage={saveMessage} onSave={handleSaveSettings} />
          )}
        </div>
      </main>
    </div>
  )
}

// ── Dashboard ──────────────────────────────────────────────────────────────────

interface DashboardTabProps {
  tokens: TokenUsage; tasks: TaskDetail[]
  onResetTokens: () => void; onCancelTask: (id: string) => void
  elapsed: (s: string) => string
  expandedTask: string | null; setExpandedTask: (id: string | null) => void
}

function DashboardTab({ tokens, tasks, onResetTokens, onCancelTask, elapsed, expandedTask, setExpandedTask }: DashboardTabProps) {
  const active = tasks.filter(t => ['running', 'waiting', 'waiting_gate'].includes(t.status)).length

  return (
    <div className="space-y-6">
      <div className="grid gap-4 sm:grid-cols-3">
        <StatCard label="Total Tokens" value={tokens.total.toLocaleString()}
          sub={`In: ${tokens.input.toLocaleString()} / Out: ${tokens.output.toLocaleString()}`}
          action={<button onClick={onResetTokens} className="text-[10px] font-bold uppercase text-rose-500">Reset</button>} />
        <StatCard label="Active Tasks" value={String(active)} sub="Running background threads"
          action={active > 0 ? <span className="h-2 w-2 rounded-full bg-emerald-500 animate-pulse inline-block" /> : null} />
        <StatCard label="Settings Store" value="SQLite" sub="Local database active" action={null} />
      </div>

      <div className="rounded-xl border overflow-hidden" style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)' }}>
        <div className="px-5 py-4 border-b flex items-center justify-between" style={{ borderColor: 'var(--color-line)' }}>
          <h3 className="text-sm font-bold">Task Monitor</h3>
          <span className="text-xs" style={{ color: 'var(--color-ink-soft)' }}>{tasks.length} tasks</span>
        </div>

        {tasks.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-16 text-center">
            <p className="text-2xl font-bold mb-1" style={{ color: 'var(--color-ink-mute)' }}>No tasks</p>
            <p className="text-xs max-w-xs" style={{ color: 'var(--color-ink-mute)' }}>
              Background tasks will appear here when Smarty is working.
            </p>
          </div>
        ) : (
          <div className="divide-y" style={{ borderColor: 'var(--color-line)' }}>
            {tasks.map(task => {
              const isActive = ['running', 'waiting', 'waiting_gate'].includes(task.status)
              const isExp = expandedTask === task.id
              return (
                <div key={task.id} className="p-5">
                  <div className="flex flex-wrap items-start justify-between gap-4">
                    <div className="flex-1 space-y-1 min-w-[220px]">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="text-[10px] font-mono" style={{ color: 'var(--color-ink-mute)' }}>#{task.id}</span>
                        <span className={`text-[10px] font-bold uppercase px-2 py-0.5 rounded-full ${
                          task.status === 'running' ? 'bg-emerald-500/10 text-emerald-600' :
                          task.status === 'waiting' ? 'bg-amber-500/10 text-amber-600' :
                          task.status === 'waiting_gate' ? 'bg-violet-500/10 text-violet-600' :
                          'bg-zinc-500/10 text-zinc-500'}`}>
                          {task.status}
                        </span>
                        {task.persona && (
                          <span className="text-[10px] px-2 py-0.5 rounded-full font-semibold border"
                            style={{ color: 'var(--color-accent)', borderColor: 'var(--color-accent)', background: 'color-mix(in srgb, var(--color-accent) 8%, transparent)' }}>
                            {task.persona}
                          </span>
                        )}
                      </div>
                      <p className="text-sm font-medium">{task.description}</p>
                      {task.latestThought && (
                        <p className="text-xs font-mono italic border-l-2 pl-2 mt-1"
                          style={{ borderColor: 'var(--color-line)', color: 'var(--color-ink-soft)' }}>
                          {task.latestThought}
                        </p>
                      )}
                    </div>
                    <div className="flex items-center gap-2 shrink-0">
                      <span className="text-[11px]" style={{ color: 'var(--color-ink-soft)' }}>{elapsed(task.startedAt)}</span>
                      <button onClick={() => setExpandedTask(isExp ? null : task.id)}
                        className="rounded-lg border p-1.5 transition"
                        style={{ borderColor: 'var(--color-line)' }} title="Logs">
                        <TerminalIcon className="h-4 w-4" style={{ color: 'var(--color-ink-soft)' }} />
                      </button>
                      {isActive && (
                        <button onClick={() => onCancelTask(task.id)}
                          className="rounded-lg border border-red-500/20 p-1.5 text-rose-500 hover:bg-rose-500/10"
                          title="Cancel">
                          <StopIcon className="h-4 w-4" />
                        </button>
                      )}
                    </div>
                  </div>
                  {isExp && (
                    <div className="mt-4 border-t pt-4" style={{ borderColor: 'var(--color-line)' }}>
                      <p className="text-[10px] font-bold uppercase tracking-wider mb-2" style={{ color: 'var(--color-ink-soft)' }}>Logs</p>
                      {task.progressLog.length === 0
                        ? <p className="text-xs italic" style={{ color: 'var(--color-ink-mute)' }}>No logs yet.</p>
                        : <div className="space-y-1 max-h-40 overflow-y-auto">
                            {task.progressLog.map((l, i) => (
                              <div key={i} className="flex gap-2 text-xs font-mono">
                                <span style={{ color: 'var(--color-ink-mute)' }}>[{new Date(l.timestamp).toLocaleTimeString()}]</span>
                                <span style={{ color: 'var(--color-ink-soft)' }}>{l.message}</span>
                              </div>
                            ))}
                          </div>
                      }
                    </div>
                  )}
                </div>
              )
            })}
          </div>
        )}
      </div>
    </div>
  )
}

function StatCard({ label, value, sub, action }: { label: string; value: string; sub: string; action: React.ReactNode }) {
  return (
    <div className="rounded-xl border p-5" style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)' }}>
      <div className="flex items-center justify-between mb-2">
        <span className="text-[10px] font-bold uppercase tracking-wider" style={{ color: 'var(--color-ink-soft)' }}>{label}</span>
        {action}
      </div>
      <div className="text-2xl font-bold tracking-tight">{value}</div>
      <p className="mt-1 text-[11px]" style={{ color: 'var(--color-ink-soft)' }}>{sub}</p>
    </div>
  )
}

// ── Providers ──────────────────────────────────────────────────────────────────

interface ProvidersTabProps {
  settings: Record<string, string>; ollamaModels: string[]
  saving: boolean; saveStatus: 'idle' | 'success' | 'error'
  saveMessage: string; onSave: (e: React.FormEvent, s: Record<string, string>) => void
}

function ProvidersTab({ settings, ollamaModels, saving, saveStatus, saveMessage, onSave }: ProvidersTabProps) {
  const [primaryProvider, setPrimaryProvider] = useState(settings['model.provider'] || 'ollama')
  const [primaryModel, setPrimaryModel] = useState(settings['model.modelName'] || '')
  const [primaryUrl, setPrimaryUrl] = useState(settings['model.baseUrl'] || '')
  const [secondaryEnabled, setSecondaryEnabled] = useState(!!settings['secondaryModel.provider'])
  const [secondaryProvider, setSecondaryProvider] = useState(settings['secondaryModel.provider'] || 'ollama')
  const [secondaryModel, setSecondaryModel] = useState(settings['secondaryModel.modelName'] || '')
  const [secondaryUrl, setSecondaryUrl] = useState(settings['secondaryModel.baseUrl'] || '')
  const [togetherKey, setTogetherKey] = useState(settings['together.apiKey'] || '')
  const [togetherUrl, setTogetherUrl] = useState(settings['together.baseUrl'] || 'https://api.together.xyz/v1')
  const [ollamaUrl, setOllamaUrl] = useState(settings['ollama.baseUrl'] || 'http://localhost:11434')

  const handleSubmit = (e: React.FormEvent) => {
    onSave(e, {
      'model.provider': primaryProvider, 'model.modelName': primaryModel, 'model.baseUrl': primaryUrl,
      'together.apiKey': togetherKey, 'together.baseUrl': togetherUrl, 'ollama.baseUrl': ollamaUrl,
      'secondaryModel.provider': secondaryEnabled ? secondaryProvider : '',
      'secondaryModel.modelName': secondaryEnabled ? secondaryModel : '',
      'secondaryModel.baseUrl': secondaryEnabled ? secondaryUrl : '',
    })
  }

  return (
    <form onSubmit={handleSubmit} className="space-y-6 max-w-3xl">
      <SaveBanner status={saveStatus} message={saveMessage} />
      <div className="grid gap-6 md:grid-cols-2">
        <Card title="Primary Model">
          <ProviderSelect label="Provider" value={primaryProvider} onChange={setPrimaryProvider} />
          <ModelField label="Model" provider={primaryProvider} value={primaryModel} onChange={setPrimaryModel} models={ollamaModels} />
          <TextField label="Endpoint URL (optional)" value={primaryUrl} onChange={setPrimaryUrl}
            placeholder={primaryProvider === 'ollama' ? 'http://localhost:11434' : 'https://api.together.xyz/v1'} />
        </Card>
        <Card title="Secondary Model" headerRight={
          <label className="flex items-center gap-2 cursor-pointer">
            <input type="checkbox" checked={secondaryEnabled} onChange={e => setSecondaryEnabled(e.target.checked)} className="rounded" />
            <span className="text-xs font-semibold" style={{ color: 'var(--color-ink-soft)' }}>Enable</span>
          </label>
        }>
          {secondaryEnabled ? (
            <>
              <ProviderSelect label="Provider" value={secondaryProvider} onChange={setSecondaryProvider} />
              <ModelField label="Model" provider={secondaryProvider} value={secondaryModel} onChange={setSecondaryModel} models={ollamaModels} />
              <TextField label="Endpoint URL (optional)" value={secondaryUrl} onChange={setSecondaryUrl}
                placeholder={secondaryProvider === 'ollama' ? 'http://localhost:11434' : 'https://api.together.xyz/v1'} />
            </>
          ) : (
            <p className="text-xs italic py-8 text-center" style={{ color: 'var(--color-ink-mute)' }}>
              Disabled — Smarty uses only the primary provider.
            </p>
          )}
        </Card>
      </div>
      <div className="grid gap-6 md:grid-cols-2">
        <Card title="Ollama (Local)">
          <TextField label="Base URL" value={ollamaUrl} onChange={setOllamaUrl} />
        </Card>
        <Card title="Together.ai (Cloud)">
          <SecretField label="API Key" value={togetherKey} onChange={setTogetherKey} placeholder="Paste API key..." />
          <TextField label="Base URL" value={togetherUrl} onChange={setTogetherUrl} />
        </Card>
      </div>
      <FormFooter saving={saving} label="Apply Provider Settings" />
    </form>
  )
}

// ── Personas ───────────────────────────────────────────────────────────────────

interface PersonasTabProps {
  settings: Record<string, string>; personas: PersonaDetail[]; capabilities: CapabilityDetail[]
  saving: boolean; saveStatus: 'idle' | 'success' | 'error'
  saveMessage: string; onSave: (e: React.FormEvent, s: Record<string, string>) => void
}

// Maps capability IDs to functional groupings the user understands
const PROVIDER_GROUPS: { id: string; label: string; caps: string[] }[] = [
  { id: 'code_access',    label: 'Code Access',    caps: ['code', 'github'] },
  { id: 'issue_tracking', label: 'Issue Tracking', caps: ['jira'] },
  { id: 'log_search',     label: 'Log Search',     caps: ['kibana'] },
]

function PersonasTab({ settings, personas, capabilities, saving, saveStatus, saveMessage, onSave }: PersonasTabProps) {
  const [activeId, setActiveId] = useState(personas[0]?.id ?? '')
  const [formState, setFormState] = useState<Record<string, string>>({})

  useEffect(() => { if (personas.length && !activeId) setActiveId(personas[0].id) }, [personas])

  useEffect(() => {
    const state: Record<string, string> = {}
    capabilities.forEach(cap => {
      [...cap.requiredConfig, ...cap.optionalConfig].forEach(key => {
        state[`${cap.id}.${key}`] = settings[`${cap.id}.${key}`] || ''
      })
    })
    setFormState(state)
  }, [settings, capabilities])

  const activePersona = personas.find(p => p.id === activeId)

  const relevantGroups = PROVIDER_GROUPS
    .filter(g => g.caps.some(cid => activePersona?.capabilityIds.includes(cid)))
    .map(g => ({
      ...g,
      activeCaps: capabilities.filter(cap => g.caps.includes(cap.id) && activePersona?.capabilityIds.includes(cap.id))
    }))
    .filter(g => g.activeCaps.length > 0)

  const handleSubmit = (e: React.FormEvent) => {
    const merged = { ...settings }
    activePersona?.capabilityIds.forEach(cid => {
      const cap = capabilities.find(c => c.id === cid)
      if (!cap) return
      ;[...cap.requiredConfig, ...cap.optionalConfig].forEach(key => {
        merged[`${cid}.${key}`] = formState[`${cid}.${key}`] || ''
      })
    })
    onSave(e, merged)
  }

  return (
    <div className="flex gap-6 items-start">
      {/* Persona list */}
      <div className="w-56 shrink-0 rounded-xl border overflow-hidden"
        style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)' }}>
        <div className="px-4 py-3 border-b text-[10px] font-bold uppercase tracking-wider"
          style={{ borderColor: 'var(--color-line)', color: 'var(--color-ink-soft)' }}>Personas</div>
        {personas.map(p => (
          <button key={p.id} onClick={() => setActiveId(p.id)}
            className="flex w-full flex-col px-4 py-3.5 text-left border-b transition"
            style={{ borderColor: 'var(--color-line)', background: activeId === p.id ? 'var(--color-surface-mid)' : 'transparent' }}>
            <span className="text-sm font-bold">{p.name}</span>
            <span className="text-[10px] font-mono mt-0.5" style={{ color: 'var(--color-ink-mute)' }}>{p.id}</span>
          </button>
        ))}
        {personas.length === 0 && (
          <p className="px-4 py-6 text-xs italic" style={{ color: 'var(--color-ink-mute)' }}>No personas loaded.</p>
        )}
      </div>

      {/* Details + config */}
      {activePersona && (
        <div className="flex-1 space-y-5">
          <Card title={activePersona.name}>
            <p className="text-sm" style={{ color: 'var(--color-ink-soft)' }}>{activePersona.description}</p>
            <div>
              <FieldLabel>System Prompt</FieldLabel>
              <textarea readOnly value={activePersona.systemPrompt}
                className="w-full h-24 rounded-lg border p-3 font-mono text-xs resize-none"
                style={{ borderColor: 'var(--color-line)', background: 'var(--color-surface-mid)', color: 'var(--color-ink-soft)' }} />
            </div>
          </Card>

          <form onSubmit={handleSubmit}>
            <Card title="Configure Providers">
              <SaveBanner status={saveStatus} message={saveMessage} />
              {relevantGroups.length === 0 && (
                <p className="text-sm text-center py-6 italic" style={{ color: 'var(--color-ink-mute)' }}>
                  No configurable providers for this persona.
                </p>
              )}
              {relevantGroups.map(group => (
                <div key={group.id} className="rounded-lg border overflow-hidden"
                  style={{ borderColor: 'var(--color-line)' }}>
                  <div className="flex items-center justify-between px-4 py-2.5 border-b"
                    style={{ borderColor: 'var(--color-line)', background: 'var(--color-surface-mid)' }}>
                    <span className="text-xs font-bold uppercase tracking-wider">{group.label}</span>
                    <span className={`text-[10px] font-bold uppercase px-2 py-0.5 rounded-full ${
                      group.activeCaps.some(c => c.isConnected) ? 'bg-emerald-500/10 text-emerald-600' : 'bg-zinc-500/10 text-zinc-500'
                    }`}>
                      {group.activeCaps.some(c => c.isConnected) ? 'Connected' : 'Not configured'}
                    </span>
                  </div>
                  <div className="p-4 space-y-5">
                    {group.activeCaps.map(cap => (
                      <div key={cap.id}>
                        <div className="flex items-center justify-between mb-3">
                          <span className="text-sm font-bold" style={{ color: 'var(--color-accent)' }}>{cap.displayName}</span>
                          <span className={`text-[9px] font-bold uppercase px-2 py-0.5 rounded-full ${
                            cap.isConnected ? 'bg-emerald-500/10 text-emerald-600' : 'bg-zinc-500/10 text-zinc-500'
                          }`}>{cap.isConnected ? 'Active' : 'Inactive'}</span>
                        </div>
                        {cap.promptHint && (
                          <p className="text-[11px] mb-3 p-2.5 rounded-lg border"
                            style={{ borderColor: 'var(--color-line)', background: 'var(--color-surface-mid)', color: 'var(--color-ink-soft)' }}>
                            {cap.promptHint}
                          </p>
                        )}
                        <div className="space-y-3">
                          {cap.requiredConfig.map(key => (
                            <CapField key={key} capId={cap.id} fieldKey={key} required formState={formState}
                              onChange={(k, v) => setFormState(prev => ({ ...prev, [k]: v }))} />
                          ))}
                          {cap.optionalConfig.map(key => (
                            <CapField key={key} capId={cap.id} fieldKey={key} required={false} formState={formState}
                              onChange={(k, v) => setFormState(prev => ({ ...prev, [k]: v }))} />
                          ))}
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              ))}
              <FormFooter saving={saving} label="Save Provider Settings" />
            </Card>
          </form>
        </div>
      )}
    </div>
  )
}

// ── Gateways ───────────────────────────────────────────────────────────────────

interface GatewaysTabProps {
  settings: Record<string, string>; capabilities: CapabilityDetail[]
  saving: boolean; saveStatus: 'idle' | 'success' | 'error'
  saveMessage: string; onSave: (e: React.FormEvent, s: Record<string, string>) => void
}

function GatewaysTab({ settings, capabilities, saving, saveStatus, saveMessage, onSave }: GatewaysTabProps) {
  const slackCap = capabilities.find(c => c.id === 'slack')
  const allKeys = slackCap ? [...slackCap.requiredConfig, ...slackCap.optionalConfig] : []

  const [formState, setFormState] = useState<Record<string, string>>(() => {
    const s: Record<string, string> = {}
    allKeys.forEach(key => { s[`slack.${key}`] = settings[`slack.${key}`] || '' })
    return s
  })

  useEffect(() => {
    const s: Record<string, string> = {}
    allKeys.forEach(key => { s[`slack.${key}`] = settings[`slack.${key}`] || '' })
    setFormState(s)
  }, [settings])

  const handleSubmit = (e: React.FormEvent) => {
    if (!slackCap) return
    const updated = { ...settings }
    allKeys.forEach(key => { updated[`slack.${key}`] = formState[`slack.${key}`] || '' })
    onSave(e, updated)
  }

  if (!slackCap) return <p className="text-sm italic" style={{ color: 'var(--color-ink-mute)' }}>No gateways configured.</p>

  const slackFields = [
    { key: 'botToken',        label: 'Bot Token (xoxb-…)',   placeholder: 'xoxb-...',    secret: true,  required: true  },
    { key: 'appToken',        label: 'App Token (xapp-…)',   placeholder: 'xapp-...',    secret: true,  required: true  },
    { key: 'companyName',     label: 'Company Name',         placeholder: 'My Company',  secret: false, required: false },
    { key: 'companyContext',  label: 'Company Context',      placeholder: 'Context for Smarty on Slack…', secret: false, required: false },
    { key: 'dataDir',         label: 'Data Directory',       placeholder: 'data-slack',  secret: false, required: false },
  ]

  return (
    <div className="max-w-xl">
      <div className="rounded-xl border overflow-hidden" style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)' }}>
        <div className="flex items-center justify-between px-5 py-4 border-b" style={{ borderColor: 'var(--color-line)' }}>
          <div>
            <h3 className="text-sm font-bold">Slack Gateway</h3>
            <p className="text-[11px] mt-0.5" style={{ color: 'var(--color-ink-soft)' }}>Runs as a background process when enabled</p>
          </div>
          <span className={`text-[10px] font-bold uppercase px-3 py-1 rounded-full ${
            slackCap.isConnected ? 'bg-emerald-500/10 text-emerald-600' : 'bg-zinc-500/10 text-zinc-500'
          }`}>{slackCap.isConnected ? 'Running' : 'Stopped'}</span>
        </div>

        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {slackCap.promptHint && (
            <p className="text-xs p-3 rounded-lg border"
              style={{ borderColor: 'var(--color-line)', background: 'var(--color-surface-mid)', color: 'var(--color-ink-soft)' }}>
              {slackCap.promptHint}
            </p>
          )}
          <SaveBanner status={saveStatus} message={saveMessage} />

          <div className="space-y-1">
            <FieldLabel>Gateway Status</FieldLabel>
            <select value={formState['slack.enabled'] || 'false'}
              onChange={e => setFormState(prev => ({ ...prev, 'slack.enabled': e.target.value }))}
              className="w-full rounded-lg border px-3 py-2 text-sm"
              style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)', color: 'var(--color-ink)' }}>
              <option value="false">Disabled</option>
              <option value="true">Enabled — runs Slack bot in background</option>
            </select>
          </div>

          {slackFields.map(({ key, label, placeholder, secret, required }) => (
            <div key={key} className="space-y-1">
              <FieldLabel required={required}>{label}</FieldLabel>
              <input
                type={secret ? 'password' : 'text'}
                value={formState[`slack.${key}`] || ''}
                onChange={e => setFormState(prev => ({ ...prev, [`slack.${key}`]: e.target.value }))}
                placeholder={placeholder}
                className="w-full rounded-lg border px-3 py-2 text-sm"
                style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)', color: 'var(--color-ink)' }}
              />
            </div>
          ))}

          <div className="flex justify-end pt-2">
            <button type="submit" disabled={saving}
              className="flex items-center gap-2 rounded-lg px-5 py-2.5 text-sm font-bold shadow-sm disabled:opacity-50"
              style={{ background: 'var(--color-accent)', color: 'var(--color-on-accent)' }}>
              {saving && <span className="h-4 w-4 animate-spin rounded-full border border-current border-t-transparent inline-block" />}
              Save Gateway Settings
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

// ── Shared primitives ──────────────────────────────────────────────────────────

function Card({ title, children, headerRight }: { title: string; children: React.ReactNode; headerRight?: React.ReactNode }) {
  return (
    <div className="rounded-xl border p-5 space-y-4" style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)' }}>
      <div className="flex items-center justify-between border-b pb-2.5" style={{ borderColor: 'var(--color-line)' }}>
        <h3 className="text-sm font-bold">{title}</h3>
        {headerRight}
      </div>
      {children}
    </div>
  )
}

function FieldLabel({ children, required }: { children: React.ReactNode; required?: boolean }) {
  return (
    <label className="text-[11px] font-bold uppercase tracking-wider flex items-center gap-1.5 mb-1"
      style={{ color: 'var(--color-ink-soft)' }}>
      {children}
      {required && <span className="text-[9px] text-rose-500 normal-case font-semibold">* required</span>}
    </label>
  )
}

function TextField({ label, value, onChange, placeholder, required }: { label: string; value: string; onChange: (v: string) => void; placeholder?: string; required?: boolean }) {
  return (
    <div className="space-y-1">
      <FieldLabel required={required}>{label}</FieldLabel>
      <input type="text" value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder}
        className="w-full rounded-lg border px-3 py-2 text-sm"
        style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)', color: 'var(--color-ink)' }} />
    </div>
  )
}

function SecretField({ label, value, onChange, placeholder, required }: { label: string; value: string; onChange: (v: string) => void; placeholder?: string; required?: boolean }) {
  return (
    <div className="space-y-1">
      <FieldLabel required={required}>{label}</FieldLabel>
      <input type="password" value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder}
        className="w-full rounded-lg border px-3 py-2 text-sm"
        style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)', color: 'var(--color-ink)' }} />
    </div>
  )
}

function ProviderSelect({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <div className="space-y-1">
      <FieldLabel>{label}</FieldLabel>
      <select value={value} onChange={e => onChange(e.target.value)}
        className="w-full rounded-lg border px-3 py-2 text-sm"
        style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)', color: 'var(--color-ink)' }}>
        <option value="ollama">Ollama (Local)</option>
        <option value="together">Together.ai (Cloud)</option>
      </select>
    </div>
  )
}

function ModelField({ label, provider, value, onChange, models }: { label: string; provider: string; value: string; onChange: (v: string) => void; models: string[] }) {
  return (
    <div className="space-y-1">
      <FieldLabel>{label}</FieldLabel>
      {provider === 'ollama' ? (
        <select value={value} onChange={e => onChange(e.target.value)}
          className="w-full rounded-lg border px-3 py-2 text-sm"
          style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)', color: 'var(--color-ink)' }}>
          {models.map(m => <option key={m} value={m}>{m}</option>)}
          {models.length === 0 && <option value="qwen3.5:latest">qwen3.5:latest</option>}
          {!models.includes(value) && value && <option value={value}>{value}</option>}
        </select>
      ) : (
        <input type="text" value={value} onChange={e => onChange(e.target.value)}
          placeholder="meta-llama/Llama-3-70b-chat-hf"
          className="w-full rounded-lg border px-3 py-2 text-sm"
          style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)', color: 'var(--color-ink)' }} />
      )}
    </div>
  )
}

function CapField({ capId, fieldKey, required, formState, onChange }: {
  capId: string; fieldKey: string; required: boolean
  formState: Record<string, string>; onChange: (k: string, v: string) => void
}) {
  const key = `${capId}.${fieldKey}`
  const isSecret = ['token', 'key', 'password', 'secret'].some(s => fieldKey.toLowerCase().includes(s))
  return (
    <div className="space-y-1">
      <FieldLabel required={required}>{fieldKey.replace(/_/g, ' ')}{!required && ' (optional)'}</FieldLabel>
      <input
        type={isSecret ? 'password' : 'text'}
        value={formState[key] || ''}
        onChange={e => onChange(key, e.target.value)}
        placeholder={`Enter ${fieldKey.replace(/_/g, ' ')}...`}
        className="w-full rounded-lg border px-3 py-1.5 text-xs"
        style={{ borderColor: 'var(--color-line)', background: 'var(--color-bg)', color: 'var(--color-ink)' }}
        required={required}
      />
    </div>
  )
}

function SaveBanner({ status, message }: { status: 'idle' | 'success' | 'error'; message: string }) {
  if (status === 'idle') return null
  return (
    <div className={`p-3.5 rounded-lg border flex items-center gap-3 text-sm font-medium ${
      status === 'success' ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-600' : 'bg-red-500/10 border-red-500/20 text-rose-500'
    }`}>
      {status === 'success' ? <CheckIcon className="h-4 w-4 shrink-0" /> : <CrossIcon className="h-4 w-4 shrink-0" />}
      {message}
    </div>
  )
}

function FormFooter({ saving, label }: { saving: boolean; label: string }) {
  return (
    <div className="flex justify-end pt-4 border-t" style={{ borderColor: 'var(--color-line)' }}>
      <button type="submit" disabled={saving}
        className="flex items-center gap-2 rounded-lg px-5 py-2.5 text-sm font-bold shadow-sm disabled:opacity-50"
        style={{ background: 'var(--color-accent)', color: 'var(--color-on-accent)' }}>
        {saving && <span className="h-4 w-4 animate-spin rounded-full border border-current border-t-transparent inline-block" />}
        {label}
      </button>
    </div>
  )
}

// ── Icons ──────────────────────────────────────────────────────────────────────

function DashboardIcon(p: React.SVGProps<SVGSVGElement>) {
  return <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...p}><rect x="3" y="3" width="7" height="9"/><rect x="14" y="3" width="7" height="5"/><rect x="14" y="12" width="7" height="9"/><rect x="3" y="16" width="7" height="5"/></svg>
}
function CpuIcon(p: React.SVGProps<SVGSVGElement>) {
  return <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...p}><rect x="4" y="4" width="16" height="16" rx="2"/><rect x="9" y="9" width="6" height="6"/><line x1="9" y1="1" x2="9" y2="4"/><line x1="15" y1="1" x2="15" y2="4"/><line x1="9" y1="20" x2="9" y2="23"/><line x1="15" y1="20" x2="15" y2="23"/><line x1="20" y1="9" x2="23" y2="9"/><line x1="20" y1="15" x2="23" y2="15"/><line x1="1" y1="9" x2="4" y2="9"/><line x1="1" y1="15" x2="4" y2="15"/></svg>
}
function PersonasIcon(p: React.SVGProps<SVGSVGElement>) {
  return <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg>
}
function GatewaysIcon(p: React.SVGProps<SVGSVGElement>) {
  return <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...p}><rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/><line x1="6" y1="6" x2="6.01" y2="6"/><line x1="6" y1="18" x2="6.01" y2="18"/></svg>
}
function TerminalIcon(p: React.SVGProps<SVGSVGElement>) {
  return <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...p}><polyline points="4 17 10 11 4 5"/><line x1="12" y1="19" x2="20" y2="19"/></svg>
}
function StopIcon(p: React.SVGProps<SVGSVGElement>) {
  return <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...p}><rect x="4" y="4" width="16" height="16" rx="2"/></svg>
}
function CheckIcon(p: React.SVGProps<SVGSVGElement>) {
  return <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...p}><polyline points="20 6 9 17 4 12"/></svg>
}
function CrossIcon(p: React.SVGProps<SVGSVGElement>) {
  return <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...p}><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
}
