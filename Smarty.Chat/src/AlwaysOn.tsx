import { useCallback, useEffect, useRef, useState } from 'react'
import { transcribe } from './api'
import { amplified, join, loudness, openEars, RATE, startedTalking, wavFromSamples, type Ears } from './audio'
import { chime, chosenVoice, CHIME_MS, hush, listenForFirstTouch, Mouth, voiceState } from './speech'
import { called, clean, dismissed, meaningful } from './wake'
import { blank, noted, post, type Report } from './listenReport'

/**
 * Always on: talking to it from across the room.
 *
 * The chat window is a thing you sit in front of and type into. This is the other way of using the same assistant —
 * you are washing up, or your hands are full, and you say its name. From that moment it is a conversation: it shows
 * what it heard in letters big enough to read from where you are standing, sends when you stop talking, reads the
 * answer out loud, and listens again. Nobody presses anything.
 *
 * The whole design follows from ONE fact: if it is far enough away to be called by name, it is too far away to be
 * read like a chat. So the screen stops being a transcript and becomes a caption — one thing at a time, centred,
 * enormous — and the answer arrives as speech with the words underneath it for whatever the speaker garbled.
 *
 * Four things it has to get right, and each one is a way this feature is unusable if it goes wrong:
 *
 *  - Not waking when it wasn't called. An always-on mic that takes the screen over because somebody in the room said
 *    a similar word is worse than no feature at all. See wake.ts, which is deliberately strict about it.
 *  - Not hearing itself. The reply comes out of the speakers next to the microphone; left listening, it transcribes
 *    its own voice, decides that was a question, and answers itself until the tab closes. It goes deaf while it
 *    speaks.
 *  - Knowing when you have finished. Three seconds of quiet is the end of a sentence. Long enough to think mid-
 *    sentence, short enough that you are not left wondering whether it heard.
 *  - Letting go. Fifteen seconds with nothing worth sending and it stands down to the home page and waits to be
 *    called again — because a conversation that will not end is a screen you have to go and dismiss.
 */

/** What it is doing, which is also what the screen says. */
export type Phase = 'waiting' | 'listening' | 'thinking' | 'speaking'

/** How long a stretch of quiet ends a sentence. */
const FINISHED_TALKING_MS = 3000

/** How long with nothing worth sending before it stands down and waits to be called again. */
const GIVE_UP_MS = 15000

/**
 * How long to wait for an answer before giving the room back.
 *
 * Longer than the other clocks on purpose: a question that sends a worker off to read four pages genuinely takes
 * this long, and standing down at fifteen seconds would abandon the answer just before it arrived. The answer is not
 * lost either way — it lands in the chat, which is exactly where it can be read later.
 */
const NO_ANSWER_MS = 60000

/** How often the rolling window is checked for the wake phrase, and the transcript refreshed while talking. */
const WATCH_MS = 900
const CAPTION_MS = 900

/** Within this, the identical sentence twice is a fault rather than a person repeating themselves. */
const SAME_AGAIN_MS = 10000

/**
 * How much genuine speech an utterance needs before it counts as one.
 *
 * Half a second. Short enough for "no" and long enough that a door closing, a chair, or a fan cannot be transcribed
 * into a question — which is not hypothetical: a hallucinated sentence was sent mid-answer, from an empty room.
 */
const VOICED_SECONDS = 0.5

/**
 * How the wake watch decides what audio to look at — and it is NOT the last few seconds.
 *
 * It was, and that was a real mistake with an obvious symptom once it is written down: your name is at the START of
 * what you say. A window holding the most recent two and a half seconds has, by the time a pass finishes, scrolled
 * past the name and kept only what followed it. "Hey Pip, what's the weather like?" was arriving at the transcriber as
 * "whats the weather live" — the name discarded before anything ever looked for it. Making that window SHORTER, for
 * speed, made it worse.
 *
 * So the buffer begins where the talking begins. A stretch of quiet means whatever comes next is a new utterance, and
 * the buffer starts again from there — which puts the name in every pass, where it can be found.
 */
const ONSET_GAP_MS = 800

/** And it cannot grow for ever: somebody talking to another person in the room is not addressing this. */
const WINDOW_CAP_SECONDS = 7

