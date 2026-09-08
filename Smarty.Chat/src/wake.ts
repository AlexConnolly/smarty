/**
 * Hearing your own name, and knowing when you have been dismissed.
 *
 * The whole of always-on listening rests on this being right in both directions. A wake that fires when it
 * shouldn't takes the entire screen off somebody mid-sentence, for a word they said to another person in the room.
 * One that fails to fire means shouting your assistant's name at a laptop that is definitely listening and
 * definitely ignoring you, which is worse than no feature.
 *
 * It is deliberately not clever. The transcript comes from Whisper, which mishears a short name in a predictable
 * way — one letter, usually a vowel — so the name is matched within one edit, and everything else is exact. No
 * scoring, no thresholds to tune: a greeting and your name, or your name to open a sentence.
 */

/**
 * Said to it directly. Any of these immediately before the name is an address rather than a mention.
 *
 * This was briefly a list of manglings as well — "a", "ay", "eh", "um" — because a real attempt came back as "a pep"
 * and never woke. That was the wrong place to fix it: the transcriber was guessing its language and running its
 * creative fallback, and told to expect the name it returns "Hey Pip." from the same audio. Widening this list would
 * have papered over that and cost false wakes on "the pip", "a pip", "one pip" for ever.
 */
const GREETINGS = ['hey', 'hi', 'hello', 'yo', 'ok', 'okay', 'hey there']

/**
 * Ways of saying "not this, go away".
 *
 * The whole utterance has to be one of these, which is the point: "cancel the dentist on Thursday" is a thing
 * somebody would very reasonably say to an assistant, and dropping the screen instead of doing it would be a
 * strange kind of obedience.
 */
const DISMISSALS = ['cancel', 'stop', 'exit', 'quit', 'never mind', 'nevermind', 'forget it', 'shut up', 'go away']

/**
 * A transcript with the transcriber's own noises taken out of it.
 *
 * Whisper annotates what it could not make out, in brackets, in the middle of the words it could: a real sentence
 * comes back as "How's it going today? [BLANK_AUDIO]". Testing whether that is worth sending is not enough — it has
 * to be CLEANED, or the tag is sent to the assistant as part of the question and shown to the user as part of theirs.
 * Which is exactly what happened on the first sentence anybody ever said to this feature.
 */
export function clean(transcript: string): string {
  return (
    (transcript ?? '')
      /*
       * From the marker to the end of the note, rather than the markers alone.
       *
       * The malformed ones are what force this. Whisper returned "How's it going today? [BLANK_AUDIO]" for the first
       * sentence anybody said to this, and "Hey Pip! What is the capital of France? * More Sound ]" for the second —
       * the same note to itself, with the brackets half-formed. Deleting just the punctuation leaves "More Sound"
       * sitting there looking like something a person said, and it gets sent as part of the question.
       *
       * The cost is a literal asterisk in real speech, which a transcriber does not produce: it writes "times".
       */
      .replace(/[*[][^*[\]]*(?:[*\]]|$)/g, ' ')
      .replace(/\((?:silence|inaudible|music|laughter|blank[_ ]?audio|noise|sound)[^)]*\)/gi, ' ')
      .replace(/[\]♪♫]/g, ' ')
      .replace(/\s+/g, ' ')
      .replace(/\s+([,.!?;:])/g, '$1')
      .trim()
  )
}

/** Down to words: lowercase, no punctuation, single spaces. Whisper's punctuation is its own invention anyway. */
export function words(transcript: string): string[] {
  return (transcript ?? '')
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\s']/gu, ' ')
    .split(/\s+/)
    .filter(Boolean)
}

/**
 * One insertion, deletion or substitution apart — no more.
 *
 * Enough for the way a short name comes back from a speech model ("pep", "peep", "pips") and not enough to make one
 * name into another. It is generous at three letters on purpose: a three-letter name is exactly the kind that gets
 * misheard, and refusing to bend there would fail the commonest case of all. What keeps that safe is not this
 * function — it is that a name still has to be greeted or open the sentence to count, and nobody says "hey dip" to
 * a room. Below three letters there is nothing left to spend an edit on.
 */
export function near(heard: string, name: string): boolean {
  if (heard === name) return true
  if (name.length < 3) return false
  if (Math.abs(heard.length - name.length) > 1) return false

  let a = 0
  let b = 0
  let edits = 0
  while (a < heard.length && b < name.length) {
    if (heard[a] === name[b]) {
      a++
      b++
      continue
    }
    if (++edits > 1) return false
    if (heard.length > name.length) a++
    else if (heard.length < name.length) b++
    else {
      a++
      b++
    }
  }
  return edits + (heard.length - a) + (name.length - b) <= 1
}

