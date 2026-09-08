import { useEffect, useRef, useState } from 'react'
import {
  PluginCommandView,
  PluginStageView,
  PluginStatus,
  deletePlugin,
  fetchPlugins,
  resetPluginSetup,
  setPluginEnabled,
  submitPluginSetup,
  uploadPlugin,
} from '../api'
import { Button, Card, EmptyState, Pill, Spinner, cx } from '../ui'

export function PluginsView() {
  const [plugins, setPlugins] = useState<PluginStatus[] | null>(null)
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null)

  const load = async () => setPlugins(await fetchPlugins())
  useEffect(() => {
    void load()
  }, [])

  if (!plugins)
    return (
      <div className="flex justify-center py-10">
        <Spinner />
      </div>
    )

  return (
    <div className="space-y-4">
      <Upload
        onDone={async (result) => {
          setNote({ ok: result.ok, text: result.message })
          await load()
        }}
      />

      {note && (
        <div className={cx('rounded-xl px-3.5 py-2.5 text-sm', note.ok ? 'bg-accent-soft text-accent' : 'bg-danger/10 text-danger')}>
          {note.text}
        </div>
      )}

      {plugins.length === 0 ? (
        <EmptyState title="No plugins" hint="Upload a zip holding a plugin DLL." icon="🧱" />
      ) : (
        <div className="space-y-3">
          {plugins.map((p) => (
            <PluginCard
              key={p.id}
              plugin={p}
              onChanged={async (message, ok) => {
                setNote({ ok, text: message })
                await load()
              }}
            />
          ))}
        </div>
      )}
    </div>
  )
}

function Upload({ onDone }: { onDone: (result: { ok: boolean; message: string }) => void | Promise<void> }) {
  const input = useRef<HTMLInputElement>(null)
  const [busy, setBusy] = useState(false)
  const [drag, setDrag] = useState(false)

  const send = async (files: FileList | File[] | null) => {
    const file = files && Array.from(files)[0]
    if (!file) return
    setBusy(true)
    const result = await uploadPlugin(file)
    setBusy(false)
    await onDone(result)
  }

  return (
    <div
      onDragOver={(e) => {
        e.preventDefault()
        setDrag(true)
      }}
      onDragLeave={() => setDrag(false)}
      onDrop={(e) => {
        e.preventDefault()
        setDrag(false)
        void send(e.dataTransfer.files)
      }}
      className={cx(
        'flex items-center justify-center gap-2 rounded-2xl border border-dashed px-4 py-6 text-sm',
        drag ? 'border-accent bg-accent-soft' : 'border-line text-ink-mute',
      )}
    >
      {busy ? (
        <Spinner />
      ) : (
        <>
          <span>Drop a plugin zip here, or</span>
          <Button size="sm" onClick={() => input.current?.click()}>
            Upload Smarty plugin
          </Button>
          <input
            ref={input}
            type="file"
            accept=".zip"
            hidden
            onChange={(e) => {
              void send(e.target.files)
              e.target.value = ''
            }}
          />
        </>
      )}
    </div>
  )
}

