import * as React from 'react'
import { transform } from '@babel/standalone'
import { KIT, PanelActions } from './widgetKit'

/**
 * Rendering a component the system wrote.
 *
 * The panel's code arrives as JSX, which the browser can't run, so it is transpiled here with Babel and evaluated
 * into a function. That is the price of the design: a panel that looks like the thing it is about has to be code,
 * and code written after the app was built cannot have been bundled with it.
 *
 * Three things make that safe enough to do:
 *
 * - It runs inside an error boundary, per panel. A component that throws — and one will, the first time a feed
 *   returns a shape it didn't expect — takes down its own box and nothing else. Without this a single bad panel
 *   white-screens the whole app, which is unarguably worse than the old fixed templates.
 * - It is given a scope and nothing else: React, the hooks, the kit, and `data`. No fetch, no window, no document.
 *   Not a security boundary — nothing in a browser is — but it means an honest mistake can't reach anything.
 * - It is compiled once and cached by code, not on every render.
 */

type Compiled = (scope: Record<string, unknown>) => React.ReactNode

const cache = new Map<string, Compiled | Error>()

function compile(code: string): Compiled | Error {
  const hit = cache.get(code)
  if (hit) return hit

  let result: Compiled | Error
  try {
    const names = Object.keys(SCOPE_KEYS)

    // Wrapped in a real function BEFORE transpiling, with the scope as its parameters. The obvious-looking
    // alternative — transpile the bare body and hand it to `new Function` — does not work: Babel parses the
    // source as a script and rejects a top-level `return` outright, so every panel fails to compile. That was
    // the first version, and it failed on all four panels at once.
    const wrapped = `(function widget(${names.join(', ')}) {\n"use strict";\n${code}\n})`

    const { code: js } = transform(wrapped, {
      presets: [['react', { runtime: 'classic', pragma: 'React.createElement', pragmaFrag: 'React.Fragment' }]],
      filename: 'widget.jsx',
      sourceType: 'script',
    })
    if (!js) throw new Error('nothing came back from the transpiler')

    // eslint-disable-next-line @typescript-eslint/no-implied-eval, no-new-func
    const fn = new Function(`return ${js}`)() as (...args: unknown[]) => React.ReactNode
    result = (scope) => fn(...names.map((n) => scope[n]))
  } catch (err) {
    result = err instanceof Error ? err : new Error(String(err))
  }

  cache.set(code, result)
  return result
}

/** The names a generated component may use. Declared once so compile and render can't disagree. */
const SCOPE_KEYS = {
  React: null,
  useState: null,
  useMemo: null,
  useEffect: null,
  useRef: null,
  data: null,
  params: null,
  history: null,
  act: null,
  // Client-mode panels do their own loading, so they get the two things that needs. Withheld from the others by
  // the publish-time contract rather than by the scope, because a scope that varies per panel means a component
  // compiled under one and rendered under another — and the compiled function is cached by code.
  fetch: null,
  ...Object.fromEntries(Object.keys(KIT).map((k) => [k, null])),
} as Record<string, null>

/**
 * What a panel needs to be a control rather than a readout.
 *
 * `act(plugin, command, parameters)` runs one of an installed plugin's commands on the server and hands back
 * `{ ok, text, data }`. Not gated on which command or which parameters: a panel that can show a vacuum's
 * battery can start it, stop it, and send it to a named room. A panel that can only read is a dashboard.
 *
 * What it does NOT do is act on its own. A person presses the button; this is the wire behind the button.
 */
function actFor(widgetId: string | undefined) {
  return async (plugin: string, command: string, parameters?: Record<string, string>) => {
    if (!widgetId) return { ok: false, text: 'this panel has no id, so it cannot act' }
    try {
      const res = await fetch(`/api/widgets/${encodeURIComponent(widgetId)}/act`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ plugin, command, parameters: parameters ?? {} }),
      })
      return (await res.json()) as { ok: boolean; text: string; data?: unknown }
    } catch (e) {
      return { ok: false, text: String(e) }
    }
  }
}

/** Asks the server to reload this panel now, so a button's effect shows without waiting for the timer. */
function refreshFor(widgetId: string | undefined) {
  return () => {
    if (!widgetId) return
    void fetch(`/api/widgets/${encodeURIComponent(widgetId)}/refresh`, { method: 'POST' }).catch(() => {})
  }
}

function scopeFor(
  data: unknown,
  params: Record<string, string>,
  history: unknown,
  act: unknown,
): Record<string, unknown> {
  return {
    React,
    useState: React.useState,
    useMemo: React.useMemo,
    useEffect: React.useEffect,
    useRef: React.useRef,
    data: data ?? null,
    // The values that make this instance about a particular thing. A panel usually has to say what it is about and
    // the feed often can't tell it — a flight endpoint keyed on a flight number tends not to echo the number back.
    params,
    // What this panel has seen before now, already differenced: for each remembered field, {values, at, first, last,
    // change, percent, min, max, since}. Empty until it has been loaded more than once, so a component must read it
    // as "might not be there yet" rather than assuming a series exists.
    history: history ?? {},
    // Runs a plugin command and returns { ok, text, data }. Every panel gets it, because a button is the
    // whole reason a panel would want one.
    act,
    // Bound, so a client-mode component can reach an endpoint the server cannot.
    fetch: window.fetch.bind(window),
    ...KIT,
  }
}

