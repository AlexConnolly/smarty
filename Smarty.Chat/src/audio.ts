export interface RecordedAudio {
  wav: Blob
  peaks: number[]
  duration: number
}

/** What Whisper is fed, and what everything here resamples to. */
export const RATE = 16000

/** An open microphone. */
export interface Ears {
  /**
   * The rate the samples are ACTUALLY arriving at.
   *
   * Asked for 16 kHz and never assumed, because the request is a hint and one major browser ignores it. Safari on iOS
   * hands back 48 kHz whatever you ask for — and audio labelled 16 kHz that is really 48 kHz plays at a third speed,
   * which is a bass drone that transcribes as "(engine revving)". That is the whole reason the phone hardly ever
   * worked: not the microphone, not the distance, not the model. A number in a header.
   */
  rate: number

  /** Stop listening and release the microphone — the light goes out. */
  close(): void

  /** What the audio context thinks it is doing. "suspended" means no audio will arrive, however open the mic is. */
  state(): string
  /**
   * Stop taking anything in, without letting go of the microphone.
   *
   * What this is for: the assistant reads its answer out loud through the speakers, and the microphone is a
   * microphone. Left open, it hears the reply, transcribes it, decides somebody said something, and answers itself
   * — a machine talking to itself until the tab is closed. Releasing the mic instead would drop the recording light
   * and ask for permission again on every turn, so it stays open and stone deaf.
   */
  deafen(quiet: boolean): void

  /**
   * Start recording what is being said, compressed.
   *
   * The samples are what the loudness and the waking are worked out from, and they are the wrong thing to SEND: raw
   * 16 kHz mono is 32 KB a second, and a sentence in progress is re-read on every pass, so the same seconds go up the
   * wire again and again. On this machine that is invisible. From a phone, through a tunnel, it is most of why nothing
   * worked — a five-second sentence is 160 KB per pass, and the pass cannot finish before the next is due.
   *
   * The browser has an encoder built in and it costs nothing to run it alongside: the same audio, a twentieth of the
   * bytes, decoded by ffmpeg at the other end. Bracketed rather than continuous, so it never records the assistant
   * talking or the silence between conversations.
   */
  begin(): void

  /** Everything recorded since {@link begin}, as a file the server can read. Null when there is nothing yet. */
  clip(): Blob | null

  /** Stop recording and forget what was recorded. */
  end(): void
}

/**
 * Listen continuously, in samples.
 *
 * Always-on listening wants a stream of numbers, not a file. Recording to a container and decoding it back would
 * work for one utterance and fights everything about this: a recording is only decodable from its first chunk — the
 * header is there — so a rolling window of the last few seconds cannot be made by dropping old chunks, and cutting
 * the recorder into self-contained segments to get round that loses a slice of speech at every seam. Samples have
 * no seams: they can be windowed, joined and measured for loudness, and the only thing that ever needs a file is
 * the transcriber, at the end.
 *
 * The context is asked for 16 kHz directly, which is what Whisper wants, so nothing is resampled twice.
 */
