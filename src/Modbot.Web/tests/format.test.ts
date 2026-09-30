import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  accessInGame,
  ago,
  dateTime,
  dateTimeWithWeekday,
  duration,
  formatDay,
  formatDayRange,
  headCountText,
  howLong,
  elapsed,
  lengthOfTime,
  needsYear,
  notLinkedTo,
  oldestReading,
  pastActions,
  timeOfDay,
  whenRange,
  plural,
} from '../src/lib/format.ts'

const NOW = '2026-09-18T12:00:00Z'

test('how long says the same steps as ago, without the ago', () => {
  assert.equal(howLong('2026-09-18T11:59:30Z', NOW), '30s')
  assert.equal(howLong('2026-09-18T11:30:00Z', NOW), '30m')
  assert.equal(howLong('2026-09-18T06:00:00Z', NOW), '6h')
  assert.equal(howLong('2026-08-19T12:00:00Z', NOW), '30d')
  assert.equal(howLong('2026-06-20T12:00:00Z', NOW), '2mth')

  assert.equal(ago('2026-08-19T12:00:00Z', NOW), '30d ago')
})

test('somebody Modbot has no record of has not been known for any time at all', () => {
  assert.equal(howLong(null, NOW), 'never')
})

test('a clock that ran backwards does not produce a negative age', () => {
  assert.equal(howLong('2026-09-18T12:00:30Z', NOW), '0s')
})

test('an age stays in days up to 45 of them', () => {
  assert.equal(ago('2026-08-19T12:00:00Z', NOW), '30d ago')
  assert.equal(ago('2026-08-05T12:00:00Z', NOW), '44d ago')
})

test('past 45 days an age is whole months passed', () => {
  assert.equal(ago('2026-08-04T12:00:00Z', NOW), '1mth ago')
  assert.equal(ago('2026-06-20T12:00:00Z', NOW), '2mth ago')
  assert.equal(ago('2026-03-02T12:00:00Z', NOW), '6mth ago')
  assert.equal(ago('2025-09-19T12:00:00Z', NOW), '11mth ago')
})

test('past a year an age is whole years passed, so a year and a half is still one', () => {
  assert.equal(ago('2025-09-18T12:00:00Z', NOW), '1y ago')
  assert.equal(ago('2025-03-04T12:00:00Z', NOW), '1y ago')
  assert.equal(ago('2024-09-18T12:00:00Z', NOW), '2y ago')
})

test('how long goes on past days in the same steps', () => {
  assert.equal(howLong('2026-03-02T12:00:00Z', NOW), '6mth')
  assert.equal(howLong('2025-03-04T12:00:00Z', NOW), '1y')
})

// Written without naming a locale or a time zone, because timeOfDay uses the viewer's: the tests
// hold on any machine they run on.
test('a time of day has no seconds: two instants in the same minute read the same', () => {
  assert.equal(timeOfDay('2026-09-18T15:41:07Z'), timeOfDay('2026-09-18T15:41:52Z'))
})

test('a time of day still tells one minute from the next', () => {
  assert.notEqual(timeOfDay('2026-09-18T15:41:07Z'), timeOfDay('2026-09-18T15:42:07Z'))
})

test('a time of day is the time alone, with no date in it', () => {
  assert.equal(timeOfDay('2026-09-18T15:41:00Z'), timeOfDay('2026-09-19T15:41:00Z'))
})

// ── Lengths of time ──────────────────────────────────────────────────────────────────────────

test('a length of time is whole units, never a decimal', () => {
  assert.equal(lengthOfTime(16), '16m')
  assert.equal(lengthOfTime(186), '3h 6m')
  assert.equal(lengthOfTime(84), '1h 24m')
  assert.equal(lengthOfTime(52 * 60), '2d 4h')
})

test('the smaller unit is left out when it is zero', () => {
  assert.equal(lengthOfTime(180), '3h')
  assert.equal(lengthOfTime(48 * 60), '2d')
})

test('a length of time is rounded before its unit is chosen', () => {
  assert.equal(lengthOfTime(59.7), '1h')
  assert.equal(lengthOfTime(24 * 60 - 0.2), '1d')
  assert.equal(lengthOfTime(24 * 60 + 40), '1d 1h')
})

