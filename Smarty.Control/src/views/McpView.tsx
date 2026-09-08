import { useEffect, useState } from 'react'
import {
  McpCatalogueEntry,
  McpServerConfigView,
  McpServerStatus,
  deleteMcpServer,
  fetchMcpCatalogue,
  fetchMcpConfig,
  fetchMcpServers,
  installMcpBuiltIn,
  reconnectMcpServer,
  saveMcpServer,
  setMcpEnabled,
} from '../api'
import { Button, Card, EmptyState, Pill, SectionTitle, Spinner, cx } from '../ui'

/**
 * The built-ins: servers Smarty already knows how to run, so switching one on is a toggle rather than a form.
 * When the machine-specific bit (a checkout, a folder) can't be found, the toggle turns into a single path field
 * instead of failing silently.
 */
function BuiltIns({ entries, onChanged }: { entries: McpCatalogueEntry[]; onChanged: (message: string) => void }) {
  const [busy, setBusy] = useState<string | null>(null)
  const [asking, setAsking] = useState<string | null>(null)
  const [path, setPath] = useState('')

  const turnOn = async (entry: McpCatalogueEntry, withPath?: string) => {
    setBusy(entry.key)
    const result = await installMcpBuiltIn(entry.key, withPath)
    setBusy(null)
    if (!result.saved) {
      // Almost always "couldn't find it" — so ask for the path rather than just reporting failure.
      setAsking(entry.key)
      setPath(entry.discoveredPath ?? '')
      onChanged(result.message)
      return
    }
    setAsking(null)
    onChanged(result.message)
  }

  const flip = async (entry: McpCatalogueEntry) => {
    setBusy(entry.key)
    const result = await setMcpEnabled(entry.name, !entry.enabled)
    setBusy(null)
    onChanged(result.message)
  }

  return (
    <div className="space-y-3">
      <SectionTitle>Built in</SectionTitle>
      {entries.map((e) => (
        <Card key={e.key}>
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-semibold">{e.name}</span>
                <Pill tone="accent">{e.function}</Pill>
                {e.installed && e.enabled && e.connected && <Pill tone="live">on · {e.toolCount} tools</Pill>}
                {e.installed && e.enabled && !e.connected && <Pill tone="danger">on · not connected</Pill>}
                {(!e.installed || !e.enabled) && <Pill tone="neutral">off</Pill>}
              </div>
              <p className="mt-1 max-w-2xl text-sm text-ink-soft">{e.summary}</p>
              <p className="mt-1 text-xs text-ink-mute">Needs: {e.requires}</p>
              {e.discoveredPath && !e.installed && (
                <p className="mt-1 truncate font-mono text-xs text-ink-mute">found: {e.discoveredPath}</p>
              )}
              {e.error && <p className="mt-1 text-xs text-danger">{e.error}</p>}
            </div>

            <div className="flex shrink-0 items-center gap-2">
              {e.homepage && (
                <a
                  className="text-xs text-ink-mute underline hover:text-ink"
                  href={e.homepage}
                  target="_blank"
                  rel="noreferrer"
                >
                  docs
                </a>
              )}
              {e.installed ? (
                <Button size="sm" variant={e.enabled ? 'ghost' : 'primary'} disabled={busy === e.key} onClick={() => void flip(e)}>
                  {busy === e.key ? '…' : e.enabled ? 'Turn off' : 'Turn on'}
                </Button>
              ) : (
                <Button size="sm" disabled={busy === e.key} onClick={() => void turnOn(e)}>
                  {busy === e.key ? 'Starting…' : 'Turn on'}
                </Button>
              )}
            </div>
          </div>

          {asking === e.key && (
            <div className="mt-3 border-t border-line pt-3">
              <label className="mb-1 block text-xs font-medium uppercase tracking-wide text-ink-mute">
                {e.pathArgument}
              </label>
              <div className="flex flex-wrap gap-2">
                <input
                  className="min-w-0 flex-1 rounded-lg border border-line bg-surface px-3 py-2 font-mono text-xs outline-none focus:border-accent"
                  value={path}
                  onChange={(ev) => setPath(ev.target.value)}
                  placeholder="C:\\Users\\you\\open-chrome-mcp\\server\\src\\index.js"
                />
                <Button size="sm" disabled={busy === e.key || !path.trim()} onClick={() => void turnOn(e, path.trim())}>
                  Use this
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setAsking(null)}>
                  Cancel
                </Button>
              </div>
            </div>
          )}
        </Card>
      ))}
    </div>
  )
}

