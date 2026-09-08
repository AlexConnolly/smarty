/**
 * Reading an answer out loud.
 *
 * Across a room, the reply is not something you look at — it is something you hear, with the words on screen as a
 * caption for the bits you missed. Which means what gets spoken is not what gets written: a screen can show a
 * bulleted list, a table and a code block, and a voice reading those out says "asterisk asterisk hash hash" and
 * loses the person entirely.
 *
 * So the text is prepared for a voice before it reaches one. Everything here is about that preparation; the speaking
 * itself is three lines of browser API at the bottom.
 */

/**
 * What to say, given what was written.
 *
 * Markdown out, structure kept. A heading becomes a sentence, a bullet becomes a clause, a code block becomes the
 * phrase "some code" — because reading a shell command aloud, character by character, is worse than useless and
 * pretending it was not there is a lie about what is on the screen.
 */
export function spoken(text: string): string {
  let out = text ?? ''

  // Fenced code first, before anything else can mangle its insides.
  out = out.replace(/```[\s\S]*?```/g, ' … some code, on the screen. ')
  out = out.replace(/`([^`]+)`/g, '$1')

  // A link says its words, never its address. "aitch tee tee pee ess colon slash slash" is nobody's idea of speech.
  out = out.replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
  out = out.replace(/\bhttps?:\/\/\S+/g, ' a link ')

  // A table is a shape, not a sentence. Read down the middle of one and every row runs into the next.
  out = out.replace(/^\s*\|.*\|\s*$/gm, ' ')

  out = out
    .replace(/^#{1,6}\s*/gm, '') // headings are sentences
    .replace(/^\s*[-*+]\s+/gm, '') // bullets are clauses
    .replace(/^\s*\d+\.\s+/gm, '') // numbered points keep their own numbers in the words
    .replace(/^\s*>\s?/gm, '')
    .replace(/(\*\*|__|\*|_|~~)/g, '')
    .replace(/^\s*[-—–]{3,}\s*$/gm, ' ')

  // Emoji and the rest of the symbol block: a screen reads them, a voice either names them or trips over them.
  out = out.replace(/[\p{Extended_Pictographic}\p{Emoji_Presentation}]/gu, ' ')

  // Paragraph breaks become a pause rather than nothing, so a list does not arrive as one long word-run.
  out = out.replace(/\n{2,}/g, '. ').replace(/\n/g, ', ')

  return out
    .replace(/\s+/g, ' ')
    .replace(/\s+([,.!?;:])/g, '$1')
    .replace(/([,.])\1+/g, '$1')
    // Punctuation left touching punctuation by whatever was removed between them. A voice reads "Here:, that is
    // all" with the stumble you would expect, and the comma was never in the writing — a lifted table was.
    .replace(/([,.!?;:])\s*[,.]/g, '$1')
    .trim()
}

/**
 * How long a reply is allowed to be, out loud.
 *
 * A voice cannot skim. Four hundred characters is around twenty seconds of speech, which is a generous answer to a
 * question asked from across a room; past that, somebody wanting the detail is going to come and read it. The cut is
 * made at a sentence end so it finishes a thought rather than stopping mid-word.
 */
export const MOUTHFUL = 400

export function shortened(text: string, limit = MOUTHFUL): string {
  if (text.length <= limit) return text

  const cut = text.slice(0, limit)
  const end = Math.max(cut.lastIndexOf('. '), cut.lastIndexOf('! '), cut.lastIndexOf('? '))
  return (end > limit * 0.4 ? cut.slice(0, end + 1) : cut) + ' There is more on the screen.'
}

/** Is there a voice available at all? Everything below is a no-op without one. */
export function canSpeak(): boolean {
  return typeof window !== 'undefined' && 'speechSynthesis' in window
}

/**
 * What the voice situation actually is, for the diagnostics — because "no voiceover" has three different causes and
 * they are indistinguishable from the outside.
 *
 * A browser can have the speech API and no voices at all: on Linux the voices come from speech-dispatcher, and without
 * it `getVoices()` is empty and `speak()` does nothing, silently, for ever. A browser can also have voices and refuse
 * to use them until the page has been touched, because speaking out loud is treated like autoplaying audio. Neither
 * says anything in a console.
 */
let primed = false

export function voiceState(): { api: boolean; voices: number; primed: boolean } {
  if (!canSpeak()) return { api: false, voices: 0, primed: false }
  return { api: true, voices: window.speechSynthesis.getVoices().length, primed }
}

/**
 * Unlock the voice on the first touch of the page.
 *
 * Speaking is gated behind a user gesture, exactly like playing a video, and the gate is silent: `speak()` returns
 * normally, nothing is heard, and no event ever fires. It matters here more than anywhere because always-on listening
 * is REMEMBERED — turn it on, reload, and the page is now listening with no gesture behind it, so the first answer is
 * read out to nobody. One empty utterance on the first tap is enough to open it.
 */
export function primeVoice(): void {
  if (primed || !canSpeak()) return
  primed = true
  try {
    const quiet = new SpeechSynthesisUtterance(' ')
    quiet.volume = 0
    window.speechSynthesis.speak(quiet)
  } catch {
    /* nothing to unlock */
  }
}

export function listenForFirstTouch(): void {
  if (typeof document === 'undefined') return
  const open = () => {
    primeVoice()
    document.removeEventListener('pointerdown', open)
    document.removeEventListener('keydown', open)
    document.removeEventListener('touchstart', open)
  }
  document.addEventListener('pointerdown', open)
  document.addEventListener('keydown', open)
  document.addEventListener('touchstart', open)
}

/**
 * The voice to use.
 *
 * Preferring a local one is not fussiness: a network voice stops mid-sentence when the wifi hiccups, and this is the
 * half of the conversation the person is relying on. Beyond that it follows the page's own language, so a British
 * assistant does not answer in an American accent for want of asking.
 */
/**
 * The language this assistant speaks, which is not the same question as what language the DEVICE is set to.
 *
 * It transcribes English — the transcriber is locked to it — so it answers in English. A tablet set to another locale
 * is still being spoken to in English and should still be spoken back to in English. Where the device is already
 * English, its exact variety is honoured, so a British phone is not answered in an American accent.
 */
function speaks(): string {
  const device = (navigator.language || '').toLowerCase()
  return device.startsWith('en') ? device : 'en-gb'
}

/**
 * The voice to use, or none — and "none" has to be a real answer.
 *
 * This used to score every voice and take the winner, which meant that when NOTHING matched the language the score
 * collapsed to "is it installed locally", and the winner was whichever local voice the device happened to list first.
 * On a device that lists an Italian voice first, the assistant answered in Italian. Then, because the utterance took
 * its language from the chosen voice, it stayed Italian.
 *
 * So a language match is now a requirement rather than a preference. If nothing matches, this returns null and the
 * caller sets the LANGUAGE on the utterance instead — which asks the engine for an English voice rather than letting
 * it fall back to whatever the platform's default happens to be.
 */
/** What a voice looks like to the choosing, so the rule can be tested without a browser in the room. */
export interface Speakable {
  name: string
  lang: string
  localService?: boolean
  default?: boolean
}

/**
 * The choosing itself, given a list and a language.
 *
 * Its own function because the rule is the whole bug and the browser is incidental: a device's voice list is not
 * something a test can arrange, and this is exactly the kind of thing that regresses quietly and is then heard by
 * somebody standing in a kitchen.
 */
export function pickVoiceFrom<T extends Speakable>(voices: T[], language: string): T | null {
  const family = language.toLowerCase().split('-')[0]
  const candidates = voices.filter((v) => (v.lang || '').toLowerCase().replace('_', '-').split('-')[0] === family)
  if (candidates.length === 0) return null

  const scored = candidates.map((v) => {
    const tag = (v.lang || '').toLowerCase().replace('_', '-')
    let score = 0
    if (tag === language.toLowerCase()) score += 4
    if (v.localService) score += 2
    if (/natural|premium|enhanced|neural/i.test(v.name)) score += 1
    if (v.default) score += 1
    return { v, score }
  })

  return scored.reduce((best, x) => (x.score > best.score ? x : best)).v
}

function pickVoice(): SpeechSynthesisVoice | null {
  return pickVoiceFrom(window.speechSynthesis.getVoices(), speaks())
}

/** Which voice is being used, for the diagnostics — so "it spoke Italian" is one glance rather than a theory. */
export function chosenVoice(): string {
  if (!canSpeak()) return 'no speech api'
  const voice = pickVoice()
  return voice ? `${voice.name} (${voice.lang})` : `none matching ${speaks()}`
}

/**
 * Say it, and call back when the mouth is closed.
 *
 * The callback is the whole reason this is not a one-liner at the call site: in a conversation, the end of speaking
 * is the moment listening starts again, and getting that wrong means either talking over the person or leaving them
 * in silence wondering whose turn it is. `onerror` counts as finished for the same reason — a voice that failed to
 * start must not leave the conversation waiting for it.
 */
export function say(text: string, done: () => void): void {
  if (!canSpeak()) {
    done()
    return
  }

  const words = shortened(spoken(text))
  if (words.length === 0) {
    done()
    return
  }

  const utterance = new SpeechSynthesisUtterance(words)
  const voice = pickVoice()
  // The language goes on every utterance, voice or no voice. Left unset, the engine falls back to the platform default
  // — which is how an English answer came out in Italian.
  utterance.lang = voice?.lang ?? speaks()
  if (voice) utterance.voice = voice
  // A shade quicker than default: default reads like a station announcement.
  utterance.rate = 1.05

  let finished = false
  let watchdog = 0
  const once = () => {
    if (finished) return
    finished = true
    window.clearTimeout(watchdog)
    done()
  }
  utterance.onend = once
  utterance.onerror = once

  /*
   * A clock on the speaking, because `onend` is not reliable and this callback is the conversation's turn signal.
   *
   * speechSynthesis drops `onend` in ordinary conditions — no voices installed at all, a voice that fails to start,
   * a tab that loses focus mid-sentence — and the cost of never hearing it here is worse than the cost of hearing it
   * twice: the microphone stays deaf and the screen sits there saying it is speaking, forever, with no way back but
   * the exit button. So it is also given a generous outside estimate of how long the words take, and whichever
   * arrives first ends the turn.
   */
  const estimate = 1500 + (words.length / 14) * 1000
  watchdog = window.setTimeout(once, estimate)

  window.speechSynthesis.cancel()
  window.speechSynthesis.speak(utterance)
}

/** Stop mid-sentence. What the exit button does, and what interrupting is. */
export function hush(): void {
  if (canSpeak()) window.speechSynthesis.cancel()
}

/**
 * "Yes?" — the sound of having been heard.
 *
 * Being called by name and getting silence back is the worst moment in the whole feature. You have said the name, you
 * do not know whether it heard you, so you say it again, louder — and the screen changes a second later showing a
 * transcript of you saying it twice. Everything that answers to a name makes a noise at this point, and it is not
 * decoration: it is the only thing that tells you to start talking.
 *
 * Two rising notes, synthesised rather than loaded. A file would be a request, a decode and a thing to ship, and this
 * is an oscillator and an envelope — and it has to be instant, which a fetch is not.
 */
export function chime(): void {
  try {
    const AC: typeof AudioContext =
      window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    const ctx = new AC()

    const play = (hz: number, at: number, secs: number) => {
      const osc = ctx.createOscillator()
      const gain = ctx.createGain()
      osc.type = 'sine'
      osc.frequency.value = hz
      // Faded in and out: a square-edged tone clicks, and a click sounds like a fault rather than an answer.
      gain.gain.setValueAtTime(0, ctx.currentTime + at)
      gain.gain.linearRampToValueAtTime(0.14, ctx.currentTime + at + 0.02)
      gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + at + secs)
      osc.connect(gain)
      gain.connect(ctx.destination)
      osc.start(ctx.currentTime + at)
      osc.stop(ctx.currentTime + at + secs + 0.02)
    }

    play(660, 0, 0.11)
    play(880, 0.1, 0.16)
    window.setTimeout(() => void ctx.close(), 700)
  } catch {
    /* no audio out is not a reason to fail to listen */
  }
}

/** How long the chime occupies the room, so the microphone can be spared hearing it. */
export const CHIME_MS = 420

/**
 * How much text to hold back before speaking any of it.
 *
 * Never a partial word — that is the whole rule, and it is why this is measured in words rather than characters. But
 * one word per utterance reads like a station announcement, because each one is spoken with its own falling
 * intonation. A few words at a time is the smallest chunk that still sounds like a sentence, and by the time the
 * first of them is out of the speaker the next few have arrived.
 */
const ENOUGH_TO_SAY = 4

/**
 * How much of the newly arrived text is safe to say: whole words, and enough of them to sound like speech.
 *
 * Its own function because it is the entire rule and the only part worth testing — everything around it is the
 * browser's voice. Returns a character count rather than the words themselves so the caller keeps one index into the
 * stream and cannot lose a space at a seam.
 */
export function completeWords(fresh: string, enough = ENOUGH_TO_SAY): number {
  // The last space is the proof: everything before it is finished, and what follows might be half a word.
  const lastBreak = fresh.search(/\s\S*$/)
  if (lastBreak <= 0) return 0

  const ready = fresh.slice(0, lastBreak)
  const words = ready.trim().split(/\s+/).filter(Boolean)
  return words.length >= enough ? lastBreak : 0
}

/**
 * Speaking a reply while it is still being written.
 *
 * Waiting for the whole answer before opening its mouth is what made it feel like a machine taking a message: three
 * seconds of nothing, then a paragraph. The words arrive from the model a few at a time, and there is no reason the
 * voice cannot follow them at the same rate — a person reading aloud from a page being written in front of them.
 *
 * What has to be true, and what all of this is for: never say half a word. So the tail of the stream is held back
 * until a space proves the word before it is finished, and only whole words are handed to the voice.
 */
export class Mouth {
  private saidUpTo = 0
  private queued = 0
  private closed = false
  private spent = 0
  private reading: number | null = null
  private watching = 0
  private settled = false
  /** Why nothing was heard, when nothing was. Read by the diagnostics. */
  mute = ''
  private readonly onQuiet: () => void

  /** @param onQuiet Called when everything fed in has been spoken and nothing more is coming. */
  constructor(onQuiet: () => void) {
    this.onQuiet = onQuiet
  }

  /**
   * Which message this is, as well as what it says.
   *
   * A turn is not one message. The assistant answers straight away — "on it, checking now" — and the real answer
   * arrives as a SECOND message a minute later, sometimes a third after that. Reading only the first leaves the
   * actual answer silent on screen; treating the second as a continuation of the first computes the new text as a
   * difference between two unrelated strings and speaks nonsense. So the mouth is told which message it is reading,
   * and starts afresh when that changes while keeping whatever is still queued behind it.
   */
  private turnTo(id: number): void {
    if (this.reading === id) return
    this.reading = id
    this.saidUpTo = 0
    this.closed = false
    // A further message in the same turn is more speaking to do, so quiet has not happened yet after all.
    this.settled = false
    /*
     * A fresh budget for each message, and this line is a bug fix rather than a detail.
     *
     * The limit exists so one long answer is not read out for four minutes — twenty seconds, then "there is more on the
     * screen". But it was counted for the LIFE OF THE MOUTH, which is the whole conversation, so once four hundred
     * characters had been spoken every later message was silent. Which read exactly as reported: the voice worked, and
     * then one turn later it simply did not, for no visible reason.
     */
    this.spent = 0
  }

  /**
   * The whole message as it stands. Called on every update; works out what is new for itself.
   *
   * Given the whole thing rather than the delta on purpose: the caller already has the text, and a stream that
   * re-sends a corrected version of what it said before (which this one does — the final message is authoritative)
   * would otherwise be spoken twice.
   */
  feed(id: number, whole: string): void {
    this.turnTo(id)
    if (this.closed) return

    const fresh = whole.slice(this.saidUpTo)
    if (!fresh) return

    const take = completeWords(fresh)
    if (take === 0) return

    this.saidUpTo += take
    this.utter(fresh.slice(0, take))
  }

  /** No more of THIS message is coming: say the last of it, and report quiet when the voice stops. */
  finish(id: number, whole: string): void {
    this.turnTo(id)
    if (this.closed) return
    this.closed = true

    const rest = whole.slice(this.saidUpTo)
    this.saidUpTo = whole.length
    if (rest.trim()) this.utter(rest)
    if (this.queued === 0 && !window.speechSynthesis?.speaking) this.settle()
    else this.watch()
  }

  /** Stop, and say nothing more. */
  stop(): void {
    this.closed = true
    this.queued = 0
    if (this.watching) {
      window.clearInterval(this.watching)
      this.watching = 0
    }
    hush()
  }

  /**
   * Whether the voice has actually finished, asked of the voice rather than guessed from a clock.
   *
   * The clock version was wrong in a way that mattered. Each piece of a streamed reply got its own timer, started when
   * the piece was QUEUED — but pieces are spoken one after another, so the fifth piece's timer ran out long before the
   * voice reached it. The mouth then reported itself quiet, listening resumed, and the microphone recorded the
   * remaining eleven seconds of the assistant reading its own answer, transcribed it, and sent it back as a question.
   *
   * `speaking` and `pending` are the authoritative answer and cost nothing to poll. The long stop is a backstop for a
   * browser that lies about both.
   */
  private watch(): void {
    if (this.watching || !canSpeak()) return

    const startedAt = Date.now()
    this.watching = window.setInterval(() => {
      const talking = window.speechSynthesis.speaking || window.speechSynthesis.pending
      const tooLong = Date.now() - startedAt > 5 * 60 * 1000
      if (talking && !tooLong) return

      window.clearInterval(this.watching)
      this.watching = 0
      this.queued = 0
      if (this.closed) this.settle()
    }, 200)
  }

  /** Report quiet once, whichever route got here. */
  private settle(): void {
    if (this.settled) return
    this.settled = true
    if (this.watching) {
      window.clearInterval(this.watching)
      this.watching = 0
    }
    this.onQuiet()
  }

  private utter(fragment: string): void {
    const words = spoken(fragment)
    if (!words) {
      if (this.closed && this.queued === 0) this.settle()
      return
    }

    // A voice cannot skim, so there is still a limit on the whole reply — just applied as it goes rather than to a
    // finished paragraph. Past it, the words stay on the screen and the speaking stops.
    if (this.spent >= MOUTHFUL) {
      if (this.closed && this.queued === 0) this.settle()
      return
    }
    this.spent += words.length

    const say = this.spent > MOUTHFUL ? words + ' There is more on the screen.' : words

    if (!canSpeak() || window.speechSynthesis.getVoices().length === 0) {
      /*
       * Nothing on this device can say it out loud.
       *
       * A browser can have the whole speech API and no voices behind it — on Linux they come from speech-dispatcher,
       * and without it `speak()` succeeds, says nothing, and fires no events. Reported rather than swallowed, because
       * "the voiceover didn't happen" has three possible causes and only this one is unfixable from here.
       */
      this.mute = 'no voice installed on this device'
      if (this.closed && this.queued === 0) this.settle()
      return
    }

    const utterance = new SpeechSynthesisUtterance(say)
    const voice = pickVoice()
    // Always the language, voice or no voice: an utterance with neither is read in the platform's default, which on a
    // device that lists an Italian voice first is Italian.
    utterance.lang = voice?.lang ?? speaks()
    if (voice) utterance.voice = voice
    utterance.rate = 1.05

    this.queued++
    let ended = false
    const done = () => {
      if (ended) return
      ended = true
      this.queued--
      // Quiet means BOTH: nothing left to say, and nothing more coming.
      if (this.queued === 0 && this.closed) this.settle()
    }
    utterance.onend = done
    utterance.onerror = done

    window.speechSynthesis.speak(utterance)
    this.watch()
  }
}
