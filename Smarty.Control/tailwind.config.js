import typography from '@tailwindcss/typography'

/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        // "Stillness & Logic" — shared with Smarty.Chat so the command centre feels like the same product.
        bg: '#f7f9fb',
        surface: '#ffffff',
        'surface-low': '#f2f4f6',
        'surface-mid': '#eceef0',
        ink: '#191c1e',
        'ink-soft': '#45464d',
        'ink-mute': '#76777d',
        line: '#e4e6ea',
        accent: '#4f46e5',
        'accent-soft': '#eef2ff',
        'on-accent': '#ffffff',
        danger: '#ba1a1a',
        // Status accents for the command centre.
        live: '#0e9f6e', // working / running
        wait: '#c27803', // waiting on a person
        idle: '#76777d', // quiet
      },
      fontFamily: {
        sans: ['Inter', 'ui-sans-serif', 'system-ui', '-apple-system', 'Segoe UI', 'Roboto', 'sans-serif'],
        mono: ['"Geist Mono"', 'ui-monospace', 'SFMono-Regular', 'Menlo', 'monospace'],
      },
      boxShadow: {
        ambient: '0 10px 30px rgba(15, 23, 42, 0.04)',
        card: '0 1px 2px rgba(15, 23, 42, 0.04), 0 1px 3px rgba(15, 23, 42, 0.03)',
      },
      maxWidth: {
        reading: '720px',
      },
      keyframes: {
        pulse2: { '0%,100%': { opacity: '1' }, '50%': { opacity: '0.35' } },
      },
      animation: {
        pulse2: 'pulse2 1.4s ease-in-out infinite',
      },
    },
  },
  plugins: [typography],
}