/**
 * One panel's body. Compile failures and render failures are shown, not swallowed — a blank box with the reason in
 * a console nobody opens is exactly how the previous version managed to look broken while "working".
 */
export function WidgetBody({
  code,
  id,
  data,
  params,
  history,
  onThrew,
  onFaults,
}: {
  code: string
  /** The panel this is, so its component can act as it. */
  id?: string
  data: unknown
  params?: Record<string, string>
  /** The panel's own series, per remembered field. Absent on a panel that remembers nothing. */
  history?: unknown
  /** Reported so the fault can be sent back to whoever wrote the code, with the message attached. */
  onThrew?: (error: string) => void
  /**
   * Resources the panel tried to load and couldn't.
   *
   * The signal that was missing entirely. A broken <img> is the single most likely fault in a client-mode panel and
   * it is completely silent: nothing throws, React renders happily, and the only evidence is an icon on the screen.
   * But the browser knows exactly what happened and says so — an `error` event naming the url that failed. Catching
   * it turns "the panel looks wrong" into "GET https://… did not return an image", which is a fault someone can
   * actually act on.
   */
  onFaults?: (faults: string[]) => void
}) {
  const compiled = React.useMemo(() => compile(code), [code])
  const box = React.useRef<HTMLDivElement>(null)

  /*
   * Listened for on the capture phase, because a resource error does not bubble. Scoped to this panel's own box so
   * two panels never report each other's faults, and collected for a moment before reporting: an image and a video
   * failing together is one diagnosis, not two.
   */
  React.useEffect(() => {
    const node = box.current
    if (!node || !onFaults) return

    const seen = new Set<string>()
    let timer: number | undefined

    const onError = (e: Event) => {
      const el = e.target as HTMLElement | null
      if (!el || !('tagName' in el)) return
      const src = (el as HTMLImageElement | HTMLVideoElement).currentSrc ||
        (el as HTMLImageElement).src || el.getAttribute('src') || '(no src)'
      seen.add(`<${el.tagName.toLowerCase()}> failed to load: ${src}`)

      window.clearTimeout(timer)
      timer = window.setTimeout(() => onFaults([...seen]), 1200)
    }

    node.addEventListener('error', onError, true)
    return () => {
      window.clearTimeout(timer)
      node.removeEventListener('error', onError, true)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [code, onFaults])

  // A compile failure is a fault in the code exactly as much as a throw is, and just as fixable — the difference
  // is only which stage caught it, so both are reported.
  React.useEffect(() => {
    if (compiled instanceof Error) onThrew?.(`Compile error: ${compiled.message}`)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [compiled])

  if (compiled instanceof Error) return <Broken what="wouldn't compile" detail={compiled.message} />

  return (
    <div ref={box} className="h-full">
      <Boundary code={code} onThrew={onThrew}>
        <Render compiled={compiled} id={id} data={data} params={params ?? {}} history={history} />
      </Boundary>
    </div>
  )
}

function Render({
  compiled,
  id,
  data,
  params,
  history,
}: {
  compiled: Compiled
  id?: string
  data: unknown
  params: Record<string, string>
  history?: unknown
}) {
  // Rebuilt only when the panel changes, so a component holding it across renders keeps the same function.
  const act = React.useMemo(() => actFor(id), [id])
  const wired = React.useMemo(() => ({ act, refresh: refreshFor(id) }), [act, id])
  // Called as a plain function rather than mounted as a component: the code is a body returning JSX, and hooks in
  // it belong to this component's own slot. Wrapping it in another component per render would remount on every
  // feed update and lose any state it kept.
  // The kit's <Action> reaches this rather than being handed a prop, so it can sit anywhere in the tree.
  return <PanelActions.Provider value={wired}>{compiled(scopeFor(data, params, history, act))}</PanelActions.Provider>
}

class Boundary extends React.Component<
  { code: string; children: React.ReactNode; onThrew?: (error: string) => void },
  { error: Error | null }
> {
  state: { error: Error | null } = { error: null }

  static getDerivedStateFromError(error: Error) {
    return { error }
  }

  componentDidCatch(error: Error) {
    // The whole point of catching it here rather than letting it reach the app: the message is the one thing that
    // makes the fault fixable, and it is only in scope at this moment.
    this.props.onThrew?.(error.message)
  }

  componentDidUpdate(prev: { code: string }) {
    // A rebuild is a fresh chance. Without this, a panel that threw once stays broken until a reload even after
    // the worker has published a fix.
    if (prev.code !== this.props.code && this.state.error) this.setState({ error: null })
  }

  render() {
    if (this.state.error) return <Broken what="hit an error" detail={this.state.error.message} />
    return this.props.children
  }
}

function Broken({ what, detail }: { what: string; detail: string }) {
  return (
    <div className="flex h-full flex-col justify-center gap-1">
      <div className="text-xs font-medium text-danger">This panel {what}</div>
      <div className="line-clamp-3 font-mono text-[0.625rem] leading-relaxed text-ink-mute">{detail}</div>
    </div>
  )
}