/**
 * MCP servers: add one, edit it, restart it, remove it — live. Saving starts the server and registers its tools
 * with no restart, so the result line says what actually happened ("connected — 16 tools") rather than just
 * "saved", which would look identical whether or not it worked.
 */
export function McpView() {
  const [servers, setServers] = useState<McpServerStatus[] | null>(null)
  const [catalogue, setCatalogue] = useState<McpCatalogueEntry[]>([])
  const [editing, setEditing] = useState<McpServerConfigView | 'new' | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)

  const load = async () => {
    const [s, c] = await Promise.all([fetchMcpServers(), fetchMcpCatalogue()])
    setServers(s)
    setCatalogue(c)
  }
  useEffect(() => {
    void load()
  }, [])

  const edit = async (name: string) => {
    const config = await fetchMcpConfig(name)
    if (config) setEditing(config)
  }

  const remove = async (name: string) => {
    setBusy(name)
    await deleteMcpServer(name)
    setNote(`Removed ${name}. Its process was stopped.`)
    await load()
    setBusy(null)
  }

  const reconnect = async (name: string) => {
    setBusy(name)
    const result = await reconnectMcpServer(name)
    setNote(
      result.connected
        ? `${name} reconnected — ${result.tools.length} tool(s).`
        : `${name} still won't connect: ${result.error ?? 'no reason given'}`,
    )
    await load()
    setBusy(null)
  }

  if (!servers)
    return (
      <div className="flex justify-center py-10">
        <Spinner />
      </div>
    )

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-2 px-1">
        <p className="max-w-2xl text-sm text-ink-mute">
          Tools from other processes, offered to Smarty's workers as if they were built in. A server that claims the{' '}
          <code className="rounded bg-surface-mid px-1">browser</code> function becomes how Smarty reaches the web.
        </p>
        <Button size="sm" onClick={() => setEditing('new')}>
          + Add server
        </Button>
      </div>

      {note && (
        <Card className="border-accent/40 text-sm">
          <div className="flex items-start justify-between gap-3">
            <span>{note}</span>
            <button className="text-ink-mute hover:text-ink" onClick={() => setNote(null)}>
              ✕
            </button>
          </div>
        </Card>
      )}

      {editing && (
        <McpEditor
          server={editing === 'new' ? null : editing}
          onCancel={() => setEditing(null)}
          onSaved={async (message) => {
            setNote(message)
            setEditing(null)
            await load()
          }}
        />
      )}

      <BuiltIns
        entries={catalogue}
        onChanged={async (message) => {
          setNote(message)
          await load()
        }}
      />

      {(() => {
        const builtInNames = new Set(catalogue.map((c) => c.name.toLowerCase()))
        const custom = servers.filter((s) => !builtInNames.has(s.name.toLowerCase()))
        return (
          <div className="space-y-3">
            <SectionTitle>Your own</SectionTitle>
            {custom.length === 0 && !editing && (
              <EmptyState
                title="Nothing custom yet"
                hint="Add any server that speaks MCP — the built-ins above are just the ones Smarty already knows how to run."
              />
            )}
            {custom.map((s) => (
          <Card key={s.name}>
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-semibold">{s.name}</span>
                  {!s.enabled ? (
                    <Pill tone="neutral">disabled</Pill>
                  ) : s.connected ? (
                    <Pill tone="live">connected</Pill>
                  ) : (
                    <Pill tone="danger">not connected</Pill>
                  )}
                  {s.functions.map((f) => (
                    <Pill key={f} tone="accent">
                      {f}
                    </Pill>
                  ))}
                </div>

                <p className="mt-1 truncate font-mono text-xs text-ink-mute" title={s.command}>
                  {s.command}
                </p>

                {s.connected && (
                  <p className="mt-1 text-xs text-ink-mute">
                    {s.serverName} {s.serverVersion} · protocol {s.protocolVersion} · prefix{' '}
                    <code className="rounded bg-surface-mid px-1">{s.toolPrefix}_</code>
                  </p>
                )}

                {s.error && <p className="mt-2 text-xs text-danger">{s.error}</p>}

                {s.tools.length > 0 && (
                  <div className="mt-2 flex flex-wrap gap-1">
                    {s.tools.map((t) => (
                      <span key={t} className="rounded bg-surface-mid px-1.5 py-0.5 font-mono text-[11px] text-ink-soft">
                        {t}
                      </span>
                    ))}
                  </div>
                )}

                {/* Tools the server offers that the allow-list is keeping away from the model. */}
                {s.offeredTools.filter((t) => !s.tools.some((k) => k.endsWith(t))).length > 0 && (
                  <p className="mt-2 text-xs text-ink-mute">
                    filtered out: {s.offeredTools.filter((t) => !s.tools.some((k) => k.endsWith(t))).join(', ')}
                  </p>
                )}
              </div>

              <div className="flex shrink-0 gap-2">
                <Button size="sm" variant="ghost" onClick={() => void edit(s.name)}>
                  Edit
                </Button>
                <Button size="sm" variant="ghost" disabled={busy === s.name} onClick={() => void reconnect(s.name)}>
                  {busy === s.name ? '…' : 'Restart'}
                </Button>
                <Button size="sm" variant="danger" disabled={busy === s.name} onClick={() => void remove(s.name)}>
                  Remove
                </Button>
              </div>
            </div>
              </Card>
            ))}
          </div>
        )
      })()}
    </div>
  )
}

