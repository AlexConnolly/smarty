import { useEffect, useState } from 'react'
import {
  CapabilityMeta,
  PersonaView,
  ToolMeta,
  deletePersona,
  fetchCapabilities,
  fetchPersonas,
  savePersona,
} from '../api'
import { Button, Card, Pill, Spinner, cx } from '../ui'

export function PersonasView() {
  const [personas, setPersonas] = useState<PersonaView[] | null>(null)
  const [caps, setCaps] = useState<CapabilityMeta[]>([])
  const [editing, setEditing] = useState<PersonaView | 'new' | null>(null)

  const load = async () => {
    const [p, c] = await Promise.all([fetchPersonas(), fetchCapabilities()])
    setPersonas(p)
    setCaps(c)
  }
  useEffect(() => {
    void load()
  }, [])

  if (!personas)
    return (
      <div className="flex justify-center py-10">
        <Spinner />
      </div>
    )

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between px-1">
        <p className="text-sm text-ink-mute">Specialist roles Smarty delegates to. Edit name, description and the tools each can call.</p>
        <Button size="sm" onClick={() => setEditing('new')}>
          + New persona
        </Button>
      </div>

      {editing && (
        <PersonaEditor
          persona={editing === 'new' ? null : editing}
          caps={caps}
          onDone={() => {
            setEditing(null)
            void load()
          }}
          onCancel={() => setEditing(null)}
        />
      )}

      <div className="space-y-3">
        {personas.map((p) => (
          <PersonaCard key={p.id} persona={p} onEdit={() => setEditing(p)} onDeleted={load} />
        ))}
      </div>

      <section>
        <h2 className="mb-2 px-1 text-sm font-semibold uppercase tracking-wide text-ink-mute">Integrations</h2>
        <div className="space-y-2">
          {caps.map((c) => (
            <Card key={c.id} className="px-4 py-3">
              <div className="flex items-center gap-2">
                <span className="font-medium">{c.displayName}</span>
                <Pill tone={c.configured ? 'live' : 'neutral'}>{c.configured ? 'configured' : 'not configured'}</Pill>
                <span className="ml-auto font-mono text-xs text-ink-mute">{c.id}</span>
              </div>
              {c.tools.length > 0 && (
                <div className="mt-2 flex flex-wrap gap-1.5">
                  {c.tools.map((t) => (
                    <span key={t.name} className="rounded-md bg-surface-low px-2 py-0.5 font-mono text-[0.7rem] text-ink-soft" title={t.description}>
                      {t.name}
                    </span>
                  ))}
                </div>
              )}
            </Card>
          ))}
        </div>
      </section>
    </div>
  )
}

function PersonaCard({ persona, onEdit, onDeleted }: { persona: PersonaView; onEdit: () => void; onDeleted: () => void }) {
  const [showTools, setShowTools] = useState(false)
  return (
    <Card className="px-4 py-3.5">
      <div className="flex items-start gap-2">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="font-semibold">{persona.name}</span>
            {persona.builtin && <Pill>built-in</Pill>}
          </div>
          <div className="mt-0.5 text-sm text-ink-soft">{persona.description}</div>
          <div className="mt-1.5 flex flex-wrap gap-1.5">
            {persona.capabilityIds.length === 0 ? (
              <span className="text-xs text-ink-mute">base tools only</span>
            ) : (
              persona.capabilityIds.map((c) => (
                <Pill key={c} tone="accent">
                  {c}
                </Pill>
              ))
            )}
          </div>
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1">
          <Button size="sm" variant="soft" onClick={onEdit}>
            Edit
          </Button>
          {!persona.builtin && (
            <Button
              size="sm"
              variant="danger"
              onClick={async () => {
                await deletePersona(persona.id)
                onDeleted()
              }}
            >
              Delete
            </Button>
          )}
        </div>
      </div>

      <button onClick={() => setShowTools((s) => !s)} className="mt-2 text-xs text-accent hover:underline">
        {showTools ? 'Hide' : 'Show'} tools it can call ({persona.tools.length})
      </button>
      {showTools && (
        <div className="mt-2 space-y-1.5">
          {persona.tools.map((t) => (
            <ToolRow key={t.name} tool={t} />
          ))}
        </div>
      )}
    </Card>
  )
}

