import { ReactNode } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import type { ConvStatus } from './api'

export const cx = (...c: (string | false | null | undefined)[]) => c.filter(Boolean).join(' ')

export function Markdown({ children }: { children: string }) {
  return (
    <div className="md text-[0.92rem] leading-relaxed break-words">
      <ReactMarkdown remarkPlugins={[remarkGfm]}>{children}</ReactMarkdown>
    </div>
  )
}

const STATUS_COLOR: Record<ConvStatus, string> = {
  idle: 'bg-idle',
  thinking: 'bg-accent',
  working: 'bg-live',
  waiting: 'bg-wait',
}
const STATUS_LABEL: Record<ConvStatus, string> = {
  idle: 'Idle',
  thinking: 'Thinking',
  working: 'Working',
  waiting: 'Waiting on you',
}

export function StatusDot({ status, label }: { status: ConvStatus; label?: boolean }) {
  const live = status === 'working' || status === 'thinking'
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className={cx('h-2 w-2 rounded-full', STATUS_COLOR[status], live && 'animate-pulse2')} />
      {label && <span className="text-xs text-ink-mute">{STATUS_LABEL[status]}</span>}
    </span>
  )
}

export function SurfaceBadge({ surface }: { surface: string }) {
  const slack = surface === 'slack'
  return (
    <span
      className={cx(
        'inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[0.68rem] font-medium',
        slack ? 'bg-[#4a154b]/10 text-[#4a154b]' : 'bg-accent-soft text-accent',
      )}
    >
      {slack ? '# Slack' : '◍ Chat'}
    </span>
  )
}

export function Pill({ children, tone = 'neutral' }: { children: ReactNode; tone?: 'neutral' | 'accent' | 'live' | 'wait' | 'danger' }) {
  const tones = {
    neutral: 'bg-surface-low text-ink-soft',
    accent: 'bg-accent-soft text-accent',
    live: 'bg-live/10 text-live',
    wait: 'bg-wait/10 text-wait',
    danger: 'bg-danger/10 text-danger',
  }
  return <span className={cx('inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[0.7rem] font-medium', tones[tone])}>{children}</span>
}

export function Card({ children, className, onClick }: { children: ReactNode; className?: string; onClick?: () => void }) {
  return (
    <div
      onClick={onClick}
      className={cx(
        'rounded-2xl bg-surface shadow-card border border-line/60',
        onClick && 'cursor-pointer transition hover:shadow-ambient active:scale-[0.997]',
        className,
      )}
    >
      {children}
    </div>
  )
}

export function Button({
  children,
  onClick,
  variant = 'primary',
  size = 'md',
  type = 'button',
  disabled,
  className,
}: {
  children: ReactNode
  onClick?: () => void
  variant?: 'primary' | 'ghost' | 'soft' | 'danger'
  size?: 'sm' | 'md'
  type?: 'button' | 'submit'
  disabled?: boolean
  className?: string
}) {
  const variants = {
    primary: 'bg-accent text-on-accent hover:brightness-110',
    ghost: 'text-ink-soft hover:bg-surface-mid',
    soft: 'bg-surface-low text-ink hover:bg-surface-mid',
    danger: 'text-danger hover:bg-danger/10',
  }
  return (
    <button
      type={type}
      onClick={onClick}
      disabled={disabled}
      className={cx(
        'inline-flex items-center justify-center gap-1.5 rounded-xl font-medium transition disabled:opacity-40',
        size === 'sm' ? 'px-2.5 py-1 text-xs' : 'px-3.5 py-2 text-sm',
        variants[variant],
        className,
      )}
    >
      {children}
    </button>
  )
}

export function EmptyState({ title, hint, icon }: { title: string; hint?: string; icon?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 py-16 text-center">
      {icon && <div className="text-3xl opacity-40">{icon}</div>}
      <div className="font-medium text-ink-soft">{title}</div>
      {hint && <div className="max-w-xs text-sm text-ink-mute">{hint}</div>}
    </div>
  )
}

export function Spinner() {
  return (
    <span className="inline-block h-3.5 w-3.5 animate-spin rounded-full border-2 border-ink-mute/30 border-t-accent" />
  )
}

export function SectionTitle({ children, right }: { children: ReactNode; right?: ReactNode }) {
  return (
    <div className="flex items-center justify-between px-1 pb-2 pt-1">
      <h2 className="text-sm font-semibold uppercase tracking-wide text-ink-mute">{children}</h2>
      {right}
    </div>
  )
}