test('a duration in seconds says seconds under a minute, and whole units after', () => {
  assert.equal(duration(1), '1s')
  assert.equal(duration(45), '45s')
  assert.equal(duration(59.6), '1m')
  // Whole seconds first, then minutes from those: 89.6 s is 90 s, so "2m", as TimeWords.Length says.
  assert.equal(duration(89.6), '2m')
  assert.equal(duration(720), '12m')
  assert.equal(duration(5400), '1h 30m')
})

// ── Plurals ──────────────────────────────────────────────────────────────────────────────────

test('only exactly one takes the singular', () => {
  assert.equal(plural(1, 'action'), 'action')
  assert.equal(plural(2, 'action'), 'actions')
  assert.equal(plural(0, 'action'), 'actions')
  assert.equal(plural(1.5, 'hour'), 'hours')
})

test('a word that does not just take an s is given both ways', () => {
  assert.equal(plural(1, 'person', 'people'), 'person')
  assert.equal(plural(3, 'person', 'people'), 'people')
})

// ── The year ─────────────────────────────────────────────────────────────────────────────────

// Built from the machine's own year, because the rule is about the viewer's current year: the
// tests hold in any year they run in. Local noon, so no time zone moves a day across New Year.
const thisYear = new Date().getFullYear()
const lastYear = thisYear - 1

test('a day in the current year is written without the year', () => {
  assert.equal(needsYear(`${thisYear}-03-03T12:00:00`), false)
  assert.ok(!formatDay(`${thisYear}-03-03T12:00:00`).includes(String(thisYear)))
})

test('a day in another year keeps its year', () => {
  assert.equal(needsYear(`${lastYear}-03-03T12:00:00`), true)
  assert.ok(formatDay(`${lastYear}-03-03T12:00:00`).includes(String(lastYear)))
})

test('the year can be asked for, for a date shown beside others that need it', () => {
  assert.ok(formatDay(`${thisYear}-03-03T12:00:00`, true).includes(String(thisYear)))
})

test('a range in the current year has no year at either end', () => {
  const range = formatDayRange(`${thisYear}-01-10T12:00:00`, `${thisYear}-02-10T12:00:00`)
  assert.ok(!range.includes(String(thisYear)))
  assert.ok(range.includes(' – '))
})

test('a range over New Year keeps both years', () => {
  const range = formatDayRange(`${lastYear}-12-15T12:00:00`, `${thisYear}-01-10T12:00:00`)
  assert.ok(range.includes(String(lastYear)))
  assert.ok(range.includes(String(thisYear)))
})

test('several dates need the year if any one of them does', () => {
  assert.equal(needsYear(`${thisYear}-01-10T12:00:00`, `${thisYear}-02-10T12:00:00`), false)
  assert.equal(needsYear(`${lastYear}-12-15T12:00:00`, `${thisYear}-01-10T12:00:00`), true)
})

test('a head count taken from n_users carries a question mark, and a sure one does not', () => {
  assert.equal(headCountText(52, false), '52')
  assert.equal(headCountText(80, true), '80?')
  assert.equal(headCountText(0, true), '0?')
})

test('the question mark goes after the number however it is written', () => {
  const compact = (n: number) => (n >= 1000 ? `${n / 1000}K` : String(n))
  assert.equal(headCountText(12000, true, compact), '12K?')
  assert.equal(headCountText(12000, false, compact), '12K')
})

test('who may join is said in the words the game uses', () => {
  assert.equal(accessInGame('public'), 'Group Public')
  assert.equal(accessInGame('plus'), 'Group+')
  assert.equal(accessInGame('members'), 'Group')
  assert.equal(accessInGame('somethingNew'), 'somethingNew')
  assert.equal(accessInGame(null), null)
})

test('a time of day has no leading zero on the hour', () => {
  assert.ok(!/^0/.test(timeOfDay(`${thisYear}-03-05T08:04:00`)))
})

test('an instant is the day, then the time, written the same way as each alone', () => {
  const at = `${thisYear}-03-05T08:04:00`
  const written = dateTime(at)
  assert.ok(written.startsWith(formatDay(at)))
  assert.ok(written.endsWith(timeOfDay(at)))
})

