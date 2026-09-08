import typography from '@tailwindcss/typography'

/** @type {import('tailwindcss').Config} */
/**
 * The classes a runtime-generated panel is allowed to use.
 *
 * Tailwind scans the source at BUILD time and emits only what it finds, so a class that appears for the first time
 * inside a component the system wrote an hour ago is not in the stylesheet at all — it silently does nothing. The
 * first generated flight panel used `tabular-nums` and `border-ink-faint`, and both were absent: the times didn't
 * align and the cards had no border, with nothing anywhere to say why.
 *
 * So the palette is declared. This list and the class list in the builder's brief (Smarty.Api/WidgetTools.cs) are
 * two halves of one contract — a class added to one and not the other is a class that either doesn't work or is
 * never used. Deliberately a small, opinionated set: enough to lay out any panel, not enough to invent a new
 * visual language per panel.
 */
const widgetClasses = [
  // layout
  'flex', 'inline-flex', 'grid', 'hidden', 'block',
  'flex-col', 'flex-row', 'flex-wrap', 'flex-1', 'shrink-0', 'grow',
  'items-start', 'items-center', 'items-end', 'items-baseline',
  'justify-start', 'justify-center', 'justify-end', 'justify-between', 'justify-around',
  'grid-cols-1', 'grid-cols-2', 'grid-cols-3', 'grid-cols-4',
  'col-span-1', 'col-span-2', 'col-span-3', 'col-span-4',
  'grid-rows-1', 'grid-rows-2', 'grid-rows-3', 'auto-rows-fr', 'row-span-1', 'row-span-2',
  'ml-auto', 'mr-auto', 'mt-auto', 'mb-auto', 'self-start', 'self-center', 'self-end',
  'relative', 'absolute', 'inset-0', 'right-0', 'top-0', 'bottom-0', 'left-0',
  'h-full', 'w-full', 'min-w-0', 'min-h-0', 'max-w-full', 'max-h-full', 'overflow-hidden',
  // a stream or an image filling its box
  'object-cover', 'object-contain', 'object-center', 'object-top', 'aspect-video', 'aspect-square',
  // spacing — a fixed step scale, so two panels agree on what "a gap" is
  'gap-0.5', 'gap-1', 'gap-1.5', 'gap-2', 'gap-2.5', 'gap-3', 'gap-4', 'gap-x-2', 'gap-x-3', 'gap-y-1', 'gap-y-2',
  'p-0', 'p-1', 'p-1.5', 'p-2', 'p-3', 'p-3.5', 'px-1', 'px-1.5', 'px-2', 'px-2.5', 'px-3', 'py-0.5', 'py-1', 'py-1.5', 'py-2',
  'mt-0.5', 'mt-1', 'mt-1.5', 'mt-2', 'mt-3', 'mb-0.5', 'mb-1', 'mb-2', 'pt-1', 'pt-2',
  // pl-4 was in the brief and not here, so any panel that indented with it simply didn't. Found by the test that
  // pairs the two lists, which is the only way a fault like this is ever found other than by looking at it.
  'pl-2', 'pl-3', 'pl-4', 'pr-2',
  'space-y-0.5', 'space-y-1', 'space-y-1.5', 'space-y-2', 'space-x-1', 'space-x-2',
  // type
  'text-[0.625rem]', 'text-[0.6875rem]', 'text-xs', 'text-sm', 'text-base', 'text-lg', 'text-xl', 'text-2xl',
  'text-[1.75rem]', 'font-normal', 'font-medium', 'font-semibold', 'font-bold', 'font-mono',
  'leading-none', 'leading-tight', 'leading-relaxed', 'tracking-tight', 'tracking-wide', 'uppercase', 'capitalize',
  'tabular-nums', 'truncate', 'line-clamp-1', 'line-clamp-2', 'line-clamp-3', 'whitespace-nowrap', 'text-left',
  'text-center', 'text-right', 'break-words',
  // The sheet's own chrome, so a generated panel is never the only thing keeping these alive.
  'backdrop-blur-sm', 'rounded-t-2xl', 'resize-none',
  // Staggered arrival, available to a generated panel through the Stagger primitive.
  'animate-rise',
  // What the visual primitives draw with: rings, image tiles and the caption gradient over them.
  'stroke-line', 'rotate-90', '-rotate-90', 'rounded-full', 'bg-gradient-to-t', 'from-ink/80',
  'to-transparent', 'text-white', 'text-white/75', 'flex-wrap', 'max-h-[7rem]',
  // Links: a panel about a thing should open the thing.
  'underline', 'decoration-transparent', 'decoration-current', 'underline-offset-2', 'hover:decoration-current',
  'cursor-pointer',
  // colour — the app's own tokens only, so a panel can't wander off the palette
  'text-ink', 'text-ink-soft', 'text-ink-mute', 'text-accent', 'text-danger', 'text-on-accent',
  'text-emerald-600', 'text-amber-600',
  // Strokes, for the sparkline the kit draws.
  'stroke-emerald-500', 'stroke-amber-500', 'stroke-danger', 'stroke-accent', 'stroke-ink-mute',
  'bg-surface', 'bg-surface-low', 'bg-surface-mid', 'bg-accent', 'bg-accent-soft', 'bg-transparent',
  'bg-emerald-50', 'bg-amber-50', 'bg-red-50',
  'border', 'border-0', 'border-t', 'border-b', 'border-l', 'border-line', 'border-accent', 'border-transparent',
  'rounded', 'rounded-sm', 'rounded-md', 'rounded-lg', 'rounded-xl', 'rounded-full',
  'opacity-50', 'opacity-60', 'opacity-70',
  // Sizes: icons and swatches at the small end, PICTURES at the large end. The large end was missing, and the
  // consequence was not a panel that looked slightly off — it was 640px photographs rendered 32px tall, because h-8
  // was the biggest height a panel was allowed to ask for. A palette that stops at icon size makes every picture an
  // icon, whatever the brief says about it.
  'h-1.5', 'h-2', 'h-3', 'h-3.5', 'h-4', 'h-5', 'h-6', 'h-8', 'h-10', 'h-11', 'h-12', 'h-14', 'h-16', 'h-20', 'h-24',
  'w-1.5', 'w-2', 'w-3', 'w-3.5', 'w-4', 'w-5', 'w-6', 'w-8', 'w-10', 'w-11', 'w-12', 'w-14', 'w-16', 'w-20', 'w-24',
  // one responsive escape hatch: hiding a secondary column on a narrow screen
  'sm:block', 'sm:flex', 'sm:hidden',
]

