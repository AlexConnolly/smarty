import { test } from 'node:test'
import assert from 'node:assert/strict'
import { called, clean, dismissed, meaningful, near, speechLike } from '../src/wake.ts'

/**
 * Hearing your own name.
 *
 * Both directions matter and they pull against each other. A wake that fires when it shouldn't takes the whole
 * screen off somebody mid-sentence over a word said to another person in the room; one that will not fire means
 * saying your assistant's name at a laptop that is plainly listening and plainly ignoring you.
 *
 * The transcripts here are the shapes a speech model actually returns: no capitals where you expect them, invented
 * punctuation, a short name misheard by one vowel, and — for silence — its confident guess at something.
 *
 *   node --test scripts/wake.test.ts
 */

test('called by name, with a greeting', () => {
  for (const said of ['Hey Pip', 'hey pip', 'Hey, Pip.', 'Hi Pip!', 'hello pip', 'OK Pip', 'yo pip']) {
    assert.equal(called(said, 'Pip').woken, true, said)
  }
})

test('the request after the name comes with it', () => {
  // Nobody says the name, waits to be acknowledged, and then says the thing. Losing the rest of the sentence would
  // mean asking for everything twice.
  const { woken, rest } = called("Hey Pip, what's the weather in London?", 'Pip')

  assert.equal(woken, true)
  // In the words it was said in — capitals, apostrophes and all. The flattened copy is only used to find the name.
  assert.equal(rest, "what's the weather in London?")
})

test('the name opening a sentence is being addressed', () => {
  const { woken, rest } = called('Pip, put the kettle on', 'Pip')

  assert.equal(woken, true)
  assert.equal(rest, 'put the kettle on')

  // The comma is the whole signal, and it is the one piece of Whisper's punctuation worth trusting: "Pip, put the
  // kettle on" and "Pip said it was fine" are the same first word and opposite intentions.
  assert.equal(called('Pip said it was fine yesterday', 'Pip').woken, false)
  // Just the name is being called too — nothing else it could be.
  assert.equal(called('Pip?', 'Pip').woken, true)
})

test('the name in the middle of a sentence is somebody talking ABOUT it', () => {
  // The case that makes an always-on mic bearable. Every one of these is a thing said in a room with a laptop in
  // it, and none of them is an instruction.
  for (const said of [
    'I asked Pip to book it',
    'Pip said it was fine yesterday, but I asked Pip again',
    'we should get Pip to do that',
    'that was a pip of a shot',
    'the pip is in the middle',
  ]) {
    assert.equal(called(said, 'Pip').woken, false, said)
  }
})

test('a name misheard by one letter still answers', () => {
  // What Whisper does to a short name at conversational distance: a vowel, a plural, a dropped consonant.
  for (const said of ['hey pep', 'hey pips', 'hey ip', 'hey pop']) {
    assert.equal(called(said, 'Pip').woken, true, said)
  }

  // One letter, and the line is drawn there deliberately. "Peep" is two edits from "Pip" and so is "deep" — bending
  // far enough to catch the first would catch the second, and then a room full of ordinary words wakes it. If a
  // particular mishearing turns out to be common in practice, it belongs in a list of that name's aliases rather
  // than in a looser measure of distance.
  assert.equal(called('hey peep', 'Pip').woken, false)
})

test('a different name is not near enough', () => {
  for (const said of ['hey chip', 'hey philip', 'hey mum', 'hey siri']) {
    assert.equal(called(said, 'Pip').woken, false, said)
  }
})

test('the name it actually answers to is whatever it was called', () => {
  // The assistant is named by the user on first run. Hard-coding one name here would leave everyone else shouting
  // at a machine that answers to somebody else.
  assert.equal(called('hey smarty, turn the lights off', 'Smarty').woken, true)
  assert.equal(called('hey smarty', 'Pip').woken, false)
  assert.equal(called('hey ada, what is the time', 'Ada').woken, true)
})

test('one edit, and no more', () => {
  assert.equal(near('bob', 'bob'), true)
  // A three-letter name is exactly the kind that gets misheard, so it is allowed to bend — what stops "hey dip"
  // becoming a wake word is that nobody greets a room that way, not the spelling.
  assert.equal(near('rob', 'bob'), true)
  assert.equal(near('smarty', 'smartly'), true)
  assert.equal(near('smart', 'smarty'), true)
  // Two edits is a different word.
  assert.equal(near('rod', 'bob'), false)
  assert.equal(near('smartest', 'smarty'), false)
  // Below three letters there is nothing left to spend an edit on.
  assert.equal(near('al', 'ed'), false)
})

