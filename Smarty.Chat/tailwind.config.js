import typography from '@tailwindcss/typography'

/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
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
