#!/usr/bin/env node
/**
 * Every Tailwind class a live panel uses must be in the compiled stylesheet.
 *
 * This is the check for a failure that is completely silent. Tailwind emits only the classes it finds in the source
 * at build time, so a class appearing for the first time inside a component the system generated later is not in
 * the CSS — the element just renders unstyled. The first generated flight panel used `tabular-nums` and
 * `border-ink-faint`; the times didn't align, the cards had no border, and nothing anywhere said why.
 *
 * The fix is a safelist in tailwind.config.js paired with the class list in the builder's brief. This proves the
 * pairing actually holds against the panels that exist, which is the only thing that matters.
 *
 *   npm run build && node scripts/check-classes.mjs [http://localhost:5179]
 */
import fs from 'node:fs'
import path from 'node:path'

const base = process.argv[2] ?? 'http://localhost:5179'

const dist = path.join(process.cwd(), 'dist', 'assets')
if (!fs.existsSync(dist)) {
  console.error('No dist/assets — run `npm run build` first.')
  process.exit(2)
}
const cssFile = fs.readdirSync(dist).filter((f) => f.endsWith('.css')).sort().pop()
const css = fs.readFileSync(path.join(dist, cssFile), 'utf8')

const res = await fetch(`${base}/api/widgets`)
if (!res.ok) {
  console.error(`Couldn't read ${base}/api/widgets — is the API running?`)
  process.exit(2)
}

/** Tailwind escapes the awkward characters in a selector, so compare on the escaped form. */
const escaped = (cls) => cls.replace(/([.:[\]/%])/g, '\\$1')

// Anything inside className="..." or className={`...`}, plus the ternary branches within them.
const CLASS_ATTR = /className=(?:"([^"]*)"|\{`([^`]*)`\}|\{[^}]*?["'`]([^"'`]*)["'`][^}]*?\})/g

let checked = 0
const missing = new Map()

for (const w of await res.json()) {
  if (!w.code) continue
  const classes = new Set()
  for (const m of w.code.matchAll(CLASS_ATTR)) {
    const text = m[1] ?? m[2] ?? m[3] ?? ''
    // Template holes and JS fragments leave debris; a real utility has no spaces or braces in it.
    for (const c of text.split(/\s+/)) {
      if (!c || c.includes('$') || c.includes('{') || c.includes('}')) continue
      classes.add(c)
    }
  }
  for (const c of classes) {
    checked++
    if (!css.includes(escaped(c))) {
      if (!missing.has(w.title)) missing.set(w.title, new Set())
      missing.get(w.title).add(c)
    }
  }
}

if (missing.size === 0) {
  console.log(`ok — all ${checked} class use(s) across the live panels are in ${cssFile}`)
  process.exit(0)
}

console.log('Classes used by a panel but NOT in the stylesheet — these render unstyled:\n')
for (const [title, set] of missing) console.log(`  ${title}: ${[...set].join(' ')}`)
console.log('\nAdd them to `widgetClasses` in tailwind.config.js AND to the class list in the builder brief')
console.log('(Smarty.Api/WidgetTools.cs), or rebuild the panel so it uses what is already there.')
process.exit(1)