test('being told to go away, and only that', () => {
  for (const said of ['cancel', 'Cancel.', 'stop', 'never mind', 'nevermind', 'forget it', 'exit', 'cancel please']) {
    assert.equal(dismissed(said), true, said)
  }
})

test('a sentence with cancel IN it is an instruction, not a dismissal', () => {
  // Dropping the screen instead of doing this would be a strange kind of obedience.
  for (const said of [
    'cancel the dentist on Thursday',
    'can you cancel my subscription',
    'stop the timer at five minutes',
    'exit the building at six',
  ]) {
    assert.equal(dismissed(said), false, said)
  }
})

test("silence's best guess is not a message", () => {
  // Whisper does not return nothing for nothing. In an always-on loop, sending these starts a conversation nobody
  // began — and then another one, every fifteen seconds, forever.
  for (const said of ['', '   ', '.', '[BLANK_AUDIO]', '(silence)', 'Thank you.', 'thanks', 'um', 'Mm-hmm', 'you']) {
    assert.equal(meaningful(said), false, JSON.stringify(said))
  }
})

test('actual words are a message', () => {
  for (const said of ['what is the weather', 'book me a table at eight', 'no', 'yes please']) {
    assert.equal(meaningful(said), true, said)
  }
})

test("the transcriber's notes to itself are taken out", () => {
  // Both of these are real: the first sentence anybody said to this arrived as the first one, and the second
  // sentence as the second. Testing whether they are worth sending is not enough — they were SENT, tag and all.
  assert.equal(clean("How's it going today? [BLANK_AUDIO]"), "How's it going today?")
  assert.equal(clean('Hey Pip! What is the capital of France? * More Sound ]'), 'Hey Pip! What is the capital of France?')
  assert.equal(clean('(silence) book a table *laughs*'), 'book a table')
  assert.equal(clean('[ Music ] ♪ hello ♪'), 'hello')
})

test('ordinary words are left alone by the cleaning', () => {
  assert.equal(clean('book a table at 8 for 2 — the usual place'), 'book a table at 8 for 2 — the usual place')
  assert.equal(clean("what's the weather like at the weekend?"), "what's the weather like at the weekend?")
  // A literal asterisk is the one thing given up for this, and a transcriber does not produce them — asked to
  // multiply, it writes "times".
  assert.equal(clean('what is 3 times 4'), 'what is 3 times 4')
})

test('the request keeps the words it was said in', () => {
  // It used to come back from the flattened copy used for matching, so "Hey Pip, what's the capital of France?" was
  // handed on as "what s the capital of france" — which reads as though something had chewed it.
  assert.equal(called("Hey Pip, what's the capital of France?", 'Pip').rest, "what's the capital of France?")
  assert.equal(called('Hey Pip. Book a table at 8pm.', 'Pip').rest, 'Book a table at 8pm.')
})

test('being called with nothing after it leaves nothing after it', () => {
  // "Hey Pip." on its own is somebody getting its attention, not a request — and a rest of ", ." would put a stray
  // comma in the chat.
  assert.equal(called('Hey Pip.', 'Pip').rest, '')
  assert.equal(called('Hey Pip!', 'Pip').woken, true)
})






test('a repeated token is not a sentence, whatever the configuration', () => {
  // All that is left of a much longer list. The rest of it — stock subtitle phrases, text in other alphabets — was
  // papering over a transcriber that had been asked to guess its language and allowed to retry creatively. Fixed
  // there, those symptoms stopped arriving. This one is about audio rather than vocabulary and stays true regardless.
  assert.equal(speechLike('la la la la la'), false)
  assert.equal(speechLike('you you you you you'), false)
  assert.equal(speechLike('what is the weather like today'), true)
  assert.equal(speechLike('no'), true)
})

test('a sentence begins at a gap or at a rise', async () => {
  const { startedTalking } = await import('../src/audio.ts')

  // A gap in the sound: the next thing said is a new sentence.
  assert.equal(startedTalking(0.02, 0.02, 0.002, 1200), true)

  // A radio on across the room, at a steady level: not a beginning, however loud it is. This is the case that broke
  // the quiet-only version — the room never goes silent, so the buffer never restarts and the name is buried.
  assert.equal(startedTalking(0.02, 0.02, 0.008, 100), false)

  // Somebody speaking over that radio: a step up from what the room was doing IS a beginning.
  assert.equal(startedTalking(0.05, 0.008, 0.006, 100), true)

  // And ordinary continuing speech is not a new sentence every chunk.
  assert.equal(startedTalking(0.06, 0.05, 0.004, 100), false)
})