export async function openEars(onHeard: (chunk: Float32Array) => void): Promise<Ears> {
  /*
   * Why this is not simply "permission refused".
   *
   * A browser will not give a page a microphone at all unless the page came over https — localhost excepted, which is
   * why this works on the machine itself and not from a phone on the same wifi. On iOS `navigator.mediaDevices` is not
   * merely refused, it is UNDEFINED, so the failure is a TypeError about reading a property of undefined, which reaches
   * the user as nothing useful whatsoever. Said plainly here, because it is a fixable thing and the fix is not in this
   * code.
   */
  if (!navigator.mediaDevices?.getUserMedia) {
    throw new Error(
      window.isSecureContext
        ? 'this browser will not give a page a microphone'
        : 'a microphone needs https — this page was loaded over plain http, so the browser will not allow it',
    )
  }

  const stream = await navigator.mediaDevices.getUserMedia({
    /*
     * Tuned for somebody across the room, which is the opposite of the default assumption.
     *
     * The browser's processing chain is built for a headset: a voice a few inches away, and everything else noise.
     * Point that at a person standing at the sink and it works against you — noise suppression is the worst of it,
     * because a quiet voice at four metres looks exactly like the room tone it exists to remove, so it is removed.
     * Ten attempts at normal speaking volume got through once, and only by shouting.
     *
     * So: suppression OFF, and automatic gain ON — the one part of the chain that helps here, since it lifts a quiet
     * input rather than deciding it was not wanted. Echo cancellation stays, and earns its place for a different
     * reason: the answers come out of the speakers beside the microphone.
     */
    audio: { echoCancellation: true, noiseSuppression: false, autoGainControl: true },
  })

  const AC: typeof AudioContext =
    window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
  const ctx = new AC({ sampleRate: RATE })

  const source = ctx.createMediaStreamSource(stream)
  /*
   * createScriptProcessor, deprecated and correct here.
   *
   * The replacement is an AudioWorklet, which runs the same handful of lines on the audio thread — genuinely better
   * for anything doing real work per sample, and this does none: it copies a buffer and hands it on. What an
   * AudioWorklet costs is a separate module file loaded at runtime, which is a build concern and a failure mode, to
   * shave microseconds off a job measured in minutes of listening.
   */
  const node = ctx.createScriptProcessor(4096, 1, 1)
  let deaf = false

  node.onaudioprocess = (e) => {
    if (deaf) return
    // Copied, because the buffer is reused by the audio thread the moment this returns.
    onHeard(new Float32Array(e.inputBuffer.getChannelData(0)))
  }

  /*
   * Through a silent gain node rather than straight to the destination. A ScriptProcessor is only pulled when
   * something downstream is listening, so it has to be connected — and connecting it to the speakers plays the
   * microphone back into the room, which is a feedback howl and a very short-lived feature.
   */
  const silence = ctx.createGain()
  silence.gain.value = 0
  source.connect(node)
  node.connect(silence)
  silence.connect(ctx.destination)

  /*
   * A context created without a click is born suspended, and a suspended context never calls back — so the
   * microphone light is on, the permission is granted, and nothing is heard. It matters most in the exact case this
   * feature is for: always-on is remembered across reloads, so on a refresh there IS no click, and the page comes
   * back apparently listening and completely deaf.
   *
   * Resuming works from any gesture, so if there hasn't been one yet, the next one anywhere on the page does it.
   */
  await ctx.resume().catch(() => {})
  if (ctx.state !== 'running') {
    const wake = () => {
      void ctx.resume()
      document.removeEventListener('pointerdown', wake)
      document.removeEventListener('keydown', wake)
    }
    document.addEventListener('pointerdown', wake)
    document.addEventListener('keydown', wake)
  }

  /*
   * A recorder on the same stream, for what gets SENT.
   *
   * Two readers of one microphone: the sample path answers "is somebody talking, and how loud", the recorder produces
   * something small enough to upload. Started and stopped around each utterance rather than left running, so a
   * conversation's worth of audio is never sitting in memory and the assistant's own voice is never in the file.
   */
  let recorder: MediaRecorder | null = null
  let clips: Blob[] = []
  const codec = ['audio/webm;codecs=opus', 'audio/ogg;codecs=opus', 'audio/mp4'].find((type) =>
    typeof MediaRecorder !== 'undefined' && MediaRecorder.isTypeSupported?.(type),
  )

  return {
    rate: ctx.sampleRate,
    state: () => ctx.state,
    begin() {
      if (!codec) return
      /*
       * Always a FRESH recording, never a no-op.
       *
       * This used to return early if one was already running, and that is how the assistant ended up talking to
       * itself: a reply arriving while it was still listening left the recorder going, so it recorded its own answer
       * being read out, and the next pass transcribed that and sent it as a question. Whatever was being recorded
       * before, this is a new utterance and it starts empty.
       */
      if (recorder) {
        try {
          recorder.stop()
        } catch {
          /* already stopped */
        }
        recorder = null
        clips = []
      }
      try {
        recorder = new MediaRecorder(stream, { mimeType: codec, audioBitsPerSecond: 24000 })
        clips = []
        recorder.ondataavailable = (e) => {
          if (e.data.size > 0) clips.push(e.data)
        }
        // A timeslice, so a sentence in progress can be read before it has finished.
        recorder.start(500)
      } catch {
        recorder = null
      }
    },
    clip() {
      // Only ever from the FIRST chunk: the container's header is in it, and a file that starts anywhere else is not
      // audio at all — which is why the windowed listening cannot use this and reads samples instead.
      if (!recorder || clips.length === 0) return null
      return new Blob(clips, { type: codec })
    },
    end() {
      try {
        recorder?.stop()
      } catch {
        /* already stopped */
      }
      recorder = null
      clips = []
    },
    close() {
      try {
        recorder?.stop()
      } catch {
        /* already stopped */
      }
      recorder = null
      clips = []
      node.onaudioprocess = null
      try {
        node.disconnect()
        silence.disconnect()
        source.disconnect()
      } catch {
        /* already torn down */
      }
      stream.getTracks().forEach((t) => t.stop())
      void ctx.close()
    },
    deafen(quiet: boolean) {
      deaf = quiet
    },
  }
}

