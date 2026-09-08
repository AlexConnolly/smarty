import React, { type ReactNode } from 'react'

/**
 * The kit a generated panel builds out of.
 *
 * A model writing raw markup gets the details wrong in ways that look cheap — inconsistent type scale, arbitrary
 * greys, a progress bar 3px tall next to one 8px tall — and it gets them wrong differently on every panel. So the
 * things that carry the design are components here, styled once, and the panel composes them. Layout and spacing
 * are left to Tailwind because those genuinely vary per panel; colour, weight and proportion do not.
 *
 * Every one of these is listed by name in the builder's brief. Adding one here means adding it there, or nothing
 * will ever use it.
 */

export type Tone = 'good' | 'warn' | 'bad' | 'accent' | 'neutral'

const TEXT: Record<Tone, string> = {
  good: 'text-emerald-600',
  warn: 'text-amber-600',
  bad: 'text-danger',
  accent: 'text-accent',
  neutral: 'text-ink',
}

const FILL: Record<Tone, string> = {
  good: 'bg-emerald-500',
  warn: 'bg-amber-500',
  bad: 'bg-danger',
  accent: 'bg-accent',
  neutral: 'bg-ink-mute',
}

const STROKE: Record<Tone, string> = {
  good: 'stroke-emerald-500',
  warn: 'stroke-amber-500',
  bad: 'stroke-danger',
  accent: 'stroke-accent',
  neutral: 'stroke-ink-mute',
}

const SOFT: Record<Tone, string> = {
  good: 'bg-emerald-50 text-emerald-700',
  warn: 'bg-amber-50 text-amber-700',
  bad: 'bg-red-50 text-danger',
  accent: 'bg-accent-soft text-accent',
  neutral: 'bg-surface-low text-ink-soft',
}

const tone = (t?: string): Tone => (t && t in TEXT ? (t as Tone) : 'neutral')

/** One figure with a label under it. The workhorse — most panels are one or two of these. */
export function Stat({
  label,
  value,
  unit,
  tone: t,
}: {
  label?: ReactNode
  value?: ReactNode
  unit?: string
  tone?: string
}) {
  const text = String(value ?? '—')
  // Steps down as it lengthens rather than wrapping or clipping: the box is fixed and the type is what gives.
  const size = text.length > 12 ? 'text-lg' : text.length > 7 ? 'text-2xl' : 'text-[1.75rem]'
  return (
    <div className="min-w-0">
      <div className={`truncate font-semibold leading-none tracking-tight ${size} ${TEXT[tone(t)]}`}>
        {text}
        {unit && <span className="ml-0.5 text-[0.6em] font-medium text-ink-mute">{unit}</span>}
      </div>
      {label !== undefined && label !== null && (
        <div className="mt-1 truncate text-[0.6875rem] uppercase tracking-wide text-ink-mute">{label}</div>
      )}
    </div>
  )
}

/** A label on the left, a value on the right. What a list of things is made of. */
export function Row({
  label,
  value,
  tone: t,
  icon,
}: {
  label?: ReactNode
  value?: ReactNode
  tone?: string
  icon?: string
}) {
  return (
    <div className="flex min-w-0 items-center gap-2">
      {icon && <Icon name={icon} className="h-3.5 w-3.5 shrink-0 text-ink-mute" />}
      <span className="min-w-0 flex-1 truncate text-sm text-ink">{label}</span>
      {value !== undefined && value !== null && (
        <span className={`shrink-0 font-mono text-[0.6875rem] ${TEXT[tone(t)]}`}>{value}</span>
      )}
    </div>
  )
}

