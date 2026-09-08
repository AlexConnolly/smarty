/**
 * What listening is doing, said out loud.
 *
 * This exists because of how the last few rounds of this feature went: it was fixed three times by inference from a
 * report of "it didn't work", and each fix was reasonable and none of them was checked against the device it had to
 * work on. A phone can tell you whether the microphone opened, what rate it opened at, whether the level moves when
 * somebody talks, whether any audio reached the transcriber and what came back — and none of that is visible from the
 * other end of a tunnel.
 *
 * So it is collected here and shown in two places: on the screen of the device doing the listening, and in the
 * server's log, which can be read without plugging a cable into anything. Both only when the page is opened with
 * `?listen=debug`.
 */

export interface Pass {
  at: number
  ms: number
  bytes: number
  kind: 'wake' | 'said'
  text: string
}

export interface Report {
  /** Whether the diagnostics were asked for at all. Everything here is inert when false. */
  on: boolean
  secure: boolean
  mic: string
  rate: number
  phase: string
  level: number
  floor: number
  talking: boolean
  woke: number
  passes: Pass[]
  note: string
  /**
   * How many buffers of audio have arrived, and what the audio context thinks it is doing.
   *
   * Both here because of a wrong turn: a report showing level 0 and no passes was read as "the microphone delivers
   * nothing on this device", and it was actually a tab left open in the background, where timers throttle and the
   * context suspends. A count of buffers tells those apart at a glance — no buffers at all is a broken capture, plenty
   * of buffers at zero level is a muted or backgrounded page.
   */
  chunks: number
  state: string
  /** How many voices the browser has, and whether speaking has been unlocked by a tap yet. */
  voices: number
  /** Which voice will actually speak, by name and language. "It answered in Italian" should be one glance. */
  voice: string
  primed: boolean
  /** Why nothing was heard, when nothing was. */
  mute: string
}

export function wanted(): boolean {
  try {
    return new URLSearchParams(window.location.search).get('listen') === 'debug'
  } catch {
    return false
  }
}

/**
 * The running picture. Mutable on purpose: it is written from the audio callback, which runs hundreds of times a
 * second and must not be allocating objects or setting React state.
 */
export function blank(): Report {
  return {
    on: wanted(),
    secure: typeof window !== 'undefined' && window.isSecureContext,
    mic: 'not opened',
    rate: 0,
    phase: 'waiting',
    level: 0,
    floor: 0,
    talking: false,
    woke: 0,
    passes: [],
    note: '',
    chunks: 0,
    state: 'unknown',
    voices: 0,
    voice: 'unknown',
    primed: false,
    mute: '',
  }
}

/** Keep the last few passes and nothing more — this is a window on now, not a history. */
export function noted(report: Report, pass: Pass): void {
  report.passes.push(pass)
  if (report.passes.length > 6) report.passes.shift()
}

/**
 * Send it to the server, where it goes in the log.
 *
 * Fire and forget: a diagnostic that can break the thing it is diagnosing is worse than none.
 */
export function post(report: Report): void {
  if (!report.on) return
  try {
    void fetch('/api/voice/diagnostic', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        secure: report.secure,
        mic: report.mic,
        rate: report.rate,
        phase: report.phase,
        level: Math.round(report.level * 10000) / 10000,
        floor: Math.round(report.floor * 10000) / 10000,
        woke: report.woke,
        agent: navigator.userAgent.slice(0, 90),
        note: report.note,
        chunks: report.chunks,
        state: report.state,
        voices: report.voices,
        voice: report.voice,
        primed: report.primed,
        mute: report.mute,
        hidden: typeof document !== 'undefined' ? document.hidden : false,
        passes: report.passes.map((p) => ({ kind: p.kind, ms: p.ms, kb: Math.round(p.bytes / 102.4) / 10, text: p.text })),
      }),
      keepalive: true,
    })
  } catch {
    /* nothing to do about it */
  }
}