/**
 * How loud counts as somebody talking, and how quiet counts as nothing at all.
 *
 * These were set an order of magnitude too high to begin with, and the result was a feature that worked once in ten
 * tries and only when shouted at. A voice from across a room, through a laptop's own microphone, arrives at a level
 * that looks like nothing: hundredths of full scale, sometimes thousandths. Anything that treats "is this loud" as
 * the question will fail — the question is "is this louder than this room's own hum", and the answer is measured
 * against a floor that follows the room.
 *
 * DEAD is the only absolute, and it is deliberately near zero: below it there is no signal to work with, and its one
 * job is to keep the transcriber from being handed a silent room to invent words about.
 */
const DEAD = 0.0015
const OVER_THE_ROOM = 1.8

/** Never let the floor sit so high that ordinary speech falls under it. */
const FLOOR_CEILING = 0.02

interface Reply {
  id: number | undefined
  text: string
  streaming: boolean
}

export interface AlwaysOnProps {
  /** Off entirely: no microphone held, nothing running. */
  enabled: boolean
  /** What it answers to. Whatever the user named it. */
  name: string
  /** Called the moment it is woken, so a fresh conversation can be opened for what follows. */
  onWake: () => void
  /** Send what was heard. The ordinary path — these are logged as normal chats, because they are. */
  onSay: (text: string) => void
  /**
   * Every reply in this conversation, in order. What gets captioned and read out.
   *
   * A list rather than "the latest", because reading them out is a queue: a turn is routinely two messages and losing
   * the first one to the arrival of the second is exactly what happened.
   */
  replies: Reply[]
  /**
   * The assistant is still working on something.
   *
   * The difference between a silence that means "we are finished" and one that means "it is thinking". Without it, a
   * person waiting politely for an answer had the conversation closed on them at fifteen seconds.
   */
  busy: boolean
  /** Standing down: back to the home page, still listening for its name. */
  onStandDown: () => void
  /** The microphone was refused, or went away. The toggle turns itself off. */
  onDeaf: (why: string) => void
}

