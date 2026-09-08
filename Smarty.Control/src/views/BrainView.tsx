import { useEffect, useMemo, useRef, useState } from 'react'
import ForceGraph3D, { type ForceGraph3DInstance } from '3d-force-graph'
import SpriteText from 'three-spritetext'
import {
  BrainEdge,
  BrainNode,
  BrainView as Brain,
  deleteBrainNode,
  fetchBrain,
  settleSame,
  timeAgo,
  wipeBrain,
} from '../api'
import { Button, Card, EmptyState, Pill, Spinner } from '../ui'

/**
 * The memory, as a thing you can fly around.
 *
 * It exists because a graph is the one data structure you cannot read as a list. A hundred edges in a table tells you
 * nothing about shape — which things are hubs, what sits on its own, where two clusters join by a single thread — and
 * shape is the entire reason for storing it this way. Every fault this design can develop is visual: a node forked in
 * two shows up as twin clusters, a value stored as a thing shows up as a leaf nobody points at, and a subject nothing
 * connects to shows up as a dot on the edge of the field.
 */

/// One colour per kind, assigned from the kinds actually present rather than from a fixed table — the vocabulary is
/// open, so a table would leave every coined kind grey and indistinguishable.
const PALETTE = [
  '#60a5fa', '#f472b6', '#34d399', '#fbbf24', '#a78bfa',
  '#22d3ee', '#fb7185', '#4ade80', '#f59e0b', '#c084fc',
]

function colours(kinds: string[]): Record<string, string> {
  const map: Record<string, string> = {}
  kinds.forEach((kind, i) => (map[kind] = PALETTE[i % PALETTE.length]))
  return map
}

type Graph = {
  nodes: (BrainNode & { colour: string })[]
  links: { source: string; target: string; label: string; state: string; because: string | null }[]
}