export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  safelist: widgetClasses,
  theme: {
    extend: {
      colors: {
        // "Stillness & Logic" — near-monochrome, one accent. Separation by light + space, not lines.
        bg: '#f7f9fb', // the page / workspace
        surface: '#ffffff', // cards, raised containers
        'surface-low': '#f2f4f6', // user bubble, auxiliary panels
        'surface-mid': '#eceef0', // hover / pressed
        ink: '#191c1e', // primary text + headlines
        'ink-soft': '#45464d', // secondary text
        'ink-mute': '#76777d', // meta / tertiary
        line: '#e4e6ea', // subtle dividers / borders
        accent: '#4f46e5', // the sole accent — actions, active states, Smarty's identity
        'accent-soft': '#eef2ff', // chip / tint backgrounds
        'on-accent': '#ffffff',
        danger: '#ba1a1a',
      },
      fontFamily: {
        sans: ['Inter', 'ui-sans-serif', 'system-ui', '-apple-system', 'Segoe UI', 'Roboto', 'sans-serif'],
        mono: ['"Geist Mono"', 'ui-monospace', 'SFMono-Regular', 'Menlo', 'monospace'],
      },
      boxShadow: {
        // High-diffusion, low-opacity — depth by ambient shadow, not borders.
        ambient: '0 10px 30px rgba(15, 23, 42, 0.04)',
        card: '0 1px 2px rgba(15, 23, 42, 0.04), 0 1px 3px rgba(15, 23, 42, 0.03)',
      },
      maxWidth: {
        reading: '680px',
      },
    },
  },
  plugins: [typography],
}