export function AlwaysOn({ enabled, name, onWake, onSay, replies, busy, onStandDown, onDeaf }: AlwaysOnProps) {
  const [phase, setPhase] = useState<Phase>('waiting')
  const [heard, setHeard] = useState('')
  const [said, setSaid] = useState('')
  const [level, setLevel] = useState(0)

  const ears = useRef<Ears | null>(null)
  /*
   * The rate the microphone is actually delivering, which is not necessarily the one asked for.
   *
   * Every threshold below is stated in SECONDS and has to become samples somewhere. Doing that with a constant made
   * every one of them wrong by a factor of three on any browser that ignores the requested rate — "the last two and a
   * half seconds" became the last eight hundred milliseconds, and "half a second of speech" became a sixth of one.
   */
  const rate = useRef(RATE)
  const window_ = useRef<Float32Array>(new Float32Array(0))
  const utterance = useRef<Float32Array>(new Float32Array(0))
  /** A transcription pass is in flight. Only ever one at a time: they are slow and they overlap badly. */
  const passing = useRef(false)
  /*
   * How long the last transcription took, and when the next one may start.
   *
   * Always-on listening re-reads the whole of what you are saying on every pass, so the cost of a pass grows with the
   * sentence — and where that cost lands is wildly different depending on where the page is. On this machine it is a
   * few hundred milliseconds. From a phone, through a tunnel, to this machine, it is seconds: the audio goes up, the
   * transcriber runs, the words come back. Asking again every 900ms in that situation queues work faster than it can
   * be done and the whole thing falls behind — which is what "it hardly ever worked on my phone" felt like.
   *
   * So the cadence follows the round trip instead of a constant: as fast as the link allows, and no faster.
   */
  const passTook = useRef(0)
  const nextPass = useRef(0)
  const floor = useRef(FLOOR_CEILING)
  /** The previous chunk's level, so a RISE can be told from a level that is merely high. */
  const wasRms = useRef(0)
  const lastVoice = useRef(0)
  const lastSound = useRef(0)
  /*
   * How much of what is being said was actually SPOKEN, in samples.
   *
   * The transcript cannot answer this. A gate loose enough to hear somebody across a room hands the transcriber room
   * tone as well, and handed room tone it does not return nothing — it returns a sentence, and the sentence gets
   * sent. One arrived in the middle of a real answer, in what looked like Japanese, from nobody at all. Audio that
   * never rose above the room is not a question however convincing the words look.
   */
  const voiced = useRef(0)
  const lastUseful = useRef(0)
  const phaseRef = useRef<Phase>('waiting')
  const heardRef = useRef('')
  /*
   * The part of the request that arrived WITH the name, before listening proper began.
   *
   * Kept apart from the rest because the two come from different audio and only one of them is still growing. "Hey
   * Pip, what's the weather in London" is heard in one breath by the watch; what follows is heard by the listening that
   * starts after the acknowledgement. Letting the second overwrite the first turned a whole question into whatever
   * fragment came after the chime.
   */
  const seed = useRef('')
  /*
   * Which messages have been read out, and which is being read now.
   *
   * A set rather than a single id: messages arrive faster than they can be spoken, and the ones waiting their turn
   * must not be forgotten. The one being read is held separately so a message that is still streaming is not marked
   * done before it has finished arriving.
   */
  const spoken = useRef<Set<number>>(new Set())
  const reading = useRef<number | null>(null)
  const sending = useRef(false)
  const sentSince = useRef(false)
  const waitingSince = useRef(0)
  const mouth = useRef<Mouth | null>(null)
  /** Whether the assistant is still working, read from inside timers that must not be rebuilt when it changes. */
  const working = useRef(false)
  /*
   * The last thing sent, and when.
   *
   * A belt-and-braces guard rather than a fix for a known path: a message went twice and I could not reproduce which
   * way round it happened. Saying the same sentence twice inside a few seconds is not something people do to an
   * assistant, and it is exactly what a stuck buffer or a doubled timer looks like from outside — so the second one
   * is dropped and the first stands.
   */
  const lastSent = useRef<{ text: string; at: number } | null>(null)

  /*
   * The callbacks, held in a ref.
   *
   * Not fussiness: an effect that depends on a callback re-runs whenever its identity changes, and a parent that
   * passes an inline arrow gives it a new identity on every render. The effect that depends on them is the one that
   * OPENS THE MICROPHONE — so the mic would be released and reacquired on every keystroke elsewhere in the app,
   * losing whatever was being said and, on some browsers, asking for permission again.
   */
  /** What listening is doing, for the panel and for the log. Inert unless ?listen=debug is on the url. */
  const report = useRef<Report>(blank())
  const [shown, setShown] = useState(0)

  const on = useRef({ onWake, onSay, onStandDown, onDeaf })
  on.current = { onWake, onSay, onStandDown, onDeaf }
  working.current = busy

  /** Time a pass, so the next one is not asked for before this link can answer. */
  const timed = useCallback(async <T,>(work: Promise<T>): Promise<T> => {
    const started = Date.now()
    try {
      return await work
    } finally {
      passTook.current = Date.now() - started
      nextPass.current = Date.now() + Math.min(passTook.current, 4000)
    }
  }, [])

  const move = useCallback((next: Phase) => {
    phaseRef.current = next
    setPhase(next)
  }, [])

  /** Back to waiting to be called, with nothing carried over from the conversation that just ended. */
  const standDown = useCallback(() => {
    mouth.current?.stop()
    mouth.current = null
    hush()
    utterance.current = new Float32Array(0)
    window_.current = new Float32Array(0)
    voiced.current = 0
    heardRef.current = ''
    setHeard('')
    setSaid('')
    sending.current = false
    sentSince.current = false
    spoken.current = new Set()
    reading.current = null
    lastSent.current = null
    ears.current?.end()
    ears.current?.deafen(false)
    move('waiting')
    on.current.onStandDown()
  }, [move])

  /* ---- the ear ---------------------------------------------------------------------------------------------- */

  useEffect(() => {
    if (!enabled) return

    let alive = true
    openEars((chunk) => {
      if (!alive) return

      const now = Date.now()
      const rms = loudness(chunk)
      setLevel(rms)
      // Measured before lastVoice is moved, or the gap is always zero.
      const quietFor = now - lastVoice.current

      /*
       * The room's own level, followed rather than assumed.
       *
       * Down instantly, up slowly: a room that falls quiet is recognised at once, and one that gets noisier is not
       * chased — otherwise a voice raises the floor as it speaks and talks itself back under it. Capped, because a
       * floor that drifts above ordinary speech makes the whole thing deaf, which is the failure this replaced.
       */
      floor.current = Math.min(FLOOR_CEILING, rms < floor.current ? rms : floor.current * 0.999 + rms * 0.001)
      const talking = rms > Math.max(DEAD, floor.current * OVER_THE_ROOM)
      if (talking) lastVoice.current = now
      report.current.chunks++
      wasRms.current = rms
      report.current.level = rms
      report.current.floor = floor.current
      report.current.talking = talking
      report.current.phase = phaseRef.current
      if (rms > DEAD) lastSound.current = now
      // The room's floor is only meaningful once it has heard the room. Until then, treat a first loud chunk as
      // speech rather than as the floor itself.
      if (floor.current === FLOOR_CEILING && rms < FLOOR_CEILING) floor.current = rms

      if (phaseRef.current === 'waiting') {
        /*
         * Start the buffer where the talking starts — a gap in the sound, or a rise above what the room was doing.
         *
         * The gap alone was not enough. A kitchen with a radio on never goes quiet, so the buffer never restarted and
         * the name sat somewhere in the middle of seven seconds of music. A rise handles that: a voice near the
         * microphone steps up over music across the room, and that step is the start of the sentence.
         */
        if (startedTalking(rms, wasRms.current, floor.current, quietFor, ONSET_GAP_MS)) {
          window_.current = new Float32Array(0)
        }
        if (window_.current.length < rate.current * WINDOW_CAP_SECONDS) {
          window_.current = join(window_.current, chunk)
        }
      } else if (phaseRef.current === 'listening') {
        utterance.current = join(utterance.current, chunk)
        if (talking) voiced.current += chunk.length
      }
    })
      .then((open) => {
        if (!alive) {
          open.close()
          return
        }
        ears.current = open
        rate.current = open.rate
        report.current.mic = 'open'
        report.current.rate = open.rate
        report.current.state = open.state()
        lastUseful.current = Date.now()
      })
      .catch((err: unknown) => {
        const why = err instanceof Error ? err.message : 'the microphone is not available'
        report.current.mic = `refused: ${why}`
        post(report.current)
        on.current.onDeaf(why)
      })

    return () => {
      alive = false
      ears.current?.close()
      ears.current = null
      hush()
    }
  }, [enabled])

  /* ---- listening for its name ------------------------------------------------------------------------------ */

  useEffect(() => {
    if (!enabled) return

    const timer = window.setInterval(() => {
      if (phaseRef.current !== 'waiting' || passing.current || Date.now() < nextPass.current) return
      /*
       * Louder than THIS ROOM, rather than loud full stop.
       *
       * This gate is where waking actually died, and the reason was the number rather than the idea: it asked for a
       * fixed 0.012 of full scale, and a voice four metres from a laptop measures 0.0089 — so the window was never
       * transcribed, the name was never heard, and no amount of saying it made any difference. Only shouting did.
       *
       * Measured, not guessed: of eleven chunks of ordinary speech recorded at that distance, the old absolute bar
       * passed three and the room-relative one passes ten. Relative is also what keeps this cheap — a fixed floor low
       * enough for quiet speech would sit under every fridge and fan in the house, and the transcriber would run
       * flat out on nothing.
       */
      if (Date.now() - lastVoice.current > WATCH_MS * 2) return
      if (window_.current.length < rate.current) return

      const audio = amplified(window_.current)
      const file = wavFromSamples(audio, rate.current)
      passing.current = true
      timed(transcribe(file, 'name'))
        .then((text) => {
          noted(report.current, { at: Date.now(), ms: passTook.current, bytes: file.size, kind: 'wake', text })
          if (phaseRef.current !== 'waiting') return
          const { woken, rest } = called(text, name)
          if (!woken) {
            // It was read, and it was not us. If the buffer is full there is nothing to be gained by reading the same
            // seven seconds again — the next thing said starts a new one.
            if (window_.current.length >= rate.current * WINDOW_CAP_SECONDS) {
              window_.current = new Float32Array(0)
            }
            return
          }

          // What followed the name is the request. "Hey Pip, what's the weather" should not need saying twice, so it
          // seeds the utterance and the three-second clock decides whether more is coming.
          window_.current = new Float32Array(0)
          utterance.current = new Float32Array(0)
          heardRef.current = clean(rest)
          setHeard(heardRef.current)
          // The request that came in the same breath as the name was heard by the watch, which only transcribes audio
          // that rose above the room — so it is already proven speech, and the counter starts satisfied. Without this
          // the commonest case of all ("Hey Pip, what's the weather") would wait for a second sentence that never
          // comes.
          voiced.current = heardRef.current ? rate.current * VOICED_SECONDS : 0
          setSaid('')
          lastUseful.current = Date.now()
          lastVoice.current = Date.now()
          sentSince.current = false
          spoken.current = new Set()
          reading.current = null
          report.current.woke++
          move('listening')
          on.current.onWake()

          /*
           * Say something back, immediately.
           *
           * The screen appearing is not enough on its own — you are across the room and may not be looking at it.
           * Being called by name and getting silence back is the thing that makes this feel broken: you cannot tell
           * whether it heard you, so you say the name again, louder, and end up watching a transcript of yourself
           * saying it twice. Everything that answers to a name makes a noise at this point, and it is not decoration:
           * it is what tells you to start talking.
           *
           * Deaf for the length of it, because the speaker is next to the microphone and it would otherwise take its
           * own acknowledgement for the start of your sentence. The clocks restart when it stops, so quiet only counts
           * as "finished" from the moment you could actually have begun.
           */
          ears.current?.begin()
          ears.current?.deafen(true)
          chime()
          window.setTimeout(() => {
            if (phaseRef.current !== 'listening') return
            ears.current?.deafen(false)
            lastVoice.current = Date.now()
            lastSound.current = Date.now()
            lastUseful.current = Date.now()
          }, CHIME_MS)
        })
        .catch((err: unknown) => {
          // A dropped pass costs nothing and the next one is along in a moment — but if they are ALL dropping, that
          // is the whole answer, and this is the only place it is visible.
          report.current.note = `wake pass failed: ${err instanceof Error ? err.message : 'unknown'}`
        })
        .finally(() => {
          passing.current = false
        })
    }, WATCH_MS)

    return () => window.clearInterval(timer)
  }, [enabled, name, move, timed])

  /* ---- listening to what you are saying -------------------------------------------------------------------- */

  useEffect(() => {
    if (!enabled) return

    const timer = window.setInterval(() => {
      if (phaseRef.current !== 'listening') return

      const quietFor = Date.now() - lastVoice.current

      // Finished. Sent on quiet rather than on a word, because "that's all" is not a thing people say to a machine
      // and a button is what this feature exists to avoid.
      if (quietFor >= FINISHED_TALKING_MS && heardRef.current && !passing.current && !sending.current) {
        const text = heardRef.current

        // Nothing was said loudly enough to be a person. Whatever the transcriber made of it, it is not a question.
        if (voiced.current < rate.current * VOICED_SECONDS) {
          heardRef.current = ''
          setHeard('')
          utterance.current = new Float32Array(0)
          voiced.current = 0
          return
        }

        if (dismissed(text)) {
          standDown()
          return
        }

        if (meaningful(text)) {
          const repeat =
            lastSent.current &&
            lastSent.current.text === text &&
            Date.now() - lastSent.current.at < SAME_AGAIN_MS
          if (repeat) {
            heardRef.current = ''
            setHeard('')
            utterance.current = new Float32Array(0)
            voiced.current = 0
            return
          }

          lastSent.current = { text, at: Date.now() }
          sending.current = true
          setSaid(text)
          setHeard('')
          heardRef.current = ''
          utterance.current = new Float32Array(0)
          voiced.current = 0
          // Deaf from here until the answer has been read out: everything it would hear in between is either the
          // tail of your own sentence or its own voice, and both end up as the next question.
          ears.current?.end()
          ears.current?.deafen(true)
          sentSince.current = true
          waitingSince.current = Date.now()
          move('thinking')
          on.current.onSay(text)
          return
        }

        // Heard something, and it was nothing: silence's best guess at words. Clear it and keep waiting.
        heardRef.current = ''
        setHeard('')
        utterance.current = new Float32Array(0)
        voiced.current = 0
      }

      /*
       * Nothing worth sending for a long time. Stand down rather than sit there with the screen taken over.
       *
       * A microphone that has gone silent altogether counts as nothing happening too — a device that has been muted,
       * or a track that has quietly died, would otherwise hold the screen open for as long as the tab is on.
       *
       * UNLESS it is still working on an answer, and that exception is the whole point of this clock existing. Silence
       * while an assistant is thinking is not somebody wandering off, it is somebody waiting — and closing the
       * conversation on them, mid-answer, back to the home page, is a strange thing to do to a person who is being
       * patient. When it is busy, listening stops and the screen stays: no microphone open, nothing to send, just the
       * answer when it comes.
       */
      const deadAir = Date.now() - lastSound.current >= GIVE_UP_MS
      if ((Date.now() - lastUseful.current >= GIVE_UP_MS || deadAir) && !sending.current) {
        if (working.current) {
          ears.current?.end()
          ears.current?.deafen(true)
          utterance.current = new Float32Array(0)
          voiced.current = 0
          heardRef.current = ''
          seed.current = ''
          setHeard('')
          waitingSince.current = Date.now()
          move('thinking')
          return
        }
        standDown()
        return
      }

      // The live caption. Whisper is given the whole utterance each pass rather than the newest slice, which is what
      // keeps the wording coherent — and what makes the caption settle rather than lurch as you talk.
      if (!passing.current && Date.now() >= nextPass.current && utterance.current.length > rate.current * 0.6) {
        /*
         * The compressed recording where there is one, the raw samples where there is not.
         *
         * Same audio either way; a twentieth of the bytes. It matters here and not in the waking because this is the
         * upload that repeats: a sentence in progress is re-read every pass, so its seconds go up the wire over and
         * over, and from a phone through a tunnel that is the difference between keeping up and falling behind.
         */
        const clip = ears.current?.clip() ?? null
        const audio = clip ?? wavFromSamples(amplified(utterance.current), rate.current)
        passing.current = true
        const bytes = audio.size
        timed(transcribe(audio))
          .then((raw) => {
            noted(report.current, { at: Date.now(), ms: passTook.current, bytes, kind: 'said', text: raw })
            if (phaseRef.current !== 'listening') return
            // Cleaned here, once, so what is shown and what is sent are the same thing — and neither of them carries
            // the transcriber's notes to itself. The first sentence anybody said to this arrived as "How's it going
            // today? [BLANK_AUDIO]", and that is what was sent.
            const text = clean(raw)
            if (!text) return
            heardRef.current = text
            setHeard(text)
            if (meaningful(text)) lastUseful.current = Date.now()
          })
          .catch((err: unknown) => {
            report.current.note = `pass failed: ${err instanceof Error ? err.message : 'unknown'}`
          })
          .finally(() => {
            passing.current = false
          })
      }
    }, CAPTION_MS)

    return () => window.clearInterval(timer)
  }, [enabled, move, standDown, timed])

  /* ---- waiting for an answer that may never come ----------------------------------------------------------- */

  useEffect(() => {
    if (!enabled || phase !== 'thinking') return

    /*
     * A reply that never arrives must not hold the room hostage.
     *
     * Every other path here ends on a clock and this one did not: sent, deaf, screen taken over, waiting on a turn
     * that failed or went off to do half an hour of work. It stands down instead — the answer still lands in the
     * chat, which is where it can be read at leisure, and the microphone is listening for its name again.
     */
    const timer = window.setInterval(() => {
      if (phaseRef.current !== 'thinking') return
      // Work in progress is not a hang. This clock is for a turn that failed silently, and a running job is the
      // opposite of that — it will either answer or fail, and both end up here.
      if (working.current) {
        waitingSince.current = Date.now()
        return
      }
      if (Date.now() - waitingSince.current < NO_ANSWER_MS) return
      standDown()
    }, CAPTION_MS)

    return () => window.clearInterval(timer)
  }, [enabled, phase, standDown])

  /* ---- reading the answer out loud ------------------------------------------------------------------------- */

  useEffect(() => {
    if (!enabled || phase === 'waiting') return

    /*
     * Only ever the answers to something THIS conversation asked.
     *
     * Message ids count from one per conversation, and a wake clears the messages a moment after the phase changes, so
     * there is a window where what is on screen belongs to a conversation that is over. Having sent something is the
     * honest test, and it keeps the useful case: a background task finishing five minutes later is still this
     * conversation talking, and it should be read out.
     */
    if (!sentSince.current) return

    /*
     * A QUEUE, read in order — the fix for messages being thrown away.
     *
     * A turn is routinely two messages: "on it, checking now", then the answer. Taking only the newest meant that when
     * the second arrived in the same render as the first, the first was never read at all — and switching mid-message
     * abandoned whatever was left of it. So the next unread message is read to its end before the following one is
     * touched, and nothing is marked read until it has finished arriving.
     */
    const next = replies.find((r) => r.id !== undefined && !spoken.current.has(r.id))
    if (!next || next.id === undefined) return

    if (reading.current !== next.id) {
      reading.current = next.id
      sending.current = false
      /*
       * Stop RECORDING as well as stop listening, and this is the pair that was missed.
       *
       * Deafening only gates the samples that measure loudness; the recorder that produces what gets SENT runs on the
       * microphone directly and knew nothing about it. So a reply arriving while it had gone back to listening was read
       * out into an open recording, transcribed, and sent back as the next question — the assistant appearing to think
       * you said the thing it had just said.
       */
      ears.current?.end()
      ears.current?.deafen(true)
      move('speaking')

      mouth.current ??= new Mouth(() => {
        // The mouth going quiet is what hands the conversation back. Get this wrong and it either talks over you or
        // leaves you wondering whose turn it is.
        if (phaseRef.current !== 'speaking') return
        ears.current?.deafen(false)
        ears.current?.begin()
        utterance.current = new Float32Array(0)
        voiced.current = 0
        heardRef.current = ''
        seed.current = ''
        setHeard('')
        lastVoice.current = Date.now()
        lastSound.current = Date.now()
        lastUseful.current = Date.now()
        move('listening')
      })
    }

    if (next.streaming) {
      mouth.current?.feed(next.id, next.text)
    } else {
      mouth.current?.finish(next.id, next.text)
      // Done with this one: only now, so a message still arriving is never skipped over.
      spoken.current.add(next.id)
      reading.current = null
    }
  }, [enabled, phase, replies, busy, move])

  /* ---- saying what it is doing ----------------------------------------------------------------------------- */

  useEffect(() => {
    // Not gated on `enabled`, and that is the point: the case worth diagnosing is the one where listening never
    // starts, and a report that only speaks while listening works cannot describe it. If the microphone was refused,
    // this is what says so.
    // Speaking is gated behind a gesture, like autoplaying audio, and always-on survives a reload — so the page can
    // be listening with no gesture behind it and the first answer is read out to nobody.
    listenForFirstTouch()

    if (!report.current.on) return

    // Redrawn often enough to watch a level move while talking; posted rarely enough not to be its own problem.
    const draw = window.setInterval(() => setShown((n) => n + 1), 250)
    const send = window.setInterval(() => {
      const voice = voiceState()
      report.current.voices = voice.voices
      report.current.primed = voice.primed
      report.current.mute = mouth.current?.mute ?? ''
      report.current.voice = chosenVoice()
      post(report.current)
    }, 4000)
    post(report.current)

    return () => {
      window.clearInterval(draw)
      window.clearInterval(send)
    }
  }, [enabled])

  /* ---- the screen ------------------------------------------------------------------------------------------ */

  useEffect(() => {
    if (phase === 'waiting') return
    const key = (e: KeyboardEvent) => {
      if (e.key === 'Escape') standDown()
    }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [phase, standDown])

  /** The message the voice is on, falling back to the newest when nothing is being read yet. */
  const onScreen = replies.find((r) => r.id === reading.current) ?? replies[replies.length - 1]

  if (!enabled || phase === 'waiting') {
    // Nothing on screen normally. With the diagnostics asked for, the WAITING state is the one worth seeing: it is
    // where "I said its name and nothing happened" happens, and until now there was nothing to look at.
    return report.current.on ? <Listening report={report.current} tick={shown} /> : null
  }

  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-bg/97 backdrop-blur-sm">
      {report.current.on && <Listening report={report.current} tick={shown} />}

      <div className="flex items-center gap-3 px-6 py-5">
        <Ear phase={phase} level={level} />
        <span className="text-sm font-medium uppercase tracking-widest text-ink-mute">{doing(phase, name)}</span>
        <button
          onClick={standDown}
          className="ml-auto rounded-full border border-line px-4 py-2 text-sm font-medium text-ink-soft transition hover:bg-surface-mid hover:text-ink"
        >
          Exit
        </button>
      </div>

      {/*
        One thing at a time, in the middle, as large as it can be. This is the part that is not a chat: a transcript
        with a scrollbar is unreadable from across a room, so what is on screen is what is being said RIGHT NOW, and
        what came before it is a single quiet line above.
      */}
      <div className="flex flex-1 flex-col items-center justify-center gap-8 px-8 pb-16 text-center">
        {said && phase !== 'listening' && (
          <div className="max-w-3xl text-xl leading-snug text-ink-mute sm:text-2xl">“{said}”</div>
        )}

        {phase === 'listening' && (
          <div className="max-w-4xl text-3xl font-medium leading-tight tracking-tight text-ink sm:text-5xl">
            {heard || <span className="text-ink-mute">…</span>}
          </div>
        )}

        {(phase === 'thinking' || phase === 'speaking') && (
          <div className="max-w-4xl text-3xl font-medium leading-tight tracking-tight text-ink sm:text-[2.75rem] sm:leading-tight">
            {/* What is being READ, not simply the newest — with a queue those are different, and the words on screen
                should be the words in the air. */}
            {onScreen?.text ? onScreen.text : <Thinking />}
          </div>
        )}
      </div>
    </div>
  )
}