/**
 * Has it been called by name?
 *
 * @returns what was said AFTER the name, when it has — which is usually the request itself, and is why this
 * returns a string rather than a boolean. "Hey Pip, what's the weather" should not need saying twice.
 */
export function called(transcript: string, name: string): { woken: boolean; rest: string } {
  const said = words(transcript)
  const target = words(name)[0] ?? ''
  if (!target) return { woken: false, rest: '' }

  /*
   * What follows the name, IN THE WORDS IT WAS SAID IN.
   *
   * The matching runs on a flattened copy — lowercased, stripped of punctuation — because that is what makes a name
   * comparable. Returning the request from that copy was a mistake: "Hey Pip, what's the capital of France?" went to
   * the assistant as "what s the capital of france", which reads as though something had chewed it. The flattened
   * words decide WHERE the name is; the original text is what gets passed on.
   */
  const original = (transcript ?? '').trim()

  // Whisper's punctuation is worth exactly one thing, and this is it: a comma after a name is the difference
  // between being spoken TO and being spoken ABOUT. "Pip, put the kettle on" and "Pip said it was fine yesterday"
  // are the same first word and opposite intentions, and nothing else in the sentence tells them apart.
  const addressed = new RegExp(`^\\s*${escape(target.slice(0, -1))}\\w?\\s*[,!?:]`, 'iu').test(transcript ?? '')

  for (let i = 0; i < said.length; i++) {
    if (!near(said[i], target)) continue

    // Greeted, or plainly addressed at the start. A name anywhere else is somebody talking about it — "I asked Pip
    // to book it" is not an instruction, and answering it would be both wrong and very annoying.
    const greeted = i > 0 && GREETINGS.includes(said[i - 1])
    const opening = i === 0 && (addressed || said.length === 1)
    if (!greeted && !opening) continue

    return { woken: true, rest: after(original, said[i]) }
  }

  return { woken: false, rest: '' }
}

/**
 * The original text after the name — punctuation, capitals and all.
 *
 * Found by looking for the name as it was actually spelled in the transcript, which is the same word the flattened
 * copy matched, so it is there. Anything left over that is only punctuation is nothing: "Hey Pip." has no request in
 * it, and returning ", ." would put a comma in somebody's chat.
 */
function after(original: string, heardName: string): string {
  const at = original.toLowerCase().indexOf(heardName.toLowerCase())
  if (at < 0) return ''

  const rest = original
    .slice(at + heardName.length)
    .replace(/^[\s,.!?;:'"—–-]+/, '')
    .trim()

  return words(rest).length > 0 ? rest : ''
}

function escape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

/** Told to go away — the whole utterance, and nothing else in it. */
export function dismissed(transcript: string): boolean {
  const said = words(transcript).filter((w) => w !== 'please')
  if (said.length === 0) return false

  const phrase = said.join(' ')
  return DISMISSALS.includes(phrase)
}

/**
 * Does this look like somebody talking, or like a transcriber filling a silence?
 *
 * Much smaller than it was, and that is the point. This used to hold a blocklist of stock phrases ("like and
 * subscribe", "Subtitles by the Amara.org community") and a test for text in another alphabet, because those were
 * arriving and being sent as questions. Both were symptoms of one line of configuration: the transcriber was asked to
 * GUESS its language and allowed to retry creatively, so on an empty room it produced a confident Japanese sign-off.
 * Fixed where it was caused — see WhisperTranscriber — and the lists became dead weight.
 *
 * What is left is about audio rather than vocabulary, and no amount of configuration makes it untrue: the same token
 * over and over is not a sentence.
 */
export function speechLike(transcript: string): boolean {
  const said = words(transcript ?? '')
  if (said.length === 0) return false
  if (said.length >= 4 && new Set(said).size <= Math.ceil(said.length / 3)) return false
  return true
}

/**
 * Is there anything in this worth sending?
 *
 * Whisper does not return nothing for silence. It returns its best guess at nothing, and its best guess is a
 * small, stable set: a bracketed noise tag, a lone "thank you", a full stop on its own. Sending those starts a
 * conversation nobody began — and in an always-on loop, starts one every fifteen seconds forever.
 */
export function meaningful(transcript: string): boolean {
  const bare = (transcript ?? '').replace(/\[[^\]]*\]|\([^)]*\)/g, ' ')
  const said = words(bare)
  if (said.length === 0) return false
  if (!speechLike(bare)) return false

  // No length rule. "No" is two letters and a complete answer — in a conversation this is meant to feel like, it
  // may be the most important thing anybody says all day.
  const filler = new Set(['thank', 'you', 'thanks', 'um', 'uh', 'mm', 'hmm', 'ah', 'oh', 'the', 'a', 'so'])
  return said.some((w) => !filler.has(w))
}