/**
 * Samples as a WAV file, for the one thing that wants a file: the transcriber.
 *
 * Resampled here rather than at capture, so a whole utterance is converted in one pass instead of chunk by chunk —
 * there are no seams to interpolate across that way. Whisper wants 16 kHz; everything else is a conversion.
 */
export function wavFromSamples(samples: Float32Array, from = RATE): Blob {
  return encodeWav(from === RATE ? samples : resampled(samples, from, RATE), RATE)
}

/**
 * Linear interpolation, which is enough for speech.
 *
 * A proper resampler filters before it decimates and matters for music. This is going to a transcriber trained on
 * telephone-quality audio, and the difference is inaudible to it — where the difference between 48 kHz labelled as
 * 16 and honest 16 is the difference between a sentence and a drone.
 */
export function resampled(samples: Float32Array, from: number, to: number): Float32Array {
  if (from === to || samples.length === 0) return samples

  const ratio = from / to
  const out = new Float32Array(Math.max(1, Math.floor(samples.length / ratio)))
  for (let i = 0; i < out.length; i++) {
    const at = i * ratio
    const low = Math.floor(at)
    const high = Math.min(samples.length - 1, low + 1)
    const part = at - low
    out[i] = samples[low] * (1 - part) + samples[high] * part
  }
  return out
}

/**
 * How loud a stretch of samples is, 0–1.
 *
 * Root mean square rather than peak, because the question being asked of it is "is somebody talking" and a single
 * click or a chair creak has a high peak and almost no energy. This is the whole of the voice detection: quiet for
 * long enough means the sentence is finished, and quiet for much longer means nobody is there.
 */
export function loudness(samples: Float32Array): number {
  if (samples.length === 0) return 0
  let sum = 0
  for (let i = 0; i < samples.length; i++) sum += samples[i] * samples[i]
  return Math.sqrt(sum / samples.length)
}

/** The last `seconds` of a run of samples, at whatever rate they arrived at. */
export function tail(samples: Float32Array, seconds: number, rate = RATE): Float32Array {
  const want = Math.floor(seconds * rate)
  return samples.length <= want ? samples : samples.subarray(samples.length - want)
}

/** Two runs of samples, end to end. */
export function join(a: Float32Array, b: Float32Array): Float32Array {
  const out = new Float32Array(a.length + b.length)
  out.set(a, 0)
  out.set(b, a.length)
  return out
}

/**
 * Has somebody just STARTED talking?
 *
 * The wake watch needs the beginning of a sentence, because that is where a name goes. Finding it by waiting for
 * silence works in a quiet room and fails completely in a room with a radio on: there is never a gap, so the buffer
 * never restarts, and the name ends up somewhere in the middle of seven seconds of music where it stands no chance.
 * A device on a kitchen counter is far more often in the second kind of room than the first.
 *
 * So a beginning is either of two things: a gap in the sound, or a RISE above whatever the room has been doing. The
 * rise is what handles the radio — a voice near the microphone is louder than music across the room, and the step up
 * is the moment the sentence starts, whether or not anything was quiet beforehand.
 *
 * A rising EDGE, not a level: it asks whether the previous moment was near the background as well as whether this one
 * is above it. Otherwise steady music sits above the background for ever and every chunk looks like a new sentence.
 */
export function startedTalking(
  rms: number,
  previous: number,
  background: number,
  quietForMs: number,
  gapMs = 800,
): boolean {
  if (quietForMs > gapMs) return true

  const floorish = Math.max(background, 0.0008)
  return previous <= floorish * 1.6 && rms > floorish * 2.5
}

/**
 * The loudest single sample in a run. What normalising works from.
 */
