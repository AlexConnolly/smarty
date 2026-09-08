import { useEffect, useState } from 'react'
import { SpendReport, fetchSpend, money, tokens } from '../api'
import { Card, EmptyState, Pill, SectionTitle, Spinner } from '../ui'

/**
 * What Smarty has cost, and where it went.
 *
 * Split the way providers actually bill: fresh input, CACHED input (about a quarter the price), and output — of
 * which reasoning tokens are called out separately, because a model thinking in the output channel is charged for
 * it whether or not the user ever sees the words. The cache-hit rate is the number that tells you whether the
 * prompt prefix is being reused, which is where an agent loop's savings live.
 */
export function SpendView() {
  const [report, setReport] = useState<SpendReport | null | 'loading'>('loading')

  const load = async () => setReport(await fetchSpend())
  useEffect(() => {
    void load()
    const t = setInterval(() => void load(), 5000)
    return () => clearInterval(t)
  }, [])

  if (report === 'loading')
    return (
      <div className="flex justify-center py-10">
        <Spinner />
      </div>
    )

  if (!report || report.total.runs === 0)
    return (
      <EmptyState
        title="Nothing spent yet"
        hint="Cost is recorded per task as it finishes. Run something and it'll show up here, broken down by model."
      />
    )

  const t = report.total

  return (
    <div className="space-y-5">
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Total" value={t.costKnown ? money(t.cost) : `${money(t.cost)}+`} hint={`${t.runs} task${t.runs === 1 ? '' : 's'}`} />
        <Stat label="Input" value={tokens(t.inputTokens)} hint={`${tokens(t.cachedInputTokens)} from cache`} />
        <Stat label="Output" value={tokens(t.outputTokens)} hint={`${tokens(t.reasoningTokens)} thinking`} />
        <Stat
          label="Cache hits"
          value={`${Math.round(t.cacheHitRate * 100)}%`}
          hint={t.cacheHitRate > 0 ? 'cached input bills ~4× cheaper' : 'no prefix reuse yet'}
        />
      </div>

      {!t.costKnown && (
        <p className="px-1 text-xs text-ink-mute">
          Some models have no price on file, so the total is a floor, not the full figure — shown with a “+”.
        </p>
      )}

      <div className="space-y-3">
        <SectionTitle>By model</SectionTitle>
        {report.byModel.map((m) => (
          <Card key={m.model}>
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="truncate font-mono text-sm">{m.model}</span>
                  {!m.priceKnown && <Pill tone="neutral">no price on file</Pill>}
                </div>
                <p className="mt-1 text-xs text-ink-mute">
                  {m.calls} call{m.calls === 1 ? '' : 's'} across {m.runs} run{m.runs === 1 ? '' : 's'} ·{' '}
                  {tokens(m.inputTokens)} in ({tokens(m.cachedInputTokens)} cached) · {tokens(m.outputTokens)} out
                  {m.reasoningTokens > 0 && <> · {tokens(m.reasoningTokens)} thinking</>}
                </p>
                {m.price && (
                  <p className="mt-1 text-[11px] text-ink-mute">
                    ${m.price.inputPerMillion}/M in · ${m.price.cachedInputPerMillion}/M cached · $
                    {m.price.outputPerMillion}/M out
                  </p>
                )}
              </div>
              <span className="shrink-0 font-mono text-sm">{m.priceKnown ? money(m.cost) : '—'}</span>
            </div>
          </Card>
        ))}
      </div>

      <div className="space-y-3">
        <SectionTitle>Costliest tasks</SectionTitle>
        {report.topRuns.map((r) => (
          <Card key={r.id}>
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm" title={r.task}>
                  {r.task}
                </p>
                <p className="mt-1 text-xs text-ink-mute">
                  {r.surface}
                  {r.persona ? ` · ${r.persona}` : ''} · {r.status} · {tokens(r.inputTokens)} in
                  {r.cachedInputTokens > 0 && <> ({tokens(r.cachedInputTokens)} cached)</>} · {tokens(r.outputTokens)} out
                </p>
                <div className="mt-1 flex flex-wrap gap-1">
                  {r.models.map((m) => (
                    <span key={m.model} className="rounded bg-surface-mid px-1.5 py-0.5 font-mono text-[11px] text-ink-soft">
                      {m.model.split('/').pop()} ×{m.calls}
                    </span>
                  ))}
                </div>
              </div>
              <span className="shrink-0 font-mono text-sm">{money(r.cost)}</span>
            </div>
          </Card>
        ))}
      </div>
    </div>
  )
}

function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <Card>
      <p className="text-xs uppercase tracking-wide text-ink-mute">{label}</p>
      <p className="mt-1 font-mono text-xl">{value}</p>
      {hint && <p className="mt-0.5 text-xs text-ink-mute">{hint}</p>}
    </Card>
  )
}
