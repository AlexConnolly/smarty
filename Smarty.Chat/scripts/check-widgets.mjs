#!/usr/bin/env node
/**
 * Compile and render every panel currently on the home page, outside the browser.
 *
 * This exists because of a bug that would otherwise have shipped: the runtime's first version wrapped a panel's
 * body in a way that did not create a function scope, so Babel rejected the top-level `return` and EVERY panel —
 * including the three hand-written seeds — rendered "this panel wouldn't compile". Nothing in the C# tests could
 * see that, because the failure was entirely in the browser half.
 *
 * Each panel is transpiled exactly as WidgetRuntime does it, then called twice: once with the feed data it has,
 * and once with `data` null, which is the very first render before any feed has answered. A panel that throws in
 * either case is a blank box on someone's home page.
 *
 *   node scripts/check-widgets.mjs [http://localhost:5179]
 */
import babel from '@babel/standalone'

const base = process.argv[2] ?? 'http://localhost:5179'

// Must match SCOPE_KEYS in WidgetRuntime.tsx and the kit listed in the builder's brief.
const NAMES = [
  'React', 'useState', 'useMemo', 'useEffect', 'useRef', 'data', 'params', 'history', 'fetch', 'act',
  'Stat', 'Row', 'Progress', 'Bars', 'Badge', 'Dot', 'Meta', 'Icon',
  'Sparkline', 'Trend', 'Kpi', 'Link', 'Stagger',
  'Hero', 'Ring', 'Tile', 'Avatar', 'Chips',
  'Header', 'Panel', 'List', 'Thing', 'Pictures', 'Button', 'Action', 'Slides',
]

function compile(code) {
  const wrapped = `(function widget(${NAMES.join(', ')}) {\n"use strict";\n${code}\n})`
  const { code: js } = babel.transform(wrapped, {
    presets: [['react', { runtime: 'classic', pragma: 'React.createElement', pragmaFrag: 'React.Fragment' }]],
    filename: 'widget.jsx',
    sourceType: 'script',
  })
  return new Function(`return ${js}`)()
}

/** Enough of React and the kit to prove the tree builds. Nothing here needs to be real. */
function scope(data, params, history) {
  const createElement = (type, props, ...children) => ({ type, props, children })
  const primitive = (name) => (props) => ({ type: name, props })
  const hooks = {
    useState: () => [null, () => {}],
    useMemo: (f) => f(),
    useEffect: () => {},
    useRef: () => ({ current: null }),
  }
  return {
    React: { createElement, Fragment: 'Fragment', ...hooks },
    ...hooks,
    data,
    params: params ?? {},
    // Empty is the honest default: a panel's series does not exist until it has been loaded more than once, and a
    // component that assumes one is a component that breaks on the day it is built.
    history: history ?? {},
    // Never called: the harness proves the tree builds, and a component that fetches on render would reach the
    // network from a test run.
    fetch: () => Promise.resolve({ ok: true, json: () => Promise.resolve({}) }),
    Stat: primitive('Stat'), Row: primitive('Row'), Progress: primitive('Progress'),
    Bars: primitive('Bars'), Badge: primitive('Badge'), Dot: primitive('Dot'),
    Meta: primitive('Meta'), Icon: primitive('Icon'),
    Sparkline: primitive('Sparkline'), Trend: primitive('Trend'), Kpi: primitive('Kpi'),
    Link: primitive('Link'), Stagger: primitive('Stagger'),
    Hero: primitive('Hero'), Ring: primitive('Ring'), Tile: primitive('Tile'),
    Avatar: primitive('Avatar'), Chips: primitive('Chips'),
    Header: primitive('Header'), Panel: primitive('Panel'), List: primitive('List'),
    Thing: primitive('Thing'), Pictures: primitive('Pictures'),
    Button: primitive('Button'), Action: primitive('Action'), Slides: primitive('Slides'),
    // Never called here: the harness proves the tree builds, and a panel that ran its own commands
    // while being checked would clean the house every time the check ran.
    act: async () => ({ ok: true, text: '' }),
  }
}

const res = await fetch(`${base}/api/widgets`)
if (!res.ok) {
  console.error(`Couldn't read ${base}/api/widgets — is the API running?`)
  process.exit(2)
}

let ok = 0
const failures = []

for (const w of await res.json()) {
  if (!w.code) continue
  try {
    const fn = compile(w.code)
    const call = (data) => fn(...NAMES.map((n) => scope(data, w.parameters, w.history)[n]))
    call(w.data ?? null)
    call(null) // the first render, before any feed has answered
    console.log(`ok    ${w.title}`)
    ok++
  } catch (err) {
    console.log(`FAIL  ${w.title} — ${err.message}`)
    failures.push(w.title)
  }
}

console.log(`\n${ok} panel(s) compile and render${failures.length ? `, ${failures.length} broken` : ''}`)
process.exit(failures.length ? 1 : 0)
