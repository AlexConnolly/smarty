import { test } from 'node:test'
import assert from 'node:assert/strict'
import { completeWords, shortened, spoken } from '../src/speech.ts'

/**
 * What a voice says, given what a screen shows.
 *
 * The two are not the same text and treating them as one is the whole failure: an assistant answering from across the
 * room reads its reply aloud, and a reply written for a screen is full of things a voice cannot say. Asterisks for
 * emphasis, hashes for headings, a table drawn in pipes, a URL — read out literally, each is a stretch of noise where
 * a sentence should be.
 *
 *   node --test scripts/speech.test.ts
 */

test('emphasis is heard, not spelled', () => {
  assert.equal(spoken('That is **really** important'), 'That is really important')
  assert.equal(spoken('a _quiet_ word and a ~~struck~~ one'), 'a quiet word and a struck one')
})

test('a heading is a sentence', () => {
  assert.equal(spoken('## Tomorrow\nRain until noon'), 'Tomorrow, Rain until noon')
})

test('bullets become clauses rather than punctuation', () => {
  assert.equal(spoken('- eggs\n- milk\n- bread'), 'eggs, milk, bread')
})

test('a link says its words and never its address', () => {
  assert.equal(spoken('see [the fixture list](https://lcfc.com/fixtures)'), 'see the fixture list')
  assert.equal(spoken('it is at https://example.com/a/b?c=d now'), 'it is at a link now')
})

test('code is named, not read', () => {
  // Reading a shell command out character by character is worse than useless, and pretending it was not on the
  // screen is a lie about what is on the screen.
  const said = spoken('Run this:\n```\nrm -rf ./data --force\n```\nthen restart')

  assert.ok(said.includes('some code'), said)
  assert.ok(!said.includes('rm -rf'), said)
  assert.ok(said.includes('then restart'), said)
})

test('inline code keeps its words', () => {
  assert.equal(spoken('the flag is `--force`'), 'the flag is --force')
})

test('a table is a shape, not a sentence', () => {
  // Read down the middle of one and every row runs into the next.
  const said = spoken('Here:\n| item | price |\n| --- | --- |\n| mug | 4.00 |\nthat is all')

  // And the colon does not end up with a comma stuck to it by what was lifted out.
  assert.equal(said, 'Here: that is all')
})

test('emoji are dropped rather than named', () => {
  assert.equal(spoken('done ✅ and shipped 🚀'), 'done and shipped')
})

test('a long answer is cut at the end of a thought', () => {
  // A voice cannot skim. What it says has to finish a sentence and then say where the rest is.
  const long = 'One sentence here. Another sentence follows on. ' + 'And more text besides. '.repeat(40)
  const said = shortened(long, 60)

  assert.ok(said.endsWith('There is more on the screen.'), said)
  assert.ok(said.includes('Another sentence follows on.'), said)
  assert.ok(!said.includes('And more text besides. And more'), said)
})

test('a short answer is left exactly as it is', () => {
  assert.equal(shortened('Yes, at six.', 60), 'Yes, at six.')
})

test('nothing to say is nothing to say', () => {
  assert.equal(spoken(''), '')
  assert.equal(spoken('```\njust code\n```').includes('some code'), true)
})

test('a half-written word is never spoken', () => {
  // The whole rule of speaking while the answer is still arriving. The stream hands over "The weather in Lond" and
  // saying that aloud is worse than waiting a beat for the rest of it.
  assert.equal(completeWords('The weather in Lond'), 0)
  // The trailing space proves "is" is finished as well, so it goes too — the held-back tail is only ever the part
  // that has no space after it yet.
  assert.equal(completeWords('The weather in London is '), 'The weather in London is'.length)
})

test('one word at a time is not speech', () => {
  // Each utterance gets its own falling intonation, so word-by-word reads like a station announcement. A few
  // together is the smallest chunk that still sounds like a sentence.
  assert.equal(completeWords('Yes '), 0)
  assert.equal(completeWords('Yes it '), 0)
  assert.equal(completeWords('Yes it is raining now '), 'Yes it is raining now'.length)
})

test('what it takes ends on a boundary, so nothing is lost at the seam', () => {
  const stream = 'It is sixteen degrees and raining in London right now'
  const take = completeWords(stream)

  assert.ok(take > 0)
  // Resuming from exactly there leaves a whole word next, never the back half of one.
  assert.ok(/^\s/.test(stream.slice(take)), JSON.stringify(stream.slice(take, take + 6)))
})

test('the speaking limit is per message, not per conversation', () => {
  // The limit stops one long answer being read out for four minutes. Counted across a whole conversation instead, it
  // silenced everything after the first four hundred characters — so the voice worked, and then one turn later simply
  // did not, for no visible reason. shortened() is the same rule the mouth applies per message.
  const long = 'This is a sentence about the weather. '.repeat(20)

  // Each message is judged on its own length, so a second message is treated exactly like the first.
  assert.equal(shortened('Yes, at six.'), 'Yes, at six.')
  assert.ok(shortened(long).endsWith('There is more on the screen.'))
  assert.equal(shortened('Yes, at six.'), 'Yes, at six.')
})

test('the voice is never chosen by list order', async () => {
  // What happened: with no voice matching the language, the score collapsed to "is it installed locally" and the winner
  // was whichever local voice the device listed first. On this device that was Italian, and because the utterance took
  // its language from the chosen voice, it stayed Italian.
  const { pickVoiceFrom } = await import('../src/speech.ts')

  const list = [
    { name: 'Alice', lang: 'it-IT', localService: true, default: true },
    { name: 'Daniel', lang: 'en-GB', localService: true, default: false },
    { name: 'Samantha', lang: 'en-US', localService: false, default: false },
  ]

  assert.equal(pickVoiceFrom(list, 'en-gb')?.name, 'Daniel')
  // An English device with only American English available gets American English, not Italian.
  assert.equal(pickVoiceFrom(list.filter((v) => v.lang !== 'en-GB'), 'en-gb')?.name, 'Samantha')
  // Nothing in the family is a real answer: the caller then asks the engine for the language instead of accepting a
  // voice in the wrong one.
  assert.equal(pickVoiceFrom(list.filter((v) => v.lang === 'it-IT'), 'en-gb'), null)
})
