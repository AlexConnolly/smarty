import { useEffect, useState } from 'react'
import { MemoryFactView, addMemory, fetchMemories, retireMemory, timeAgo } from '../api'
import { Button, Card, EmptyState, Pill, Spinner } from '../ui'

function scopeLabel(scope?: string | null): string {
  if (!scope) return 'About you (global)'
  if (scope.startsWith('user:')) return `Personal · ${scope.slice(5)}`
  return `Project · ${scope}`
}

export function MemoriesView() {
  const [facts, setFacts] = useState<MemoryFactView[] | null>(null)
  const [adding, setAdding] = useState(false)
  const [form, setForm] = useState({ type: '', key: '', value: '', context: '', scope: '' })

  const load = async () => setFacts(await fetchMemories())
  useEffect(() => {
    void load()
  }, [])

  const submit = async () => {
    if (!form.type.trim() || !form.key.trim() || !form.value.trim()) return
    await addMemory({
      type: form.type.trim(),
      key: form.key.trim(),
      value: form.value.trim(),
      context: form.context.trim() || undefined,
      scope: form.scope.trim() || undefined,
    })
    setForm({ type: '', key: '', value: '', context: '', scope: '' })
    setAdding(false)
    void load()
  }

  if (!facts)
    return (
      <div className="flex justify-center py-10">
        <Spinner />
      </div>
    )

  const groups = new Map<string, MemoryFactView[]>()
  for (const f of facts) {
    const k = f.scope ?? ''
    if (!groups.has(k)) groups.set(k, [])
    groups.get(k)!.push(f)
  }

  return (
    <div className="space-y-5">
      <div className="flex justify-end">
        <Button onClick={() => setAdding((a) => !a)} variant={adding ? 'soft' : 'primary'} size="sm">
          {adding ? 'Cancel' : '+ Add memory'}
        </Button>
      </div>

      {adding && (
        <Card className="space-y-2 p-4">
          <div className="grid grid-cols-2 gap-2">
            <Input label="Type" value={form.type} onChange={(v) => setForm({ ...form, type: v })} placeholder="location" />
            <Input label="Key" value={form.key} onChange={(v) => setForm({ ...form, key: v })} placeholder="home" />
          </div>
          <Input label="Value" value={form.value} onChange={(v) => setForm({ ...form, value: v })} placeholder="London" />
          <Input label="Context (optional)" value={form.context} onChange={(v) => setForm({ ...form, context: v })} />
          <Input
            label="Scope (optional)"
            value={form.scope}
            onChange={(v) => setForm({ ...form, scope: v })}
            placeholder="blank = global · or a project slug"
          />
          <div className="flex justify-end">
            <Button onClick={submit} size="sm">
              Save
            </Button>
          </div>
        </Card>
      )}

      {facts.length === 0 ? (
        <EmptyState icon="🧠" title="No memories yet" hint="Durable facts Smarty has learned will appear here, grouped by scope." />
      ) : (
        [...groups.entries()].map(([scope, items]) => (
          <section key={scope}>
            <h2 className="mb-2 px-1 text-sm font-semibold uppercase tracking-wide text-ink-mute">{scopeLabel(scope)}</h2>
            <div className="space-y-2">
              {items.map((f) => (
                <Card key={f.id} className="flex items-start gap-3 px-4 py-3">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-medium">{f.key}</span>
                      <span className="text-ink-soft">{f.value}</span>
                      <Pill>{f.type}</Pill>
                    </div>
                    {f.context && <div className="mt-0.5 text-sm text-ink-mute">{f.context}</div>}
                    <div className="mt-0.5 text-xs text-ink-mute">noted {timeAgo(f.asserted)}</div>
                  </div>
                  <Button
                    variant="danger"
                    size="sm"
                    onClick={async () => {
                      await retireMemory(f.id)
                      void load()
                    }}
                  >
                    Retire
                  </Button>
                </Card>
              ))}
            </div>
          </section>
        ))
      )}
    </div>
  )
}

function Input({
  label,
  value,
  onChange,
  placeholder,
}: {
  label: string
  value: string
  onChange: (v: string) => void
  placeholder?: string
}) {
  return (
    <label className="block text-sm">
      <span className="mb-1 block text-xs font-medium text-ink-mute">{label}</span>
      <input
        value={value}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        className="w-full rounded-xl border border-line bg-surface px-3 py-2 outline-none focus:border-accent"
      />
    </label>
  )
}