function McpEditor({
  server,
  onCancel,
  onSaved,
}: {
  server: McpServerConfigView | null
  onCancel: () => void
  onSaved: (message: string) => void
}) {
  const [name, setName] = useState(server?.name ?? '')
  const [command, setCommand] = useState(server?.command ?? 'node')
  const [args, setArgs] = useState((server?.args ?? []).join('\n'))
  const [cwd, setCwd] = useState(server?.cwd ?? '')
  const [env, setEnv] = useState(
    // Keys come back without values, so an existing secret isn't shipped to the browser. Re-typing a value
    // overwrites it; leaving it blank clears it, which the hint below says out loud.
    (server?.envKeys ?? []).map((k) => `${k}=`).join('\n'),
  )
  const [functions, setFunctions] = useState((server?.functions ?? []).join(', '))
  const [prefix, setPrefix] = useState(server?.prefix ?? '')
  const [tools, setTools] = useState((server?.tools ?? []).join(', '))
  const [promptHint, setPromptHint] = useState(server?.promptHint ?? '')
  const [enabled, setEnabled] = useState(server?.enabled ?? true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const save = async () => {
    if (!name.trim() || !command.trim()) {
      setError('A name and a command are both required.')
      return
    }
    setSaving(true)
    setError(null)

    const envMap: Record<string, string> = {}
    for (const line of env.split('\n')) {
      const at = line.indexOf('=')
      if (at <= 0) continue
      const key = line.slice(0, at).trim()
      if (key) envMap[key] = line.slice(at + 1).trim()
    }

    const result = await saveMcpServer({
      name: name.trim(),
      command: command.trim(),
      args: args.split('\n').map((a) => a.trim()).filter(Boolean),
      env: envMap,
      cwd: cwd.trim() || undefined,
      enabled,
      prefix: prefix.trim() || undefined,
      tools: tools.split(',').map((t) => t.trim()).filter(Boolean),
      functions: functions.split(',').map((f) => f.trim()).filter(Boolean),
      promptHint: promptHint.trim() || undefined,
    })

    setSaving(false)
    if (!result.saved) {
      setError(result.message)
      return
    }
    onSaved(result.message)
  }

  const field = 'w-full rounded-lg border border-line bg-surface px-3 py-2 text-sm outline-none focus:border-accent'
  const label = 'mb-1 block text-xs font-medium uppercase tracking-wide text-ink-mute'

  return (
    <Card className="border-accent/40">
      <h3 className="mb-4 font-semibold">{server ? `Edit ${server.name}` : 'Add an MCP server'}</h3>

      <div className="grid gap-4 sm:grid-cols-2">
        <div>
          <label className={label}>Name</label>
          <input className={field} value={name} onChange={(e) => setName(e.target.value)} placeholder="chrome" />
          <p className="mt-1 text-xs text-ink-mute">Prefixes its tools, so two servers can both offer a "navigate".</p>
        </div>

        <div>
          <label className={label}>Command</label>
          <input className={field} value={command} onChange={(e) => setCommand(e.target.value)} placeholder="node" />
          <p className="mt-1 text-xs text-ink-mute">node, npx, python, or a full path.</p>
        </div>

        <div className="sm:col-span-2">
          <label className={label}>Arguments — one per line</label>
          <textarea
            className={cx(field, 'h-24 font-mono text-xs')}
            value={args}
            onChange={(e) => setArgs(e.target.value)}
            placeholder={'%USERPROFILE%\\open-chrome-mcp\\server\\src\\index.js'}
          />
        </div>

        <div className="sm:col-span-2">
          <label className={label}>Environment — KEY=value, one per line</label>
          <textarea
            className={cx(field, 'h-20 font-mono text-xs')}
            value={env}
            onChange={(e) => setEnv(e.target.value)}
            placeholder="OPEN_CHROME_MCP_PORT=8777"
          />
          <p className="mt-1 text-xs text-ink-mute">
            Where a server's credentials belong. Existing values aren't shown — retype one to change it, or leave it
            blank to clear it.
          </p>
        </div>

        <div>
          <label className={label}>Functions</label>
          <input
            className={field}
            value={functions}
            onChange={(e) => setFunctions(e.target.value)}
            placeholder="browser"
          />
          <p className="mt-1 text-xs text-ink-mute">
            What this server IS, so personas ask for a capability rather than a server. <code>browser</code> makes it
            how Smarty reaches the web.
          </p>
        </div>

        <div>
          <label className={label}>Working directory (optional)</label>
          <input className={field} value={cwd} onChange={(e) => setCwd(e.target.value)} />
        </div>

        <div>
          <label className={label}>Tool prefix (optional)</label>
          <input className={field} value={prefix} onChange={(e) => setPrefix(e.target.value)} placeholder="defaults to the name" />
        </div>

        <div>
          <label className={label}>Allow-list (optional)</label>
          <input className={field} value={tools} onChange={(e) => setTools(e.target.value)} placeholder="navigate, read_page" />
          <p className="mt-1 text-xs text-ink-mute">Empty = every tool it offers.</p>
        </div>

        <div className="sm:col-span-2">
          <label className={label}>How should a worker use it? (optional)</label>
          <textarea
            className={cx(field, 'h-20')}
            value={promptHint}
            onChange={(e) => setPromptHint(e.target.value)}
            placeholder="Call chrome_tabs_context first, then chrome_navigate and chrome_read_page…"
          />
          <p className="mt-1 text-xs text-ink-mute">Woven into the worker's prompt when this server is connected.</p>
        </div>

        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
          Enabled
        </label>
      </div>

      {error && <p className="mt-3 text-sm text-danger">{error}</p>}

      <div className="mt-5 flex items-center gap-2">
        <Button onClick={() => void save()} disabled={saving}>
          {saving ? 'Starting it…' : server ? 'Save and restart' : 'Add and connect'}
        </Button>
        <Button variant="ghost" onClick={onCancel} disabled={saving}>
          Cancel
        </Button>
        <span className="text-xs text-ink-mute">Saving starts the server now — no restart needed.</span>
      </div>

      <p className="mt-3 text-xs text-ink-mute">
        ⚠️ An MCP server runs with your privileges. Only add ones you trust, and use the allow-list to narrow a
        server to the calls you actually want available.
      </p>
    </Card>
  )
}
