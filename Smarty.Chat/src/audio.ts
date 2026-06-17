export interface RecordedAudio {
  wav: Blob
  peaks: number[]
  duration: number
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