test('an instant with its weekday is otherwise the same instant', () => {
  const at = `${thisYear}-03-05T08:04:00`
  assert.ok(dateTimeWithWeekday(at).endsWith(timeOfDay(at)))
  assert.notEqual(dateTimeWithWeekday(at), dateTime(at))
})

test('a range inside one day names the day once, and an open one ends with a dash', () => {
  const inDay = whenRange(`${thisYear}-03-05T18:02:00`, `${thisYear}-03-05T19:21:00`)
  assert.equal(inDay.split(formatDay(`${thisYear}-03-05T18:02:00`)).length, 2)
  assert.ok(inDay.includes('–'))
  assert.ok(whenRange(`${thisYear}-03-05T20:04:00`, null).endsWith('–'))
})

test('a range across midnight names both days', () => {
  const across = whenRange(`${thisYear}-03-05T18:01:00`, `${thisYear}-03-06T00:11:00`)
  assert.ok(across.includes(formatDay(`${thisYear}-03-05T18:01:00`)))
  assert.ok(across.includes(formatDay(`${thisYear}-03-06T00:11:00`)))
})

test('past actions say who acted in full words, singular only at exactly one', () => {
  assert.equal(pastActions(4, 3), '4 actions by 3 moderators')
  assert.equal(pastActions(1, 1), '1 action by 1 moderator')
  assert.equal(pastActions(2, 1), '2 actions by 1 moderator')
})

test('past actions with nobody named leave the moderators off rather than saying 0', () => {
  assert.equal(pastActions(3, 0), '3 actions')
  assert.equal(pastActions(1, 0), '1 action')
})

test('the oldest reading is the one furthest behind its own clock', () => {
  const members = { at: '2026-09-27T11:53:00Z', now: '2026-09-27T12:00:00Z' }
  const bans = { at: '2026-09-27T11:37:00Z', now: '2026-09-27T12:00:00Z' }
  assert.deepEqual(oldestReading([members, bans]), bans)
  assert.deepEqual(oldestReading([bans, members]), bans)
  assert.equal(ago(oldestReading([members, bans])!.at, '2026-09-27T12:00:00Z'), '23m ago')
})

test('a list never read leaves no oldest reading', () => {
  const read = { at: '2026-09-27T11:53:00Z', now: '2026-09-27T12:00:00Z' }
  assert.equal(oldestReading([read, { at: null, now: '2026-09-27T12:00:00Z' }]), null)
  assert.equal(oldestReading([{ at: null, now: '2026-09-27T12:00:00Z' }, read]), null)
})

test('missing accounts are named in one line', () => {
  assert.equal(notLinkedTo([]), '')
  assert.equal(notLinkedTo(['Modbot']), 'Not linked to Modbot')
  assert.equal(notLinkedTo(['Discord', 'Modbot']), 'Not linked to Discord or Modbot')
  assert.equal(notLinkedTo(['VRChat', 'Discord', 'Modbot']), 'Not linked to VRChat, Discord or Modbot')
})

test('past a month a length reads as months and days, and past a year as years and months', () => {
  assert.equal(lengthOfTime(30 * 24 * 60), '30d')
  assert.equal(lengthOfTime(30.44 * 24 * 60), '1mth')
  assert.equal(lengthOfTime(31 * 24 * 60), '1mth 1d')
  assert.equal(lengthOfTime(45 * 24 * 60), '1mth 15d')
  assert.equal(lengthOfTime(61 * 24 * 60), '2mth')
  assert.equal(lengthOfTime(90 * 24 * 60), '3mth')
  assert.equal(lengthOfTime(180 * 24 * 60), '6mth')
  assert.equal(lengthOfTime(365 * 24 * 60), '1y')
  assert.equal(lengthOfTime(430 * 24 * 60), '1y 2mth')
})

test('there are no weeks: ten days are ten days', () => {
  assert.equal(lengthOfTime(10 * 24 * 60), '10d')
})

test('a length or a duration is never negative, and zero is zero', () => {
  assert.equal(lengthOfTime(0), '0m')
  assert.equal(lengthOfTime(-30), '0m')
  assert.equal(duration(0), '0s')
  assert.equal(duration(-5), '0s')
})

test('how long a call took keeps a decimal past a second', () => {
  assert.equal(elapsed(412), '412ms')
  assert.equal(elapsed(1234), '1.2s')
})
