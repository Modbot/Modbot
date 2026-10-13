import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  AUTO_LINKS,
  autoLinkOf,
  cleanFillInName,
  fillInFields,
  fillInKey,
  fillInMarker,
  fillInsIn,
  fillInsOfTemplate,
  fillInText,
  splitFillIns,
  unfilled,
  valuesByKey,
} from '../src/lib/calendarFillIns.ts'
import { fieldsFromInput, newEventFromTemplate, addMinutes, lengthBetween } from '../src/lib/calendarTemplates.ts'
import { blankEvent } from '../src/lib/calendar.ts'

test('a text splits into its words and its blanks', () => {
  assert.deepEqual(splitFillIns('Movie night with {{Host}} on {{ What }}!'), [
    { kind: 'text', text: 'Movie night with ' },
    { kind: 'fillIn', name: 'Host' },
    { kind: 'text', text: ' on ' },
    { kind: 'fillIn', name: 'What' },
    { kind: 'text', text: '!' },
  ])
})

test('a marker with no name, or broken brackets, stays plain text', () => {
  assert.deepEqual(splitFillIns('a {{}} b {{ }} c {x} {{y'), [{ kind: 'text', text: 'a {{}} b {{ }} c {x} {{y' }])
})

test('a name is cleaned of brackets, line breaks and extra spaces, and kept short', () => {
  assert.equal(cleanFillInName('  Host {{ name }}\n'), 'Host name')
  assert.equal(cleanFillInName('x'.repeat(60)).length, 40)
  assert.equal(fillInMarker('Host'), '{{Host}}')
  assert.equal(fillInKey(' The   HOST '), 'the host')
})

test('the blanks in some texts are listed once each, as first spelled', () => {
  assert.deepEqual(fillInsIn(['{{Host}} and {{what}}', null, undefined, '{{host}} {{What}} {{Where}}']), ['Host', 'what', 'Where'])
})

test('a blank is filled everywhere it stands, and a value is not read for blanks again', () => {
  const values = valuesByKey(['Host', 'What'], { Host: ' Ana ', What: '{{Host}}' })

  assert.equal(fillInText('{{host}} runs {{WHAT}}; {{Host}}', values), 'Ana runs {{Host}}; Ana')
  assert.equal(fillInText('no blanks', values), 'no blanks')
})

test('a blank with no value is filled with nothing, and unfilled says which are empty', () => {
  assert.equal(fillInText('Hi {{Host}}!', {}), 'Hi !')
  assert.deepEqual(unfilled(['Host', 'What'], { Host: 'Ana', What: '  ' }), ['What'])
  assert.deepEqual(unfilled(['Host'], { Host: 'Ana' }), [])
})

const input = { ...blankEvent(new Date(2026, 9, 20, 10)), title: 'Night with {{Host}}', description: '{{What}} at 8' }

test('a template fills the main and every site-own text', () => {
  const fields = fieldsFromInput(
    { ...input, vrChatTitle: 'VR: {{Host}}', discordDescription: '{{What}} / {{Host}}', googleTitle: '' },
    120,
  )

  assert.deepEqual(fillInsOfTemplate(fields), ['Host', 'What'])

  const filled = fillInFields(fields, { Host: 'Ana', What: 'Movies' })

  assert.equal(filled.title, 'Night with Ana')
  assert.equal(filled.description, 'Movies at 8')
  assert.equal(filled.vrChatTitle, 'VR: Ana')
  assert.equal(filled.discordDescription, 'Movies / Ana')
  assert.equal(filled.googleTitle, '')
  assert.equal(filled.vrChatDescription, '')
  assert.deepEqual(fillInsOfTemplate(filled), [])
})

test('filling a template does not change the template', () => {
  const fields = fieldsFromInput(input, 120)
  fillInFields(fields, { Host: 'Ana', What: 'Movies' })

  assert.equal(fields.title, 'Night with {{Host}}')
})

test('a new event from a template ends its length after the start, and keeps the categories', () => {
  const fields = { ...fieldsFromInput(input, 90), categoryIds: ['c1', 'c2'] }
  const event = newEventFromTemplate(fields, { ...input, startsAt: '2026-10-20T23:00', endsAt: '2026-10-21T00:00' })

  assert.equal(event.startsAt, '2026-10-20T23:00')
  assert.equal(event.endsAt, '2026-10-21T00:30')
  assert.deepEqual(event.categoryIds, ['c1', 'c2'])
})

test('a template saved before categories were kept loads with none', () => {
  const fields = { ...fieldsFromInput(input, 60), categoryIds: null }

  assert.deepEqual(newEventFromTemplate(fields, input).categoryIds, [])
})

test('minutes are added to a wall-clock time, and a length is read back', () => {
  assert.equal(addMinutes('2026-10-20T19:00', 150), '2026-10-20T21:30')
  assert.equal(addMinutes('2026-12-31T23:30', 45), '2027-01-01T00:15')
  assert.equal(lengthBetween('2026-10-20T19:00', '2026-10-20T21:30'), 150)
  assert.equal(lengthBetween('2026-10-20T19:00', '2026-10-20T19:00'), 1)
  assert.equal(lengthBetween('2026-10-20T19:00', '2027-10-20T19:00'), 7 * 24 * 60)
})

test('the four automatic links are told from fill-ins by name, in any capitals or spacing', () => {
  assert.deepEqual([...AUTO_LINKS], ['Google event', 'Discord event', 'Add our Google calendar', 'VRChat group page'])
  assert.equal(autoLinkOf('  google   EVENT '), 'Google event')
  assert.equal(autoLinkOf('Host'), null)

  assert.deepEqual(splitFillIns('See {{google event}} and {{Host}}'), [
    { kind: 'text', text: 'See ' },
    { kind: 'link', name: 'Google event' },
    { kind: 'text', text: ' and ' },
    { kind: 'fillIn', name: 'Host' },
  ])
})

test('automatic links are not blanks to ask for, and stay in the text when a template is filled in', () => {
  const fields = fieldsFromInput(
    { ...input, description: '{{What}}: {{Google event}} / {{Discord event}}', vrChatDescription: '{{VRChat group page}}' },
    60,
  )

  assert.deepEqual(fillInsOfTemplate(fields), ['Host', 'What'])

  const filled = fillInFields(fields, { Host: 'Ana', What: 'Movies' })

  assert.equal(filled.description, 'Movies: {{Google event}} / {{Discord event}}')
  assert.equal(filled.vrChatDescription, '{{VRChat group page}}')
  assert.deepEqual(fillInsOfTemplate(filled), [])
})

test('a link typed in other capitals is stored under its own spelling once filled in', () => {
  assert.equal(fillInText('{{add our google calendar}}', {}), '{{Add our Google calendar}}')
})

test('a link name in a title keeps its words and loses its brackets; a fill-in stays', () => {
  assert.equal(withoutLinks('Movie {{ google EVENT }} with {{Host}}'), 'Movie Google event with {{Host}}')
  assert.equal(withoutLinks('{{VRChat group page}}{{Discord event}}'), 'VRChat group pageDiscord event')
  assert.equal(withoutLinks('Plain title'), 'Plain title')
})