export function BrainView() {
  const [brain, setBrain] = useState<Brain | null>(null)
  const [loading, setLoading] = useState(true)
  const [picked, setPicked] = useState<BrainNode | null>(null)
  const [showDead, setShowDead] = useState(false)

  /// Deleting is two steps, never one. The graph is the only store here with no undo, so the first click arms and the
  /// second does it — and arming is per node, so it cannot be left armed and fired at whatever gets clicked next.
  const [armed, setArmed] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  /// A wipe asks for the word rather than a second click. A click is muscle memory; typing is not, and this is the one
  /// action with nothing at all behind it.
  const [wiping, setWiping] = useState(false)
  const [phrase, setPhrase] = useState('')

  const mount = useRef<HTMLDivElement | null>(null)
  const graph = useRef<ForceGraph3DInstance | null>(null)
  const framed = useRef(false)

  const load = async () => {
    setBrain(await fetchBrain())
    setLoading(false)
  }

  /// Reframe after a delete, because the layout is now wrong: removing a hub leaves the survivors flung out where its
  /// pull used to hold them, and the camera stays where the old shape was.
  const forget = async (node: BrainNode) => {
    setBusy(true)
    const gone = await deleteBrainNode(node.id)
    setBusy(false)
    setArmed(null)
    if (!gone) return
    setPicked(null)
    framed.current = false
    await load()
  }

  const wipe = async () => {
    setBusy(true)
    const gone = await wipeBrain()
    setBusy(false)
    if (!gone) return
    setWiping(false)
    setPhrase('')
    setPicked(null)
    framed.current = false
    await load()
  }

  useEffect(() => {
    void load()
  }, [])

  const kindColour = useMemo(
    () => colours((brain?.kinds ?? []).map((k) => k.kind)),
    [brain?.kinds],
  )

  /// Only edges with both ends are links. A property has no far end — it is a fact ABOUT one node, so it belongs in the
  /// panel that opens when you click, not as a line to nowhere.
  const shaped = useMemo<Graph>(() => {
    if (!brain) return { nodes: [], links: [] }

    const known = new Set(brain.nodes.map((n) => n.id))
    return {
      nodes: brain.nodes.map((n) => ({ ...n, colour: kindColour[n.kind] ?? '#94a3b8' })),
      links: brain.edges
        .filter((e) => (showDead ? true : e.state === 'active'))
        .filter((e) => e.toId && known.has(e.fromId) && known.has(e.toId))
        .map((e) => ({
          source: e.fromId,
          target: e.toId!,
          label: e.relation,
          state: e.state,
          because: e.because,
        })),
    }
  }, [brain, kindColour, showDead])

  // Nothing is on screen until there is something to draw, so the container this mounts into does not exist during the
  // first render — and an effect that runs once on mount finds a null ref and, having no dependencies, never looks again.
  // That is exactly how this drew a blank white box: the instance was never created at all.
  const ready = !loading && (brain?.nodes.length ?? 0) > 0

  // Built once, then fed. Re-creating it on every change would throw away the layout and the camera, so you would lose
  // your place in the graph every time anything refreshed.
  useEffect(() => {
    if (!ready || !mount.current || graph.current) return

    const instance = new ForceGraph3D(mount.current)
      .backgroundColor('#0b0f17')
      .showNavInfo(false)
      .nodeLabel((n: any) => `${n.name}  ·  ${n.kind}`)
      .nodeColor((n: any) => n.colour)
      // Area by connections, not radius: a hub with ten edges should read as bigger than one with two without
      // swallowing the screen, and the square root is what makes the comparison honest.
      .nodeVal((n: any) => 1 + Math.sqrt(n.connections) * 3)
      .nodeOpacity(0.95)
      // Names on screen, not on hover. Coloured dots tell you the SHAPE of the memory but not what any of it is, and
      // having to hover each one to find out makes the whole thing a guessing game.
      .nodeThreeObjectExtend(true)
      .nodeThreeObject((n: any) => {
        const label = new SpriteText(n.name)
        label.color = '#e2e8f0'
        label.textHeight = 3.5
        // Cleared above the sphere, which is itself sized by how connected the thing is — a fixed offset would sit
        // inside the big ones and float away from the small ones.
        label.position.set(0, 5 + Math.sqrt(n.connections) * 2.2, 0)
        return label
      })
      .linkLabel((l: any) => (l.because ? `${l.label} — ended: ${l.because}` : l.label))
      .linkColor((l: any) => (l.state === 'active' ? '#64748b' : '#be123c'))
      .linkWidth((l: any) => (l.state === 'active' ? 1 : 0.5))
      // Direction, shown as movement. Which way a fact was recorded is the difference between "Rosa is my neighbour"
      // and the reverse, and an undirected line loses it.
      .linkDirectionalParticles(2)
      .linkDirectionalParticleWidth(1.4)
      .linkDirectionalParticleSpeed(0.006)
      .onNodeClick((n: any) => {
        setPicked(n as BrainNode)
        // Fly to it rather than jumping: keeping the surroundings in view is the whole point of looking at a graph.
        const distance = 90
        const ratio = 1 + distance / Math.hypot(n.x || 1, n.y || 1, n.z || 1)
        instance.cameraPosition({ x: (n.x || 0) * ratio, y: (n.y || 0) * ratio, z: (n.z || 0) * ratio }, n, 900)
      })
      .onBackgroundClick(() => setPicked(null))
      // Framed once the layout settles, and once only. A force layout starts as a knot in the middle of nowhere, so
      // without this the whole graph sits as a thumbnail in the centre of a large black rectangle — which is what it
      // did. Only the first time, though: doing it on every settle would yank the camera back while you were flying.
      .onEngineStop(() => {
        if (framed.current) return
        framed.current = true
        // A beat after the engine reports it has settled, not on the instant. It cools while still drifting apart, so
        // fitting immediately frames a graph slightly smaller than the one that ends up on screen — which reads as
        // zoomToFit having done nothing at all.
        window.setTimeout(() => instance.zoomToFit(600, 40), 300)
      })

    // Pushed apart harder than the default. The defaults are tuned for hundreds of nodes; a personal brain has tens, and
    // at that size they huddle into a knot where the connections — the entire point — overlap into a blob.
    instance.d3Force('charge')?.strength(-260)
    instance.d3Force('link')?.distance(70)

    graph.current = instance

    const fit = () => {
      if (!mount.current) return
      instance.width(mount.current.clientWidth).height(mount.current.clientHeight)
    }
    fit()
    window.addEventListener('resize', fit)

    return () => {
      window.removeEventListener('resize', fit)
      instance._destructor?.()
      graph.current = null
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ready])

  // The ONLY place data is set, and it depends on `ready` so it runs in the same commit that created the instance —
  // effects fire in declaration order, so the one above has already built it by the time this runs.
  //
  // Setting it in both places restarted the force layout: the first settle framed a knot that was about to be thrown
  // away, the one-shot flag was spent, and the real layout was never framed at all. Which looked exactly like zoomToFit
  // not working.
  useEffect(() => {
    graph.current?.graphData(shaped as any)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [shaped, ready])

  const facts = useMemo<BrainEdge[]>(() => {
    if (!brain || !picked) return []
    return brain.edges.filter((e) => e.fromId === picked.id || e.toId === picked.id)
  }, [brain, picked])

  if (loading) return <Spinner />

  // Empty, but still with a way back. Before the wipe existed, an empty brain was only ever the state you arrived in,
  // so a bare message was enough; now it is a state you can put yourself into, and one with no refresh in it leaves you
  // reloading the page to see what you just told it.
  if (!brain || brain.nodes.length === 0)
    return (
      <div className="space-y-3">
        <EmptyState title="Nothing in the brain yet" hint="Tell it something and it'll show up here." />
        <div className="flex justify-center">
          <button onClick={() => void load()} className="text-xs text-ink-soft underline hover:text-ink">
            refresh
          </button>
        </div>
      </div>
    )

  const dead = brain.edges.filter((e) => e.state !== 'active').length

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <Pill>{brain.nodes.length} things</Pill>
        <Pill>{shaped.links.length} connections</Pill>
        {brain.kinds
          .filter((k) => k.count > 0)
          .map((k) => (
            <span key={k.kind} className="inline-flex items-center gap-1.5 text-xs text-ink-soft">
              <span className="h-2 w-2 rounded-full" style={{ background: kindColour[k.kind] }} />
              {k.kind} {k.count}
            </span>
          ))}

        <div className="ml-auto flex items-center gap-3">
          {dead > 0 && (
            <label className="flex cursor-pointer items-center gap-2 text-xs text-ink-soft">
              <input type="checkbox" checked={showDead} onChange={(e) => setShowDead(e.target.checked)} />
              show what ended ({dead})
            </label>
          )}
          <button onClick={() => void load()} className="text-xs text-ink-soft underline hover:text-ink">
            refresh
          </button>
          <button onClick={() => { setWiping(true); setPhrase('') }} className="text-xs text-danger underline hover:opacity-80">
            wipe
          </button>
        </div>
      </div>

      {/* The word, not a second click. Typing is deliberate in a way clicking twice is not, and this takes the identity
          with it — so the setup page comes back afterwards, which is worth saying before rather than discovering. */}
      {wiping && (
        <Card className="border-danger/40 bg-danger/5">
          <div className="text-sm font-semibold text-ink">Wipe the whole brain?</div>
          <div className="mt-1 text-xs text-ink-soft">
            {brain.nodes.length} node{brain.nodes.length === 1 ? '' : 's'}, {brain.edges.length} fact
            {brain.edges.length === 1 ? '' : 's'}
            {brain.waiting.length > 0 && `, ${brain.waiting.length} waiting to be read`}
            {brain.context.length > 0 && `, ${brain.context.length} kept file${brain.context.length === 1 ? '' : 's'}`}.
            Everything goes, including who you are — you will be asked again.
          </div>
          <div className="mt-3 flex items-center gap-2">
            <input
              autoFocus
              value={phrase}
              onChange={(e) => setPhrase(e.target.value)}
              placeholder="type wipe"
              className="w-32 rounded-md border border-line bg-surface px-2 py-1.5 text-xs text-ink outline-none focus:border-danger"
            />
            <button
              disabled={phrase.trim().toLowerCase() !== 'wipe' || busy}
              onClick={() => void wipe()}
              className="rounded-md bg-danger px-2.5 py-1.5 text-xs font-medium text-white disabled:opacity-40"
            >
              {busy ? 'Wiping…' : 'Wipe it'}
            </button>
            <button
              onClick={() => { setWiping(false); setPhrase('') }}
              className="rounded-md border border-line px-2.5 py-1.5 text-xs text-ink-soft hover:text-ink"
            >
              Cancel
            </button>
          </div>
        </Card>
      )}

      {/* An explicit height, because a canvas cannot inherit one from a flex parent whose own height is only a
          minimum — it would measure zero and draw nothing at all. */}
      <div className="relative h-[72vh] min-h-[420px] overflow-hidden rounded-xl border border-line">
        <div ref={mount} className="h-full w-full" />

        {picked && (
          <Card className="absolute right-3 top-3 max-h-[calc(100%-1.5rem)] w-80 overflow-auto bg-surface/95 backdrop-blur">
            <div className="flex items-start justify-between gap-2">
              <div>
                <div className="text-sm font-semibold text-ink">{picked.name}</div>
                <div className="text-xs text-ink-mute">
                  {picked.kind} · known {timeAgo(picked.created)}
                </div>
              </div>
              <button onClick={() => { setPicked(null); setArmed(null) }} className="text-ink-mute hover:text-ink">
                ✕
              </button>
            </div>

            {picked.aliases.length > 0 && (
              <div className="mt-2 text-xs text-ink-soft">also: {picked.aliases.join(', ')}</div>
            )}

            <div className="mt-3 space-y-1.5">
              {facts.map((f) => (
                <div
                  key={f.id}
                  className={`rounded-md px-2 py-1.5 text-xs ${
                    f.state === 'active' ? 'bg-surface-low text-ink-soft' : 'bg-surface-low/60 text-ink-mute'
                  }`}
                >
                  <div>
                    {/* Read in the direction it was recorded, whichever end you clicked — the stored direction is the
                        fact, and flipping it to suit the reader would state the opposite. */}
                    <span className="text-ink-mute">{f.fromId === picked.id ? '' : `${f.from} `}</span>
                    <span className="font-medium text-ink">{f.relation}</span>
                    <span className="text-ink-mute"> {f.toId === picked.id ? '' : `${f.to ?? f.value}`}</span>
                  </div>
                  {f.note && <div className="mt-0.5 text-ink-mute">{f.note}</div>}
                  {f.state !== 'active' && (
                    <div className="mt-0.5 text-danger">
                      {f.state}
                      {f.because ? ` — ${f.because}` : ''}
                    </div>
                  )}
                </div>
              ))}
              {facts.length === 0 && <div className="text-xs text-ink-mute">Nothing recorded about it yet.</div>}
            </div>

            {/* Says what goes with it, because the count is the part that changes the decision — deleting a hub takes
                a dozen facts with it and the number is the only warning that carries. */}
            <div className="mt-4 border-t border-line pt-3">
              {armed === picked.id ? (
                <div className="space-y-2">
                  <div className="text-xs text-danger">
                    Delete {picked.name} and {facts.length} fact{facts.length === 1 ? '' : 's'} joined to it? This
                    cannot be undone.
                  </div>
                  <div className="flex gap-2">
                    <button
                      disabled={busy}
                      onClick={() => void forget(picked)}
                      className="rounded-md bg-danger px-2.5 py-1.5 text-xs font-medium text-white disabled:opacity-50"
                    >
                      {busy ? 'Deleting…' : 'Delete it'}
                    </button>
                    <button
                      onClick={() => setArmed(null)}
                      className="rounded-md border border-line px-2.5 py-1.5 text-xs text-ink-soft hover:text-ink"
                    >
                      Keep it
                    </button>
                  </div>
                </div>
              ) : (
                <button
                  onClick={() => setArmed(picked.id)}
                  className="text-xs text-danger underline hover:opacity-80"
                >
                  Delete this node
                </button>
              )}
            </div>
          </Card>
        )}
      </div>

      {/* The one thing here that is waiting on a person rather than on the graph. Above the housekeeping, because
          unanswered it keeps two halves of somebody's facts apart — and because the answer is one click. */}
      {brain.maybes.length > 0 && (
        <Card className="border-wait/40 px-4 py-3">
          <div className="text-sm font-semibold">Might be the same thing</div>
          <div className="mt-2 space-y-2">
            {brain.maybes.map((m) => (
              <div key={`${m.id}-${m.otherId}`} className="flex flex-wrap items-center justify-between gap-2">
                <div className="min-w-0 flex-1 text-sm text-ink-soft">
                  Is <span className="font-medium text-ink">{m.name}</span> ({m.connections} fact
                  {m.connections === 1 ? '' : 's'}) the same {m.kind} as{' '}
                  <span className="font-medium text-ink">{m.other}</span> ({m.otherConnections})?
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <Button
                    size="sm"
                    onClick={async () => {
                      await settleSame(m.id, m.otherId, true)
                      // A merge changes the shape, so the layout the camera was framing is gone with it.
                      framed.current = false
                      await load()
                    }}
                  >
                    Same
                  </Button>
                  <Button
                    variant="soft"
                    size="sm"
                    onClick={async () => {
                      await settleSame(m.id, m.otherId, false)
                      await load()
                    }}
                  >
                    Different
                  </Button>
                </div>
              </div>
            ))}
          </div>
        </Card>
      )}

      {/* The housekeeping the graph makes obvious, named so it can be acted on. */}
      {(brain.loose.length > 0 || brain.duplicates.length > 0 || brain.waiting.length > 0) && (
        <div className="flex flex-wrap gap-2 text-xs">
          {brain.waiting.length > 0 && (
            <Pill tone="wait">{brain.waiting.length} said, not yet worked through</Pill>
          )}
          {brain.duplicates.length > 0 && (
            <Pill tone="wait">
              {brain.duplicates.length} possible duplicate{brain.duplicates.length === 1 ? '' : 's'}:{' '}
              {brain.duplicates.slice(0, 2).map((d) => `${d.a}/${d.b}`).join(', ')}
            </Pill>
          )}
          {brain.loose.length > 0 && (
            <Pill>
              {brain.loose.length} probably {brain.loose.length === 1 ? 'a value' : 'values'}:{' '}
              {brain.loose.slice(0, 3).map((l) => l.name).join(', ')}
            </Pill>
          )}
        </div>
      )}
    </div>
  )
}