/**
 * What listening is doing, on the screen of the device doing it.
 *
 * Deliberately ugly and deliberately dense. It is not a feature — it is the thing that should have existed three
 * rounds ago, when a tablet said "nothing happened" and there was no way to tell whether the microphone had opened,
 * whether the level moved when somebody spoke, or what the transcriber made of it. Read it aloud down a phone and it
 * is enough to diagnose this from anywhere.
 */
function Listening({ report, tick }: { report: Report; tick: number }) {
  const bar = Math.min(100, Math.round((report.level / 0.05) * 100))
  const floor = Math.min(100, Math.round((report.floor / 0.05) * 100))

  return (
    <div
      // Above the overlay, out of the way of the captions, and legible on a tablet at arm's length.
      className="fixed bottom-2 left-2 z-[60] max-w-[22rem] rounded-lg bg-ink/90 p-2.5 font-mono text-[0.625rem] leading-snug text-white"
      data-tick={tick}
    >
      <div className="flex gap-2">
        <span className={report.secure ? 'text-emerald-400' : 'text-red-400'}>
          {report.secure ? 'https ok' : 'NOT SECURE'}
        </span>
        <span className={report.mic === 'open' ? 'text-emerald-400' : 'text-red-400'}>mic {report.mic}</span>
        <span>{report.rate ? `${report.rate} Hz` : 'no rate'}</span>
        <span className={report.state === 'running' ? '' : 'text-amber-300'}>{report.state}</span>
        <span className={report.chunks > 0 ? '' : 'text-red-400'}>{report.chunks} buffers</span>
      </div>

      <div className="mt-1 flex items-center gap-2">
        <span className="w-14 shrink-0">{report.phase}</span>
        <span className="relative h-2 flex-1 overflow-hidden rounded-full bg-white/15">
          <span
            className={`absolute inset-y-0 left-0 ${report.talking ? 'bg-emerald-400' : 'bg-white/50'}`}
            style={{ width: `${bar}%` }}
          />
          {/* Where the bar has to reach for it to count as somebody talking. */}
          <span className="absolute inset-y-0 w-px bg-amber-300" style={{ left: `${Math.max(2, floor * 1.8)}%` }} />
        </span>
        <span className="w-24 shrink-0 text-right">
          {report.level.toFixed(4)} / {report.floor.toFixed(4)}
        </span>
      </div>

      <div className="mt-1">
        woke {report.woke}× · {report.voice}{report.primed ? '' : ' · not yet tapped'}
        {report.mute && <span className="text-amber-300"> · {report.mute}</span>}
        {report.note && <span className="text-amber-300"> · {report.note}</span>}
      </div>

      {report.passes.length === 0 ? (
        <div className="mt-1 text-amber-300">nothing sent to the transcriber yet</div>
      ) : (
        <div className="mt-1 space-y-0.5">
          {report.passes
            .slice()
            .reverse()
            .map((p, i) => (
              <div key={i} className="truncate">
                <span className="text-white/60">
                  {p.kind} {Math.round(p.ms)}ms {(p.bytes / 1024).toFixed(0)}k{' '}
                </span>
                {p.text || <span className="text-white/40">(nothing)</span>}
              </div>
            ))}
        </div>
      )}
    </div>
  )
}

