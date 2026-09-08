import { useEffect, useRef, type ReactNode } from 'react'
import { createPortal } from 'react-dom'

/**
 * The dialogue this app didn't have.
 *
 * Every decision so far has been either a menu item that acts instantly or a question typed into a chat box, which
 * means anything needing more than one field had nowhere to live — you could remove a panel in one tap but not tell it
 * what to change. So: one sheet, used for everything that asks something.
 *
 * Built for a phone, because that is where this page is read. It rises from the bottom with the corners rounded and a
 * grab handle, the way a native sheet does, and its primary action is a full-width button pinned to the bottom edge
 * where a thumb already is. On a wider screen the same thing settles into a centred panel rather than becoming a
 * different component — one behaviour to reason about, two shapes.
 *
 * Deliberately portalled to the body. The first version of the panel menu rendered inside its card and was clipped by
 * the card's own overflow, which is a mistake worth making only once.
 */
export function Sheet({
  open,
  title,
  subtitle,
  icon,
  onClose,
  children,
  action,
}: {
  open: boolean
  title: string
  subtitle?: string
  /** A glyph for what this is about. A sheet that opens with a shape reads faster than one that opens with a word. */
  icon?: ReactNode
  onClose: () => void
  children: ReactNode
  /** The one thing this sheet is for. Rendered full width along the bottom. */
  action?: ReactNode
}) {
  const panel = useRef<HTMLDivElement>(null)

  // Escape closes, and the page behind stops scrolling while it is open — on a phone a sheet over a scrolling page
  // reads as broken.
  useEffect(() => {
    if (!open) return

    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)

    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'

    // Focus the first thing worth typing into, so a sheet that exists to take a sentence is ready for one.
    const focusable = panel.current?.querySelector<HTMLElement>('textarea, input, button')
    focusable?.focus()

    return () => {
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = previous
    }
  }, [open, onClose])

  if (!open) return null

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-end justify-center sm:items-center">
      {/* The backdrop closes it, which is the gesture everyone tries first. */}
      <div
        className="absolute inset-0 bg-ink/25 backdrop-blur-sm animate-fade-in"
        onClick={onClose}
        aria-hidden
      />

      <div
        ref={panel}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="animate-sheet-up relative flex max-h-[88vh] w-full flex-col rounded-t-2xl border border-line bg-surface shadow-ambient sm:max-w-lg sm:rounded-2xl"
      >
        {/* The grab handle. Purely a signal that this came from the bottom and can go back there. */}
        <div className="flex justify-center pt-2.5 sm:hidden">
          <div className="h-1 w-9 rounded-full bg-line" />
        </div>

        <div className="flex items-start gap-3 px-5 pb-3 pt-3.5">
          {icon && (
            <div className="animate-rise mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent">
              {icon}
            </div>
          )}
          <div className="animate-rise min-w-0" style={{ animationDelay: '40ms' }}>
            <div className="text-[1.0625rem] font-semibold tracking-tight text-ink">{title}</div>
            {subtitle && <div className="mt-0.5 text-[0.8125rem] leading-snug text-ink-soft">{subtitle}</div>}
          </div>
        </div>

        {/* The body scrolls, the header and the action do not — so the button is always reachable however long the
            content gets. */}
        <div className="min-h-0 flex-1 overflow-y-auto px-5 pb-2">{children}</div>

        {action && (
          <div className="border-t border-line px-5 pb-[max(1.25rem,env(safe-area-inset-bottom))] pt-3.5">{action}</div>
        )}
      </div>
    </div>,
    document.body,
  )
}

/**
 * One labelled section of a sheet, arriving in its turn.
 *
 * The stagger is the point: a sheet whose contents appear all at once is a form, and a sheet that assembles itself in
 * half a second reads as something being prepared for you. The delay is an inline style rather than a class per
 * position, so any number of sections costs nothing.
 */
export function SheetSection({
  label,
  at = 0,
  children,
}: {
  label?: string
  /** Position in the sheet, for the stagger. */
  at?: number
  children: ReactNode
}) {
  return (
    <div className="animate-rise" style={{ animationDelay: `${80 + at * 60}ms` }}>
      {label && (
        <div className="mb-2 text-[0.6875rem] font-medium uppercase tracking-wider text-ink-mute">{label}</div>
      )}
      {children}
    </div>
  )
}

/** The full-width primary action a sheet is built around. */
export function SheetButton({
  children,
  onClick,
  disabled,
  busy,
}: {
  children: ReactNode
  onClick: () => void
  disabled?: boolean
  busy?: boolean
}) {
  return (
    <button
      onClick={onClick}
      disabled={disabled || busy}
      className="w-full rounded-xl bg-accent px-4 py-3 text-[0.9375rem] font-medium text-on-accent transition enabled:hover:brightness-110 disabled:opacity-40"
    >
      {busy ? 'Working…' : children}
    </button>
  )
}

/**
 * The three footprints, as something to tap rather than a word to know.
 *
 * Drawn to scale against each other, because "kpi" and "tall" mean nothing until you can see that one is a quarter of
 * the other. The proportions are the real ones: 2×1, 2×2 and 4×2 grid cells.
 */
export function SizePicker({
  value,
  onChange,
}: {
  value: 'kpi' | 'tall' | 'wide'
  onChange: (size: 'kpi' | 'tall' | 'wide') => void
}) {
  const options: { size: 'kpi' | 'tall' | 'wide'; label: string; hint: string; box: string }[] = [
    { size: 'kpi', label: 'KPI', hint: 'one number', box: 'h-4 w-8' },
    { size: 'tall', label: 'Tall', hint: 'a few rows', box: 'h-8 w-8' },
    { size: 'wide', label: 'Wide', hint: 'a list or chart', box: 'h-8 w-16' },
  ]

  return (
    <div className="flex items-end gap-2.5">
      {options.map((o) => (
        <button
          key={o.size}
          onClick={() => onChange(o.size)}
          className={`flex flex-1 flex-col items-center gap-2 rounded-xl border p-3 transition ${
            value === o.size
              ? 'border-accent bg-accent-soft/60'
              : 'border-line bg-surface hover:bg-surface-low'
          }`}
        >
          <div className={`rounded ${o.box} ${value === o.size ? 'bg-accent' : 'bg-line'}`} />
          <div className="text-center">
            <div className="text-xs font-medium text-ink">{o.label}</div>
            <div className="text-[0.625rem] leading-tight text-ink-mute">{o.hint}</div>
          </div>
        </button>
      ))}
    </div>
  )
}