export function peak(samples: Float32Array): number {
  let top = 0
  for (let i = 0; i < samples.length; i++) {
    const v = Math.abs(samples[i])
    if (v > top) top = v
  }
  return top
}

/**
 * Turn it up before anybody has to read it.
 *
 * The single biggest thing that makes speech from across a room transcribable. A voice four metres from a laptop
 * arrives at a tenth of the level of the same voice at arm's length, and a transcriber handed that quiet a waveform
 * returns silence — not because it cannot make out the words, but because there is barely a signal to work on. Scaled
 * up to near full range first, the same audio comes back as a sentence.
 *
 * The gain is capped, and that is what stops this being a bad idea: without a limit, a silent room is amplified until
 * its own noise floor is a roar, and the transcriber obligingly invents words to describe it. That is not a
 * hypothetical either — it invented a sentence in another script and it was sent as a question. So the ceiling is
 * modest, and it is paired with never being handed a window that had no speech in it.
 */
export function amplified(samples: Float32Array, target = 0.6, most = 12): Float32Array {
  const top = peak(samples)
  if (top === 0) return samples

  const gain = Math.min(most, target / top)
  if (gain <= 1.05) return samples

  const out = new Float32Array(samples.length)
  for (let i = 0; i < samples.length; i++) out[i] = Math.max(-1, Math.min(1, samples[i] * gain))
  return out
}

// Decode a recorded blob (webm/opus etc.), resample to 16 kHz mono, and produce a WAV (for Whisper)
// plus a small set of waveform peaks (for the visualisation).
export async function toWav16k(blob: Blob, bars = 56): Promise<RecordedAudio> {
  const arrayBuf = await blob.arrayBuffer()

  const AC: typeof AudioContext = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
  const decodeCtx = new AC()
  let audioBuf: AudioBuffer
  try {
    audioBuf = await decodeCtx.decodeAudioData(arrayBuf.slice(0))
  } finally {
    decodeCtx.close()
  }

  const targetRate = 16000
  const length = Math.max(1, Math.ceil(audioBuf.duration * targetRate))
  const offline = new OfflineAudioContext(1, length, targetRate)
  const src = offline.createBufferSource()
  src.buffer = audioBuf
  src.connect(offline.destination)
  src.start()
  const rendered = await offline.startRendering()
  const samples = rendered.getChannelData(0)

  return {
    wav: encodeWav(samples, targetRate),
    peaks: computePeaks(samples, bars),
    duration: audioBuf.duration,
  }
}

function encodeWav(samples: Float32Array, sampleRate: number): Blob {
  const dataSize = samples.length * 2
  const buffer = new ArrayBuffer(44 + dataSize)
  const view = new DataView(buffer)
  const writeString = (offset: number, s: string) => {
    for (let i = 0; i < s.length; i++) view.setUint8(offset + i, s.charCodeAt(i))
  }

  writeString(0, 'RIFF')
  view.setUint32(4, 36 + dataSize, true)
  writeString(8, 'WAVE')
  writeString(12, 'fmt ')
  view.setUint32(16, 16, true)
  view.setUint16(20, 1, true) // PCM
  view.setUint16(22, 1, true) // mono
  view.setUint32(24, sampleRate, true)
  view.setUint32(28, sampleRate * 2, true) // byte rate
  view.setUint16(32, 2, true) // block align
  view.setUint16(34, 16, true) // bits per sample
  writeString(36, 'data')
  view.setUint32(40, dataSize, true)

  let off = 44
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]))
    view.setInt16(off, s < 0 ? s * 0x8000 : s * 0x7fff, true)
    off += 2
  }
  return new Blob([view], { type: 'audio/wav' })
}

function computePeaks(samples: Float32Array, bars: number): number[] {
  const bucket = Math.floor(samples.length / bars) || 1
  const peaks: number[] = []
  for (let b = 0; b < bars; b++) {
    let max = 0
    const start = b * bucket
    const end = Math.min(samples.length, start + bucket)
    for (let i = start; i < end; i++) {
      const v = Math.abs(samples[i])
      if (v > max) max = v
    }
    peaks.push(max)
  }
  const norm = Math.max(0.01, ...peaks)
  return peaks.map((p) => p / norm)
}

export function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds))
  const m = Math.floor(s / 60)
  return `${m}:${String(s % 60).padStart(2, '0')}`
}
