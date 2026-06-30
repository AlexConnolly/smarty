import { useState } from 'react'
import type { TimelineItem } from './store'
import { Markdown, Pill, Spinner, cx } from './ui'

// Friendly labels + glyphs for the tools workers call, so the live stream reads at a glance.
const TOOL_META: Record<string, { glyph: string; label: string }> = {
  run_shell_command: { glyph: '⌘', label: 'Shell' },
  web_search: { glyph: '🔍', label: 'Web search' },
  get_page_answer: { glyph: '📄', label: 'Read page' },
  read_file: { glyph: '📄', label: 'Read file' },
  file_summary: { glyph: '📄', label: 'Summarise file' },
  write_file: { glyph: '✎', label: 'Write file' },
  list_files: { glyph: '🗂', label: 'List files' },
  send_file: { glyph: '📎', label: 'Send file' },
  search_memory: { glyph: '🧠', label: 'Recall memory' },
  set_memory: { glyph: '🧠', label: 'Save memory' },
  run_python: { glyph: '🐍', label: 'Run Python' },
  log_search: { glyph: '📊', label: 'Search logs' },
  log_summary: { glyph: '📊', label: 'Summarise logs' },
  code_search: { glyph: '⟨⟩', label: 'Search code' },
  code_read: { glyph: '⟨⟩', label: 'Read code' },
  code_tree: { glyph: '⟨⟩', label: 'Browse code' },
  jira_search: { glyph: '🪪', label: 'Search Jira' },
  jira_get_issue: { glyph: '🪪', label: 'Get Jira issue' },
  jira_create_issue: { glyph: '🪪', label: 'Create Jira issue' },
}

function prettyArgs(args?: string): string {
  if (!args) return ''
  try {
    const obj = JSON.parse(args)
    return Object.entries(obj)
      .map(([k, v]) => `${k}: ${typeof v === 'string' ? v : JSON.stringify(v)}`)
      .join('  ·  ')
  } catch {
    return args
  }
}

function ToolCall({ name, args, result, done }: { name: string; args?: string; result?: string; done: boolean }) {
  const [open, setOpen] = useState(false)
  const meta = TOOL_META[name] ?? { glyph: '🛠', label: name }
  const preview = prettyArgs(args)
  return (
    <div className="rounded-xl border border-line bg-surface-low/60">
      <button
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center gap-2.5 px-3 py-2 text-left"
      >
        <span className="grid h-7 w-7 shrink-0 place-items-center rounded-lg bg-surface text-sm shadow-card">
          {meta.glyph}
        </span>
        <span className="min-w-0 flex-1">
          <span className="flex items-center gap-2">
            <span className="text-sm font-medium text-ink">{meta.label}</span>
            <span className="font-mono text-[0.7rem] text-ink-mute">{name}</span>
          </span>
          {preview && <span className="block truncate text-xs text-ink-mute">{preview}</span>}
        </span>
        {done ? (
          <span className="text-live" title="done">✓</span>
        ) : (
          <Spinner />
        )}
      </button>
      {open && (
        <div className="space-y-2 border-t border-line px-3 py-2.5 text-xs">
          {args && (
            <div>
              <div className="mb-1 font-medium text-ink-mute">Arguments</div>
              <pre className="overflow-x-auto rounded-lg bg-surface p-2 font-mono text-[0.72rem] text-ink-soft">{tryFormat(args)}</pre>
            </div>
          )}
          {result != null && result !== '' && (
            <div>
              <div className="mb-1 font-medium text-ink-mute">Result</div>
              <pre className="max-h-64 overflow-auto rounded-lg bg-surface p-2 font-mono text-[0.72rem] text-ink-soft whitespace-pre-wrap">{result}</pre>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function tryFormat(s: string): string {
  try {
    return JSON.stringify(JSON.parse(s), null, 2)
  } catch {
    return s
  }
}

function ReasoningBlock({ text }: { text: string }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="text-xs">
      <button onClick={() => setOpen((o) => !o)} className="text-ink-mute hover:text-ink-soft">
        {open ? '▾' : '▸'} thinking
      </button>
      {open && <div className="mt-1 whitespace-pre-wrap rounded-lg bg-surface-low/60 p-2 text-ink-mute">{text}</div>}
    </div>
  )
}

export function Timeline({ items, onAnswer }: { items: TimelineItem[]; onAnswer?: (taskId: string, text: string) => void }) {
  return (
    <div className="space-y-3">
      {items.map((it) => {
        switch (it.kind) {
          case 'message':
            return (
              <div key={it.key} className={cx('flex', it.role === 'user' ? 'justify-end' : 'justify-start')}>
                <div
                  className={cx(
                    'max-w-[88%] rounded-2xl px-3.5 py-2.5',
                    it.role === 'user' ? 'bg-surface-low text-ink' : 'bg-surface shadow-card border border-line/60',
                  )}
                >
                  {it.text ? <Markdown>{it.text}</Markdown> : <span className="text-ink-mute"><Spinner /></span>}
                  {it.live && it.text && <span className="ml-0.5 inline-block h-3 w-1.5 animate-pulse2 bg-accent align-middle" />}
                </div>
              </div>
            )
          case 'reasoning':
            return it.text.trim() ? <ReasoningBlock key={it.key} text={it.text} /> : null
          case 'tool':
            return <ToolCall key={it.key} name={it.name} args={it.args} result={it.result} done={it.done} />
          case 'task':
            return (
              <div key={it.key} className="flex items-center gap-2 text-xs">
                <Pill tone={it.status === 'running' ? 'live' : it.status === 'done' ? 'neutral' : 'wait'}>
                  {it.status === 'running' ? '● task running' : `task ${it.status}`}
                </Pill>
                <span className="truncate text-ink-mute">{it.desc}</span>
              </div>
            )
          case 'question':
            return (
              <div key={it.key} className="rounded-2xl border border-wait/30 bg-wait/5 px-3.5 py-3">
                <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-wait">Worker is asking</div>
                <Markdown>{it.question}</Markdown>
                {it.options.length > 0 && (
                  <div className="mt-2 flex flex-wrap gap-2">
                    {it.options.map((o, i) => (
                      <button
                        key={i}
                        onClick={() => onAnswer?.(it.taskId, o)}
                        className="rounded-full border border-line bg-surface px-3 py-1 text-xs hover:bg-surface-mid"
                      >
                        {o}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            )
          case 'file':
            return (
              <div key={it.key} className="flex items-center gap-2 text-sm">
                <Pill tone="accent">📎 file</Pill>
                <span className="font-medium">{it.name}</span>
                {it.caption && <span className="text-ink-mute">— {it.caption}</span>}
              </div>
            )
        }
      })}
    </div>
  )
}