export function Progress({
  value = 0,
  max = 100,
  tone: t = 'accent',
  label,
}: {
  value?: number
  max?: number
  tone?: string
  label?: ReactNode
}) {
  const pct = max > 0 ? Math.max(0, Math.min(100, (value / max) * 100)) : 0
  return (
    <div className="min-w-0">
      {label !== undefined && label !== null && (
        <div className="mb-1 flex items-baseline justify-between gap-2">
          <span className="truncate text-xs text-ink-soft">{label}</span>
          <span className="shrink-0 font-mono text-[0.6875rem] text-ink-mute">{Math.round(pct)}%</span>
        </div>
      )}
      <div className="h-1.5 w-full overflow-hidden rounded-full bg-surface-mid">
        <div className={`h-full rounded-full transition-all ${FILL[tone(t)]}`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

/** A series, as bars. For a forecast, a week of numbers, anything with a shape worth seeing. */
export function Bars({ values = [], tone: t = 'accent' }: { values?: number[]; tone?: string }) {
  const nums = (values ?? []).filter((v) => typeof v === 'number' && isFinite(v))
  if (nums.length === 0) return null
  const lo = Math.min(...nums, 0)
  const hi = Math.max(...nums, lo + 1)
  return (
    <div className="flex h-8 items-end gap-[3px]">
      {nums.slice(0, 24).map((v, i) => (
        <div
          key={i}
          className={`min-w-[3px] flex-1 rounded-sm ${FILL[tone(t)]}`}
          // A floor of 8%, so a zero is still a visible bar rather than a gap that reads as missing data.
          style={{ height: `${8 + ((v - lo) / (hi - lo)) * 92}%` }}
        />
      ))}
    </div>
  )
}

export function Badge({ children, tone: t }: { children?: ReactNode; tone?: string }) {
  return (
    <span
      className={`shrink-0 whitespace-nowrap rounded-full px-2 py-0.5 text-[0.6875rem] font-medium ${SOFT[tone(t)]}`}
    >
      {children}
    </span>
  )
}

export function Dot({ tone: t }: { tone?: string }) {
  return <span className={`h-1.5 w-1.5 shrink-0 rounded-full ${FILL[tone(t)]}`} />
}

export function Meta({ children }: { children?: ReactNode }) {
  return <div className="truncate text-[0.6875rem] text-ink-mute">{children}</div>
}

/**
 * The icons a panel can use, by name.
 *
 * A closed set, because a generated `<svg>` full of hand-written path data is the one thing guaranteed to look
 * wrong. All on a 24-box, all stroked at the same weight, so any two sit together.
 */
const PATHS: Record<string, string> = {
  plane: 'M2 13l20-7-7 20-3-8-10-5z',
  clock: 'M12 7v5l3 2M21 12a9 9 0 11-18 0 9 9 0 0118 0z',
  sun: 'M12 4V2m0 20v-2m8-8h2M2 12h2m13.7-5.7l1.4-1.4M4.9 19.1l1.4-1.4m11.4 0l1.4 1.4M4.9 4.9l1.4 1.4M16 12a4 4 0 11-8 0 4 4 0 018 0z',
  cloud: 'M6 18h11a4 4 0 000-8 6 6 0 00-11.6 2A3.5 3.5 0 006 18z',
  rain: 'M7 15h10a4 4 0 000-8 6 6 0 00-11 1.5A3.5 3.5 0 007 15zm1 3l-1 3m5-3l-1 3m5-3l-1 3',
  check: 'M4 12.5l5 5L20 6.5',
  alert: 'M12 9v4m0 3h.01M10.3 3.9L2.4 17.2A2 2 0 004.1 20h15.8a2 2 0 001.7-2.8L13.7 3.9a2 2 0 00-3.4 0z',
  arrow: 'M5 12h14m-5-6l6 6-6 6',
  train: 'M8 20l-2 2m10-2l2 2M5 15h14M7 4h10a2 2 0 012 2v7a2 2 0 01-2 2H7a2 2 0 01-2-2V6a2 2 0 012-2z',
  pin: 'M12 21s7-6.3 7-11a7 7 0 10-14 0c0 4.7 7 11 7 11zm0-8.5a2.5 2.5 0 110-5 2.5 2.5 0 010 5z',
  money: 'M12 6v12m3-9a3 3 0 00-3-2.5c-1.7 0-3 1-3 2.3s1.3 2 3 2.2 3 .9 3 2.2-1.3 2.3-3 2.3A3 3 0 019 15',
  chart: 'M4 20V10m5 10V4m5 16v-7m5 7V8',
  box: 'M3 8l9-5 9 5v8l-9 5-9-5V8zm9-5v10m0 0l9-5m-9 5L3 8',
  bell: 'M15 19a3 3 0 01-6 0m9-3H6l1.4-2.1A3 3 0 008 12.2V10a4 4 0 118 0v2.2a3 3 0 00.6 1.7L18 16z',
  // Added because a panel can only be as visual as its vocabulary. Every one of these replaces a word a row would
  // otherwise have to spend width on.
  drop: 'M12 3s5.5 6.2 5.5 10a5.5 5.5 0 11-11 0C6.5 9.2 12 3 12 3z',
  wind: 'M3 8h9a3 3 0 100-6M3 16h13a3 3 0 110 6M3 12h18',
  star: 'M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8-5.2-2.8-5.2 2.8 1-5.8L3.5 9.7l5.9-.9z',
  heart: 'M12 20s-7-4.4-7-9.5A4 4 0 0112 8a4 4 0 017 2.5C19 15.6 12 20 12 20z',
  tag: 'M3 12.5V4h8.5L21 13.5 13.5 21 3 12.5zm4-5.5h.01',
  calendar: 'M7 3v3m10-3v3M4 9h16M6 6h12a2 2 0 012 2v11a2 2 0 01-2 2H6a2 2 0 01-2-2V8a2 2 0 012-2z',
  mail: 'M4 6h16a1 1 0 011 1v10a1 1 0 01-1 1H4a1 1 0 01-1-1V7a1 1 0 011-1zm0 1l8 6 8-6',
  music: 'M9 18V6l10-2v12M9 18a2.5 2.5 0 11-5 0 2.5 2.5 0 015 0zm10-2a2.5 2.5 0 11-5 0 2.5 2.5 0 015 0z',
  film: 'M4 5h16a1 1 0 011 1v12a1 1 0 01-1 1H4a1 1 0 01-1-1V6a1 1 0 011-1zm3 0v14m10-14v14M3 12h18',
  cart: 'M3 4h2l2.5 10h10L20 7H6m2 12a1 1 0 102 0 1 1 0 00-2 0zm8 0a1 1 0 102 0 1 1 0 00-2 0z',
  flame: 'M12 21c3.3 0 6-2.4 6-5.6 0-4.3-6-12.4-6-12.4S6 11.1 6 15.4C6 18.6 8.7 21 12 21zm0-3a2 2 0 01-2-2c0-1.4 2-4 2-4s2 2.6 2 4a2 2 0 01-2 2z',
  moon: 'M20 14.5A8.5 8.5 0 019.5 4a8.5 8.5 0 1010.5 10.5z',
  wifi: 'M5 12.5a10 10 0 0114 0M8 16a6 6 0 018 0m-4 3.5h.01',
  battery: 'M4 8h13a1 1 0 011 1v6a1 1 0 01-1 1H4a1 1 0 01-1-1V9a1 1 0 011-1zm17 2v4M6 11v2',
  home: 'M4 11l8-7 8 7v8a1 1 0 01-1 1h-4v-6H9v6H5a1 1 0 01-1-1v-8z',
  person: 'M12 11a3.5 3.5 0 100-7 3.5 3.5 0 000 7zm-7 9a7 7 0 0114 0',
}

export function Icon({ name, className }: { name?: string; className?: string }) {
  const d = PATHS[(name ?? '').toLowerCase()]
  if (!d) return null
  return (
    <svg
      viewBox="0 0 24 24"
      className={className ?? 'h-4 w-4'}
      fill="none"
      stroke="currentColor"
      strokeWidth="1.75"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
    >
      <path d={d} />
    </svg>
  )
}

/** Exactly what a generated component gets in scope. Anything not in here does not exist to it. */
/**
 * A series as a line, for a panel that remembers.
 *
 * A line rather than bars because a history is usually two or three dozen readings and bars at that count are a grey
 * smear. Drawn as an inline SVG path: it scales to whatever box it is in, needs no library, and a panel is not
 * allowed to load one anyway.
 */
export function Sparkline({
  values = [],
  tone: t = 'accent',
}: {
  values?: number[]
  tone?: string
}) {
  const nums = (values ?? []).filter((v) => typeof v === 'number' && isFinite(v))
  if (nums.length < 2) return null

  const lo = Math.min(...nums)
  const hi = Math.max(...nums)
  // A flat series is a real answer — "unchanged for a week" — so it draws down the middle rather than dividing by nil.
  const span = hi - lo || 1
  const points = nums.map((v, i) => {
    const x = (i / (nums.length - 1)) * 100
    const y = 100 - ((v - lo) / span) * 100
    return `${x.toFixed(2)},${y.toFixed(2)}`
  })

  return (
    <svg viewBox="0 0 100 100" preserveAspectRatio="none" className="h-8 w-full" aria-hidden>
      <polyline
        points={points.join(' ')}
        fill="none"
        className={STROKE[tone(t)]}
        strokeWidth="3"
        strokeLinecap="round"
        strokeLinejoin="round"
        vectorEffect="non-scaling-stroke"
      />
    </svg>
  )
}

/**
 * What changed, with its direction.
 *
 * <p>
 * Colour comes from the sign unless a tone is given, because "up" is good for views and bad for a price you are
 * paying — the panel knows which, and the primitive shouldn't guess. Renders nothing at all when there is no change
 * to report, so a component can hand it a history that doesn't exist yet without checking first.
 * </p>
 */
export function Trend({
  change,
  percent,
  tone: t,
  suffix,
}: {
  change?: number
  percent?: number | null
  tone?: string
  suffix?: string
}) {
  if (typeof change !== 'number' || !isFinite(change)) return null

  const flat = Math.abs(change) < 0.0001
  const shown = t ?? (flat ? 'neutral' : change > 0 ? 'good' : 'bad')
  const arrow = flat ? '→' : change > 0 ? '↑' : '↓'
  const size = Math.abs(change)
  const figure = size >= 100 ? size.toFixed(0) : size >= 1 ? size.toFixed(size % 1 ? 1 : 0) : size.toFixed(2)

  return (
    <span className={`inline-flex items-baseline gap-1 text-xs font-medium ${TEXT[tone(shown)]}`}>
      <span>{arrow}</span>
      <span>
        {flat ? 'flat' : figure}
        {!flat && suffix ? suffix : ''}
      </span>
      {!flat && typeof percent === 'number' && isFinite(percent) && (
        <span className="text-ink-mute">({percent > 0 ? '+' : ''}{percent.toFixed(percent % 1 ? 1 : 0)}%)</span>
      )}
    </span>
  )
}

/**
 * The whole KPI box: one figure, its label, and the trend beside it.
 *
 * <p>
 * Exists so the smallest panel has one obvious right answer. A 2×1 box holds a number and a label and nothing else,
 * and every panel that tried to hold more than that ended up clipped — so the correct layout is provided rather than
 * described, and a component that uses this cannot get the proportions wrong.
 * </p>
 */
export function Kpi({
  label,
  value,
  unit,
  change,
  percent,
  tone: t,
  suffix,
}: {
  label?: ReactNode
  value?: ReactNode
  unit?: string
  change?: number
  percent?: number | null
  tone?: string
  suffix?: string
}) {
  return (
    <div className="flex h-full min-w-0 flex-col justify-center gap-1">
      <Stat label={label} value={value} unit={unit} tone={t} />
      <Trend change={change} percent={percent} suffix={suffix} />
    </div>
  )
}

/**
 * A link out of a panel.
 *
 * <p>
 * Panels were read-only for no reason anybody chose. Nothing ever forbade an anchor — the runtime returns JSX and an
 * anchor is JSX — but the brief never mentioned links and the kit had none, so no generated component ever wrote one.
 * The result was a page full of things you would obviously want to click: an eBay listing you cannot open, a news
 * headline that is only a headline, a flight with no way through to the flight.
 * </p>
 * <p>
 * Opens in a new tab, because a panel is a glance and navigating the whole app away from it is not what anyone wanted.
 * <c>rel</c> is set for the usual reason — a new tab must not be handed a reference back to this window.
 * </p>
 */
export function Link({
  href,
  children,
  tone: t,
  title,
  fill = false,
}: {
  href?: string
  children?: ReactNode
  tone?: string
  title?: string
  /**
   * The link IS the thing rather than words inside it — a picture, a whole row, a tile.
   *
   * An anchor is inline, and a percentage height inside an inline box has no containing block to be a percentage
   * of: wrap a picture that fills its box in a plain Link and the box collapses to nothing, silently, in the one
   * case where the whole point was the picture's size. So a link around something that fills becomes a block that
   * fills.
   */
  fill?: boolean
}) {
  // No href is not an error worth breaking a panel over: sources are missing sometimes, and the text still belongs
  // on screen.
  if (!href) return <>{children}</>

  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      title={title}
      className={`${
        fill ? 'block h-full min-h-0' : 'underline decoration-transparent underline-offset-2 hover:decoration-current'
      } transition ${t ? TEXT[tone(t)] : ''}`}
    >
      {children}
    </a>
  )
}

/**
 * Rows that arrive one after another rather than all at once.
 *
 * The difference between a list that looks pasted in and one that looks considered, and it is not something a
 * generated component should have to hand-roll — the delay has to be per child, which means either a wrapper like this
 * or an inline style repeated at every call site. Wrap the children and they rise in order.
 */
export function Stagger({ children, step = 45 }: { children?: ReactNode; step?: number }) {
  const items = React.Children.toArray(children)
  return (
    <>
      {items.map((child, i) => (
        // Capped, because a panel with fifteen rows should still be finished arriving before you have read the first.
        <div key={i} className="animate-rise" style={{ animationDelay: `${Math.min(i, 10) * step}ms` }}>
          {child}
        </div>
      ))}
    </>
  )
}

/**
 * One figure, as the whole point of the panel.
 *
 * The difference between this and {@link Stat} is intent: a Stat is a labelled number in a row of things, a Hero is
 * the reason the panel exists. It gets the icon, the size and the trend, and the sparkline sits under it so the number
 * carries its own history — which is the thing a panel can do that a glance at a website cannot.
 */
export function Hero({
  label,
  value,
  unit,
  icon,
  tone: t,
  change,
  percent,
  series,
}: {
  label?: ReactNode
  value?: ReactNode
  unit?: string
  icon?: string
  tone?: string
  change?: number
  percent?: number | null
  series?: number[]
}) {
  const text = String(value ?? '—')
  const size = text.length > 9 ? 'text-2xl' : text.length > 6 ? 'text-3xl' : 'text-[2.5rem]'

  return (
    <div className="flex h-full min-w-0 flex-col justify-center">
      {(icon || label) && (
        <div className="mb-1 flex items-center gap-1.5 text-ink-mute">
          {icon && <Icon name={icon} className="h-3.5 w-3.5" />}
          {label && <span className="truncate text-[0.6875rem] font-medium uppercase tracking-wider">{label}</span>}
        </div>
      )}
      <div className="flex items-baseline gap-1">
        <span className={`font-semibold leading-none tracking-tight ${size} ${TEXT[tone(t)]}`}>{text}</span>
        {unit && <span className="text-sm text-ink-mute">{unit}</span>}
      </div>
      <div className="mt-1.5">
        <Trend change={change} percent={percent} />
      </div>
      {series && series.length > 1 && (
        <div className="-mx-1 mt-auto opacity-70">
          <Sparkline values={series} tone={t} />
        </div>
      )}
    </div>
  )
}

/**
 * A proportion as a circle rather than a bar.
 *
 * A ring reads as a fraction at a glance in a way a horizontal bar does not, and it fits a square box, which is what
 * most of these panels are. Drawn with a stroked circle and a dash offset — no library, and it scales with the box.
 */
export function Ring({
  value = 0,
  max = 100,
  label,
  caption,
  tone: t = 'accent',
}: {
  value?: number
  max?: number
  label?: ReactNode
  caption?: ReactNode
  tone?: string
}) {
  const fraction = Math.max(0, Math.min(1, max > 0 ? value / max : 0))
  // 2πr for r=15.9, the radius that makes the circumference 100 — so the dash array is a percentage directly.
  const circumference = 100

  return (
    <div className="relative flex h-full min-h-0 items-center justify-center">
      <svg viewBox="0 0 36 36" className="h-full max-h-[7rem] w-auto -rotate-90" aria-hidden>
        <circle cx="18" cy="18" r="15.9" fill="none" className="stroke-line" strokeWidth="3" />
        <circle
          cx="18"
          cy="18"
          r="15.9"
          fill="none"
          className={STROKE[tone(t)]}
          strokeWidth="3"
          strokeLinecap="round"
          strokeDasharray={`${fraction * circumference} ${circumference}`}
        />
      </svg>
      <div className="absolute inset-0 flex flex-col items-center justify-center">
        {label && <div className="text-lg font-semibold leading-none tracking-tight text-ink">{label}</div>}
        {caption && <div className="mt-0.5 text-[0.625rem] text-ink-mute">{caption}</div>}
      </div>
    </div>
  )
}

/**
 * A picture with words over it.
 *
 * Panels about a thing that HAS a picture — a listing, a record, a place — were rendering the picture as a 32px
 * thumbnail beside a headline, which wastes the most recognisable thing they have. This makes the image the panel and
 * puts the text on top of it.
 */
export function Tile({
  src,
  title,
  caption,
  href,
}: {
  src?: string
  title?: ReactNode
  caption?: ReactNode
  href?: string
}) {
  const body = (
    <div className="relative h-full min-h-0 w-full overflow-hidden rounded-lg bg-surface-low">
      {src && <img src={src} alt="" className="h-full w-full object-cover" />}
      {(title || caption) && (
        <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-ink/80 to-transparent px-2.5 pb-2 pt-6">
          {title && <div className="line-clamp-2 text-xs font-medium leading-tight text-white">{title}</div>}
          {caption && <div className="mt-0.5 text-[0.625rem] text-white/75">{caption}</div>}
        </div>
      )}
    </div>
  )
  return href ? <Link href={href} fill>{body}</Link> : body
}

/** Someone, as a circle. Initials when there is no photo, because a grey blank is worse than two letters. */
export function Avatar({ src, name, size = 'md' }: { src?: string; name?: string; size?: 'sm' | 'md' }) {
  const box = size === 'sm' ? 'h-6 w-6 text-[0.5625rem]' : 'h-8 w-8 text-[0.6875rem]'
  const initials = (name ?? '')
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((w) => w[0]?.toUpperCase())
    .join('')

  if (src) return <img src={src} alt={name ?? ''} className={`${box} shrink-0 rounded-full object-cover`} />
  return (
    <div className={`${box} flex shrink-0 items-center justify-center rounded-full bg-accent-soft font-semibold text-accent`}>
      {initials || '·'}
    </div>
  )
}

/** A wrapping row of small pills, for a handful of short things that are not a list. */
export function Chips({ items = [], tone: t }: { items?: (string | number)[]; tone?: string }) {
  if (items.length === 0) return null
  return (
    <div className="flex flex-wrap gap-1.5">
      {items.map((item, i) => (
        <Badge key={i} tone={t}>
          {item}
        </Badge>
      ))}
    </div>
  )
}

/**
 * The panel's own top line: what it is, and one figure or state about it.
 *
 * The grid deliberately has no title bar — a panel is a component written for its own subject and says what it is.
 * Which is true right up until the thing it says its name with is a field the loader stopped finding, and then the
 * panel has no words on it at all. That is not a hypothetical: an eBay panel whose scrape lost the listing titles
 * rendered four rows of price and thumbnail and nothing else. So the heading is a component with the title passed
 * in, not a line of markup improvised per panel.
 */
export function Header({
  title,
  icon,
  badge,
  tone: t,
  href,
}: {
  title?: ReactNode
  icon?: string
  badge?: ReactNode
  tone?: string
  href?: string
}) {
  return (
    <div className="flex min-w-0 items-center gap-2">
      {icon && <Icon name={icon} className="h-4 w-4 shrink-0 text-ink-mute" />}
      <span className="min-w-0 flex-1 truncate text-sm font-medium text-ink">
        {href ? <Link href={href}>{title}</Link> : title}
      </span>
      {badge !== undefined && badge !== null && badge !== '' && <Badge tone={t}>{badge}</Badge>}
    </div>
  )
}

/**
 * The whole panel: a heading, a body that takes whatever height is left, and a footnote.
 *
 * <p>
 * This exists because of what every generated panel wrote instead. `flex h-full flex-col justify-between` spaces
 * three children out to the edges of the box — which is fine for three lines of text and wrong for anything that
 * should GROW: the middle child gets its content's height and the leftover goes into the gaps. A picture grid laid
 * out that way has no height at all, so the component picks one, and the only picture heights the palette had were
 * icon-sized. That is the whole story of a panel of 640px photographs rendered 32px tall.
 * </p>
 * <p>
 * Here the body is `min-h-0 flex-1`, so a child asking for `h-full` gets the real remaining height and a picture is
 * as big as the box can afford. The component never has to know how tall its box is — which it doesn't.
 * </p>
 */
export function Panel({
  title,
  icon,
  badge,
  tone: t,
  footer,
  children,
}: {
  title?: ReactNode
  icon?: string
  badge?: ReactNode
  tone?: string
  footer?: ReactNode
  children?: ReactNode
}) {
  return (
    <div className="flex h-full min-h-0 flex-col gap-2">
      {(title || icon || badge) && <Header title={title} icon={icon} badge={badge} tone={t} />}
      <div className="min-h-0 flex-1">{children}</div>
      {footer !== undefined && footer !== null && footer !== '' && <Meta>{footer}</Meta>}
    </div>
  )
}

/** Rows, spaced the same in every panel, arriving in order. What a list of things is made of. */
export function List({ children }: { children?: ReactNode }) {
  return (
    <div className="flex min-h-0 flex-col gap-1.5">
      <Stagger>{children}</Stagger>
    </div>
  )
}

/**
 * One thing in a list, with its picture.
 *
 * <p>
 * The row a listing, a record, a fixture or a headline actually wants: an image big enough to recognise, the name,
 * a line of detail under it, and the figure that matters on the right. Hand-rolled, this is the row that goes wrong
 * most often — the image ends up icon-sized, the title has no `min-w-0` above it and pushes the price off the edge,
 * and the two lines of text are set at whatever sizes came to mind.
 * </p>
 * <p>
 * An absent title shows a dash rather than nothing. A row with no words in it looks like a design decision; a dash
 * looks like the missing value it is, and that is the difference between noticing and not.
 * </p>
 */
export function Thing({
  image,
  icon,
  title,
  meta,
  value,
  badge,
  tone: t,
  href,
}: {
  image?: string
  icon?: string
  title?: ReactNode
  meta?: ReactNode
  value?: ReactNode
  badge?: ReactNode
  tone?: string
  href?: string
}) {
  const body = (
    <div className="flex min-w-0 items-center gap-2.5 rounded-lg bg-surface-low px-2 py-1.5">
      {image ? (
        <img src={image} alt="" className="h-11 w-11 shrink-0 rounded-md bg-surface-mid object-cover" />
      ) : icon ? (
        <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-md bg-surface-mid text-ink-mute">
          <Icon name={icon} className="h-4 w-4" />
        </div>
      ) : null}
      <div className="min-w-0 flex-1">
        <div className="truncate text-xs font-medium leading-tight text-ink">{title || '—'}</div>
        {meta !== undefined && meta !== null && meta !== '' && (
          <div className="mt-0.5 truncate text-[0.6875rem] leading-tight text-ink-mute">{meta}</div>
        )}
      </div>
      {badge !== undefined && badge !== null && badge !== '' && <Badge tone={t}>{badge}</Badge>}
      {value !== undefined && value !== null && value !== '' && (
        <span className={`shrink-0 text-sm font-semibold tabular-nums ${TEXT[tone(t)]}`}>{value}</span>
      )}
    </div>
  )
  // The row is the link, so it gets the block treatment: an underline dragged across a title, a caption and a price
  // is not what "this opens the listing" should look like.
  return href ? (
    <Link href={href} fill>
      {body}
    </Link>
  ) : (
    body
  )
}

/**
 * Several pictures AS the panel.
 *
 * <p>
 * The counterpart to {@link Tile}, which is one picture. A profile, a gallery, a match-day feed is a handful of
 * them, and the interesting property is that none of the sizing is a decision the component gets to make: the grid
 * takes the box's whole height, the rows share it, and each picture covers its cell. Give it four photographs in a
 * box 200px tall and you get four 100px photographs; give it the same four in a KPI box and they shrink to fit.
 * Nothing in the panel says a number of pixels, so nothing in the panel can say the wrong one.
 * </p>
 * <p>
 * `lead` is for a feed with a newest one: the first picture takes a 2×2 block and the rest fill in beside it. It
 * needs three or more to be a mosaic rather than a lopsided pair, so below that it is ignored.
 * </p>
 */
export function Pictures({
  items = [],
  cols,
  lead = false,
}: {
  items?: (string | { src?: string; href?: string; caption?: string })[]
  cols?: number
  lead?: boolean
}) {
  const pics = (items ?? [])
    .map((item) => (typeof item === 'string' ? { src: item } : item))
    .filter((p): p is { src: string; href?: string; caption?: string } => !!p?.src)

  if (pics.length === 0) return null

  const mosaic = lead && pics.length >= 3
  const shown = pics.slice(0, mosaic ? 5 : 6)
  // Columns from the count, so three pictures are a row and four are a square rather than a row of three and an
  // orphan. A number can still be given when the panel knows better than the count does.
  const columns = mosaic ? 4 : (cols ?? (shown.length <= 3 ? shown.length : shown.length === 4 ? 2 : 3))

  const COLS: Record<number, string> = {
    1: 'grid-cols-1',
    2: 'grid-cols-2',
    3: 'grid-cols-3',
    4: 'grid-cols-4',
  }

  return (
    <div className={`grid h-full min-h-0 auto-rows-fr gap-1 ${COLS[columns] ?? 'grid-cols-3'}`}>
      {shown.map((p, i) => {
        const cell = (
          <div className="relative h-full min-h-0 overflow-hidden rounded-md bg-surface-low">
            <img src={p.src} alt={p.caption ?? ''} className="h-full w-full object-cover" />
          </div>
        )
        return (
          <div key={i} className={mosaic && i === 0 ? 'col-span-2 row-span-2 min-h-0' : 'min-h-0'}>
            {p.href ? (
              <Link href={p.href} fill>
                {cell}
              </Link>
            ) : (
              cell
            )}
          </div>
        )
      })}
    </div>
  )
}

/**
 * Something to press.
 *
 * Panels have been read-only since the first one, and not by decision — the kit simply had no control in it, and a
 * generated component reaches for what the brief lists and nothing else. So every panel that wanted a second view
 * of its data showed one view, and every panel that wanted to be acted on was a picture of the thing instead.
 *
 * What it does is entirely the component's business: `onPress` is an ordinary handler, so `useState` gives you a
 * slideshow, a tab, a "show the other four" — anything whose consequence lives inside the panel. Reaching OUT of
 * the panel is a different matter and is deliberately not possible from here.
 */
export function Button({
  label,
  onPress,
  tone: t,
  icon,
  active = false,
}: {
  label?: ReactNode
  onPress?: () => void
  tone?: string
  icon?: string
  active?: boolean
}) {
  return (
    <button
      type="button"
      onClick={onPress}
      className={
        'inline-flex shrink-0 items-center gap-1 rounded-full px-2 py-0.5 text-[0.6875rem] font-medium ' +
        'transition-opacity hover:opacity-80 ' +
        (active ? FILL[tone(t)] + ' text-on-accent' : SOFT[tone(t)])
      }
    >
      {icon ? <Icon name={icon} className="h-3 w-3" /> : null}
      {label}
    </button>
  )
}

/**
 * What a panel can reach outside itself: run one of an installed plugin's commands, and ask for fresh data
 * once it has.
 *
 * A context rather than a prop, so <Action> can be dropped anywhere in a panel's tree without every component
 * between here and there having to pass a handler down. The runtime provides it; the panel never sees it.
 */
export const PanelActions = React.createContext<{
  act: (
    plugin: string,
    command: string,
    parameters?: Record<string, string>,
  ) => Promise<{ ok: boolean; text: string; data?: unknown }>
  refresh: () => void
} | null>(null)

/**
 * A button that DOES something: runs a plugin command, and reloads the panel when it worked.
 *
 * The busy state, the failure message and the refresh all live here rather than in the panel, for the same
 * reason the slideshow's timer does — every panel would otherwise hand-roll the same three pieces of state and
 * get one of them wrong. A panel that wants the raw wire has `act` in its scope; this is the shape it takes
 * nine times out of ten.
 *
 * It reports what came back when a command fails. A button that silently does nothing is a button nobody
 * presses twice.
 */
export function Action({
  label,
  plugin,
  command,
  parameters,
  icon,
  tone: t,
  disabled = false,
}: {
  label?: ReactNode
  /** Which plugin, by its id — "roborock". */
  plugin?: string
  /** Which of its commands — "clean", "stop", "dock". */
  command?: string
  parameters?: Record<string, string>
  icon?: string
  tone?: string
  disabled?: boolean
}) {
  const wired = React.useContext(PanelActions)
  const [busy, setBusy] = React.useState(false)
  const [problem, setProblem] = React.useState<string | null>(null)

  const press = async () => {
    if (busy || disabled || !wired || !plugin || !command) return
    setBusy(true)
    setProblem(null)
    const result = await wired.act(plugin, command, parameters)
    setBusy(false)
    if (result?.ok) wired.refresh()
    else setProblem(result?.text ?? 'that did not work')
  }

  return (
    <span className="inline-flex min-w-0 items-center gap-1">
      <Button label={busy ? '\u2026' : label} onPress={press} tone={t} icon={icon} />
      {problem ? <Meta>{problem}</Meta> : null}
    </span>
  )
}

/**
 * Several pictures, one at a time, moving on by itself.
 *
 * The timer lives HERE rather than in the panel, and that is the whole reason this exists as a kit component. A
 * generated component may not call setInterval — a panel that polls for itself is a panel that keeps running after
 * it is off screen, and the publish contract refuses one. But "show me the last six posts" is a slideshow, and
 * asking for it used to produce a grid of thumbnails because a slideshow was unbuildable. So the kit owns the
 * interval, cleans it up on unmount, and the panel just hands over the pictures.
 *
 * Paused while the pointer is on it, so a caption someone is reading does not slide away mid-sentence.
 */
export function Slides({
  items = [],
  every = 5,
  caption = true,
}: {
  items?: (string | { src?: string; href?: string; caption?: string })[]
  every?: number
  caption?: boolean
}) {
  const pics = (items ?? [])
    .map((item) => (typeof item === 'string' ? { src: item } : item))
    .filter((p): p is { src: string; href?: string; caption?: string } => !!p?.src)

  const [at, setAt] = React.useState(0)
  const [held, setHeld] = React.useState(false)

  // Seconds, clamped: a one-second slideshow is a strobe, and a generated number is not to be trusted with that.
  const wait = Math.max(2, Math.min(60, every || 5)) * 1000

  React.useEffect(() => {
    if (held || pics.length < 2) return
    const timer = window.setInterval(() => setAt((i) => (i + 1) % pics.length), wait)
    return () => window.clearInterval(timer)
  }, [held, pics.length, wait])

  // The list can get shorter under it on a refresh, and an index past the end renders nothing at all — which looks
  // exactly like a broken panel and is the sort of thing that only happens hours later.
  const shown = pics[Math.min(at, pics.length - 1)]
  if (!shown) return null

  const picture = (
    <div className="relative h-full min-h-0 w-full overflow-hidden rounded-md bg-surface-low">
      <img src={shown.src} alt={shown.caption ?? ''} className="h-full w-full object-cover" />
      {caption && shown.caption ? (
        <div className="absolute inset-x-0 bottom-0 truncate bg-black/45 px-1.5 py-0.5 text-[0.625rem] text-white">
          {shown.caption}
        </div>
      ) : null}
      {pics.length > 1 ? (
        <div className="absolute inset-x-0 bottom-0 flex justify-center gap-1 pb-1">
          {pics.map((_, i) => (
            <span
              key={i}
              className={`h-1 w-1 rounded-full ${i === (at % pics.length) ? 'bg-white' : 'bg-white/40'}`}
            />
          ))}
        </div>
      ) : null}
    </div>
  )

  return (
    <div
      className="h-full min-h-0 w-full"
      onMouseEnter={() => setHeld(true)}
      onMouseLeave={() => setHeld(false)}
    >
      {shown.href ? (
        <Link href={shown.href} fill>
          {picture}
        </Link>
      ) : (
        picture
      )}
    </div>
  )
}

export const KIT = {
  Stat, Row, Progress, Bars, Badge, Dot, Meta, Icon, Sparkline, Trend, Kpi, Link, Stagger,
  Hero, Ring, Tile, Avatar, Chips, Header, Panel, List, Thing, Pictures, Button, Action, Slides,
}
