process.env.TZ = 'UTC'

import assert from 'node:assert/strict'
import { test } from 'node:test'
import { discordPieces, discordTime, unescapeDiscord } from '../src/lib/discordText.ts'

/**
 * The calendar's preview draws a channel card from Discord's own text, as the publisher sends it
 * (calendar design §14.2): timestamps in the viewer's time, world links as links, and the
 * backslashes that keep a name from being read as formatting taken off.
 */

test('a When field is two timestamps and the text around them', () => {
  const pieces = discordPieces('<t:1790000000:F> (<t:1790000000:R>)')

  assert.deepEqual(
    pieces.map((p) => p.kind),
    ['time', 'text', 'time', 'text'],
  )
  assert.equal(pieces[0].kind === 'time' && pieces[0].style, 'F')
  assert.equal(pieces[0].kind === 'time' && pieces[0].at.getTime(), 1790000000 * 1000)
  assert.equal(pieces[1].kind === 'text' && pieces[1].text, ' (')
})

test('a World field is a link with the escaped name made plain', () => {
  const pieces = discordPieces('[The \\[Black\\] Cat\\_s](https://modbot.example/worlds/wrld_1)')

  assert.equal(pieces.length, 1)
  assert.deepEqual(pieces[0], { kind: 'link', label: 'The [Black] Cat_s', url: 'https://modbot.example/worlds/wrld_1' })
})

test('plain text with no Discord markup is one piece', () => {
  assert.deepEqual(discordPieces('Group members'), [{ kind: 'text', text: 'Group members' }])
})

test('backslashes before letters and digits are kept: only formatting characters are escaped', () => {
  assert.equal(unescapeDiscord('\\*nova\\* and C:\\temp'), '*nova* and C:\\temp')
})

test('times read the way Discord writes each style', () => {
  const at = new Date('2026-10-02T20:00:00Z')
  const now = new Date('2026-09-30T20:00:00Z')

  // Newer ICU puts a narrow no-break space before PM, which \s matches.
  assert.match(discordTime(at, 't', now, 'en-US'), /^8:00\sPM$/)
  assert.equal(discordTime(at, 'D', now, 'en-US'), 'October 2, 2026')
  assert.match(discordTime(at, 'F', now, 'en-US'), /^Friday, October 2, 2026\D+8:00\sPM$/)
  assert.equal(discordTime(at, 'R', now, 'en-US'), 'in 2 days')
})