function doing(phase: Phase, name: string): string {
  if (phase === 'listening') return 'Listening'
  // Not "listening" while it is thinking, because it is not: the microphone is shut. Saying so is the difference
  // between a person waiting and a person wondering whether to repeat themselves.
  if (phase === 'thinking') return `${name} is working on it`
  return `${name} is speaking`
}

/**
 * That it is hearing you, shown as the level rather than as an animation.
 *
 * A spinner tells you the software is running. A ring that moves when you talk tells you the microphone is working,
 * which is the only question anybody has when they are standing across a room talking to a laptop.
 */
function Ear({ phase, level }: { phase: Phase; level: number }) {
  const loud = Math.min(1, level * 6)
  const size = phase === 'listening' ? 0.7 + loud * 0.6 : 1

  return (
    <span className="relative grid h-6 w-6 place-items-center">
      <span
        className="absolute rounded-full bg-accent/25 transition-transform duration-100"
        style={{ height: '1.5rem', width: '1.5rem', transform: `scale(${size})` }}
      />
      <span className={`relative h-2.5 w-2.5 rounded-full bg-accent ${phase === 'speaking' ? 'animate-pulse' : ''}`} />
    </span>
  )
}

function Thinking() {
  return (
    <span className="inline-flex items-end gap-2 text-ink-mute">
      {[0, 1, 2].map((i) => (
        <span
          key={i}
          className="h-3 w-3 animate-bounce rounded-full bg-ink-mute/50"
          style={{ animationDelay: `${i * 140}ms` }}
        />
      ))}
    </span>
  )
}