function ToolRow({ tool }: { tool: ToolMeta }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="rounded-lg bg-surface-low/60 px-3 py-2">
      <button onClick={() => setOpen((o) => !o)} className="flex w-full items-baseline gap-2 text-left">
        <span className="font-mono text-xs text-ink">{tool.name}</span>
        <span className="min-w-0 flex-1 truncate text-xs text-ink-mute">{tool.description}</span>
        {tool.parameters.length > 0 && <span className="text-ink-mute">{open ? '▾' : '▸'}</span>}
      </button>
      {open && tool.parameters.length > 0 && (
        <div className="mt-1.5 space-y-1 border-t border-line pt-1.5">
          {tool.parameters.map((p) => (
            <div key={p.name} className="text-xs">
              <span className="font-mono text-ink-soft">{p.name}</span>
              <span className="text-ink-mute"> : {p.type}</span>
              {p.required && <span className="text-danger"> *</span>}
              <span className="text-ink-mute"> — {p.description}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function PersonaEditor({
  persona,
  caps,
  onDone,
  onCancel,
}: {
  persona: PersonaView | null
  caps: CapabilityMeta[]
  onDone: () => void
  onCancel: () => void
}) {
  const [name, setName] = useState(persona?.name ?? '')
  const [description, setDescription] = useState(persona?.description ?? '')
  const [selected, setSelected] = useState<string[]>(persona?.capabilityIds ?? [])
  const [busy, setBusy] = useState(false)

  const toggle = (id: string) =>
    setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]))

  const save = async () => {
    if (!name.trim()) return
    setBusy(true)
    await savePersona({ id: persona?.id, name: name.trim(), description: description.trim(), capabilityIds: selected })
    setBusy(false)
    onDone()
  }

  return (
    <Card className="space-y-3 p-4">
      <div className="text-sm font-semibold">{persona ? `Edit ${persona.name}` : 'New persona'}</div>
      <label className="block text-sm">
        <span className="mb-1 block text-xs font-medium text-ink-mute">Name</span>
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          className="w-full rounded-xl border border-line bg-surface px-3 py-2 outline-none focus:border-accent"
        />
      </label>
      <label className="block text-sm">
        <span className="mb-1 block text-xs font-medium text-ink-mute">Description</span>
        <textarea
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={2}
          className="w-full rounded-xl border border-line bg-surface px-3 py-2 outline-none focus:border-accent"
        />
      </label>
      <div>
        <span className="mb-1.5 block text-xs font-medium text-ink-mute">Capabilities</span>
        <div className="flex flex-wrap gap-2">
          {caps.map((c) => (
            <button
              key={c.id}
              onClick={() => toggle(c.id)}
              className={cx(
                'rounded-full border px-3 py-1 text-xs transition',
                selected.includes(c.id) ? 'border-accent bg-accent-soft text-accent' : 'border-line bg-surface text-ink-soft hover:bg-surface-mid',
              )}
              title={c.configured ? 'configured' : 'not configured — will contribute no tools until set up'}
            >
              {c.displayName}
              {!c.configured && <span className="ml-1 opacity-50">·off</span>}
            </button>
          ))}
        </div>
      </div>
      <div className="rounded-lg bg-surface-low/60 px-3 py-2 text-xs text-ink-mute">
        The persona's system prompt is managed by Smarty and isn't shown or edited here.
      </div>
      <div className="flex justify-end gap-2">
        <Button variant="ghost" size="sm" onClick={onCancel}>
          Cancel
        </Button>
        <Button size="sm" onClick={save} disabled={busy || !name.trim()}>
          {busy ? 'Saving…' : 'Save'}
        </Button>
      </div>
    </Card>
  )
}