function PluginCard({
  plugin,
  onChanged,
}: {
  plugin: PluginStatus
  onChanged: (message: string, ok: boolean) => void | Promise<void>
}) {
  const [busy, setBusy] = useState(false)
  const [showCommands, setShowCommands] = useState(false)

  const act = async (run: () => Promise<{ ok: boolean; message: string }>) => {
    setBusy(true)
    const result = await run()
    setBusy(false)
    await onChanged(result.message, result.ok)
  }

  return (
    <Card className="px-4 py-3.5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-semibold">{plugin.name}</span>
            {!plugin.enabled && <Pill>off</Pill>}
            {plugin.enabled && plugin.ready && <Pill tone="live">{plugin.commands.length} commands</Pill>}
            {plugin.enabled && !plugin.ready && plugin.loaded && <Pill tone="wait">needs setup</Pill>}
            {plugin.enabled && !plugin.loaded && <Pill tone="danger">didn't load</Pill>}
            {plugin.ready && <span className="font-mono text-xs text-ink-mute">{plugin.personaId}</span>}
          </div>
          <p className="mt-1 max-w-2xl text-sm text-ink-soft">{plugin.description}</p>
          {plugin.error && <p className="mt-1 text-xs text-danger">{plugin.error}</p>}
        </div>

        <div className="flex shrink-0 items-center gap-2">
          {plugin.ready && (
            <Button size="sm" variant="soft" disabled={busy} onClick={() => void act(() => resetPluginSetup(plugin.id))}>
              Set up again
            </Button>
          )}
          <Button
            size="sm"
            variant={plugin.enabled ? 'ghost' : 'primary'}
            disabled={busy}
            onClick={() => void act(() => setPluginEnabled(plugin.id, !plugin.enabled))}
          >
            {plugin.enabled ? 'Turn off' : 'Turn on'}
          </Button>
          <Button size="sm" variant="danger" disabled={busy} onClick={() => void act(() => deletePlugin(plugin.id))}>
            Remove
          </Button>
        </div>
      </div>

      {plugin.setup && (
        <Stage
          key={plugin.setup.id}
          stage={plugin.setup}
          busy={busy}
          onSubmit={(values) => act(() => submitPluginSetup(plugin.id, plugin.setup!.id, values))}
        />
      )}

      {plugin.commands.length > 0 && (
        <>
          <button onClick={() => setShowCommands((s) => !s)} className="mt-2 text-xs text-accent hover:underline">
            {showCommands ? 'Hide' : 'Show'} commands ({plugin.commands.length})
          </button>
          {showCommands && (
            <div className="mt-2 space-y-1.5">
              {plugin.commands.map((c) => (
                <CommandRow key={c.tool} command={c} />
              ))}
            </div>
          )}
        </>
      )}
    </Card>
  )
}

/**
 * One step of a plugin's setup.
 *
 * Keyed on the stage id by the caller, so moving to the next step mounts a fresh form rather than carrying the
 * previous step's answers into it — which is what stops a code being typed over an email address, and stops a
 * secret lingering in a field after it has been used.
 */
function Stage({
  stage,
  busy,
  onSubmit,
}: {
  stage: PluginStageView
  busy: boolean
  onSubmit: (values: Record<string, string>) => void | Promise<void>
}) {
  const [values, setValues] = useState<Record<string, string>>({})
  const missing = stage.fields.filter((f) => f.required && !(values[f.name] ?? '').trim())

  return (
    <form
      className="mt-3 space-y-2 border-t border-line pt-3"
      onSubmit={(e) => {
        e.preventDefault()
        if (!busy && missing.length === 0) void onSubmit(values)
      }}
    >
      <div className="text-sm font-medium">{stage.title}</div>
      {stage.instruction && <p className="text-xs text-ink-mute">{stage.instruction}</p>}
      {stage.problem && <p className="text-xs text-danger">{stage.problem}</p>}

      {stage.fields.map((field) => (
        <label key={field.name} className="flex flex-wrap items-center gap-2 text-sm">
          <span className="w-40 shrink-0 font-mono text-xs text-ink-soft">
            {field.name}
            {field.required && <span className="text-danger"> *</span>}
          </span>
          {field.type === 'boolean' ? (
            <input
              type="checkbox"
              checked={values[field.name] === 'true'}
              onChange={(e) => setValues((v) => ({ ...v, [field.name]: e.target.checked ? 'true' : 'false' }))}
              className="h-4 w-4"
            />
          ) : (
            <input
              type={field.secret ? 'password' : field.type === 'integer' || field.type === 'number' ? 'number' : 'text'}
              step={field.type === 'number' ? 'any' : undefined}
              autoComplete="off"
              value={values[field.name] ?? ''}
              placeholder={field.description}
              onChange={(e) => setValues((v) => ({ ...v, [field.name]: e.target.value }))}
              className="min-w-0 flex-1 rounded-lg border border-line bg-surface px-3 py-1.5 text-sm outline-none focus:border-accent"
            />
          )}
        </label>
      ))}

      <div className="flex justify-end">
        <Button size="sm" type="submit" disabled={busy || missing.length > 0}>
          {busy ? 'Working…' : stage.fields.length === 0 ? 'Continue' : 'Save'}
        </Button>
      </div>
    </form>
  )
}

function CommandRow({ command }: { command: PluginCommandView }) {
  return (
    <div className="rounded-lg bg-surface-low/60 px-3 py-2">
      <div className="flex items-baseline gap-2">
        <span className="font-mono text-xs text-ink">{command.tool}</span>
        <span className="min-w-0 flex-1 truncate text-xs text-ink-mute">{command.description}</span>
      </div>
      {command.parameters.length > 0 && (
        <div className="mt-1.5 space-y-1 border-t border-line pt-1.5">
          {command.parameters.map((p) => (
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
