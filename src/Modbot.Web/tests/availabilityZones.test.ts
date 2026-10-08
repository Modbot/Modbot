import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  addDays,
  browserClock,
  DAY_NAMES,
  DAY_ORDER,
  hourHeading,
  hourText,
  inViewersWeek,
  instantsOf,
  isKnownZone,
  offsetAt,
  viewerWeek,
  weekStartFor,
  zoneChoices,
  type AvailabilityCell,
} from '../src/lib/availabilityZones.ts'

const HOUR = 3_600_000
const at = (iso: string) => Date.parse(iso)

/** The week the viewer sees, from a moment in it. */
const weekOf = (iso: string, zone: string) => viewerWeek(weekStartFor(at(iso), zone), zone)

const cell = (day: number, hour: number, state: AvailabilityCell['state'] = 'free'): AvailabilityCell => ({ day, hour, state })

/** The hours of the week a result holds, as `day * 24 + hour` and its state. */
const held = (week: ReturnType<typeof inViewersWeek>) =>
  week.flatMap((state, index) => (state ? [`${index}:${state}`] : []))

const TUE = 24
const SUN = 6 * 24

test('a zone is how far ahead of UTC its clocks are, and a half hour zone is a half hour', () => {
  assert.equal(offsetAt('Asia/Kolkata', at('2026-06-01T00:00:00Z')), 5.5 * HOUR)
  assert.equal(offsetAt('Europe/London', at('2026-01-15T00:00:00Z')), 0)
  assert.equal(offsetAt('Europe/London', at('2026-07-01T00:00:00Z')), HOUR)
  assert.equal(offsetAt('America/New_York', at('2026-01-15T00:00:00Z')), -5 * HOUR)
  assert.equal(offsetAt('America/New_York', at('2026-07-01T00:00:00Z')), -4 * HOUR)
})

test('dates move by calendar days, across a month and a year', () => {
  assert.deepEqual(addDays({ year: 2026, month: 3, day: 30 }, 3), { year: 2026, month: 4, day: 2 })
  assert.deepEqual(addDays({ year: 2026, month: 1, day: 1 }, -1), { year: 2025, month: 12, day: 31 })
})

test('the week starts on the Monday by the zone the viewer is in', () => {
  assert.deepEqual(weekStartFor(at('2026-06-10T12:00:00Z'), 'Europe/London'), { year: 2026, month: 6, day: 8 })

  // Late on a Sunday in New York is already Monday in London, so the two zones start different weeks.
  const moment = at('2026-06-14T23:30:00Z')
  assert.deepEqual(weekStartFor(moment, 'Europe/London'), { year: 2026, month: 6, day: 15 })
  assert.deepEqual(weekStartFor(moment, 'America/New_York'), { year: 2026, month: 6, day: 8 })
})

test('an hour on the clock is one instant, two when the clocks go back, none when they go forward', () => {
  assert.deepEqual(instantsOf({ year: 2026, month: 6, day: 1 }, 12, 'Asia/Kolkata'), [at('2026-06-01T06:30:00Z')])

  // London, 2026-03-29: 01:00 GMT becomes 02:00 BST, so 01:00 never shows.
  assert.deepEqual(instantsOf({ year: 2026, month: 3, day: 29 }, 1, 'Europe/London'), [])
  assert.deepEqual(instantsOf({ year: 2026, month: 3, day: 29 }, 2, 'Europe/London'), [at('2026-03-29T01:00:00Z')])

  // London, 2026-10-25: 02:00 BST goes back to 01:00 GMT, so 01:00 shows twice.
  assert.deepEqual(instantsOf({ year: 2026, month: 10, day: 25 }, 1, 'Europe/London'), [
    at('2026-10-25T00:00:00Z'),
    at('2026-10-25T01:00:00Z'),
  ])
})

test("the viewer's week is every hour of seven dates, with the hour the clocks skip left empty", () => {
  const ordinary = weekOf('2026-06-10T12:00:00Z', 'Europe/London')
  assert.equal(ordinary.length, 168)
  assert.ok(ordinary.every((instants) => instants.length === 1))

  // The week of 2026-03-23 ends with Sunday the 29th, whose 01:00 does not happen.
  const forward = weekOf('2026-03-25T12:00:00Z', 'Europe/London')
  assert.deepEqual(forward[SUN + 1], [])
  assert.equal(forward[SUN].length, 1)
  assert.equal(forward[SUN + 2].length, 1)

  // The week of 2026-10-19 ends with Sunday the 25th, whose 01:00 happens twice.
  const back = weekOf('2026-10-21T12:00:00Z', 'Europe/London')
  assert.equal(back[SUN + 1].length, 2)
})

test('a week seen from the same zone is the same week', () => {
  const week = weekOf('2026-06-10T12:00:00Z', 'Europe/London')
  const cells = [cell(0, 9), cell(1, 18), cell(1, 19, 'ifNeeded'), cell(6, 23)]

  assert.deepEqual(held(inViewersWeek(cells, 'Europe/London', week)), [
    '9:free',
    `${1 * 24 + 18}:free`,
    `${1 * 24 + 19}:ifNeeded`,
    `${6 * 24 + 23}:free`,
  ])
})

test('a zone far enough ahead moves the day: Monday 02:00 in Auckland is Sunday afternoon in London', () => {
  // NZST is 12 hours ahead and BST one hour, so Monday 02:00 NZST is Sunday 14:00 UTC, 15:00 BST.
  const week = weekOf('2026-06-10T12:00:00Z', 'Europe/London')

  assert.deepEqual(held(inViewersWeek([cell(0, 2)], 'Pacific/Auckland', week)), [`${SUN + 15}:free`])
})

test('a zone on the half hour keeps each hour as one hour, rounding the half up', () => {
  // IST is 5:30 ahead of UTC and BST one hour, so Tuesday 18:00 IST is 13:30 BST, shown as 14:00.
  const week = weekOf('2026-06-10T12:00:00Z', 'Europe/London')
  const evening = [cell(1, 18), cell(1, 19), cell(1, 20)]

  assert.deepEqual(held(inViewersWeek(evening, 'Asia/Kolkata', week)), [
    `${TUE + 14}:free`,
    `${TUE + 15}:free`,
    `${TUE + 16}:free`,
  ])
})

test('seen from a half hour zone, a whole hour zone lands on the hour after and carries over midnight', () => {
  // London Tuesday 18:00 and 19:00 BST are 22:30 and 23:30 IST: Tuesday 23:00 and Wednesday 00:00.
  const week = weekOf('2026-06-10T12:00:00Z', 'Asia/Kolkata')

  assert.deepEqual(held(inViewersWeek([cell(1, 18), cell(1, 19)], 'Europe/London', week)), [
    `${TUE + 23}:free`,
    `${2 * 24 + 0}:free`,
  ])
})

test('every hour of a week is still an hour, whatever the half hour', () => {
  const everything = Array.from({ length: 168 }, (_, i) => cell(Math.floor(i / 24), i % 24))
  const week = weekOf('2026-06-10T12:00:00Z', 'Europe/London')

  assert.equal(held(inViewersWeek(everything, 'Asia/Kathmandu', week)).length, 168)
  assert.equal(held(inViewersWeek(everything, 'Asia/Kolkata', week)).length, 168)
})

test('a daylight-saving change moves a person on the days after it and not before', () => {
  const newYork = [cell(1, 18)]

  // 2026-03-02: New York on EST (UTC-5), London on GMT: Tuesday 18:00 is 23:00 in London.
  const before = weekOf('2026-03-03T12:00:00Z', 'Europe/London')
  assert.deepEqual(held(inViewersWeek(newYork, 'America/New_York', before)), [`${TUE + 23}:free`])

  // 2026-03-09: New York changed to EDT on the 8th and London has not yet: one hour closer, 22:00.
  const between = weekOf('2026-03-10T12:00:00Z', 'Europe/London')
  assert.deepEqual(held(inViewersWeek(newYork, 'America/New_York', between)), [`${TUE + 22}:free`])

  // 2026-03-30: London changed to BST on the 29th too, so the gap is back to 23:00.
  const after = weekOf('2026-03-31T12:00:00Z', 'Europe/London')
  assert.deepEqual(held(inViewersWeek(newYork, 'America/New_York', after)), [`${TUE + 23}:free`])
})

test('the week the clocks go forward in has one hour fewer, and nobody is free in the hour that is not there', () => {
  const week = weekOf('2026-03-25T12:00:00Z', 'Europe/London')
  const everything = Array.from({ length: 168 }, (_, i) => cell(Math.floor(i / 24), i % 24))
  const seen = inViewersWeek(everything, 'Asia/Kolkata', week)

  assert.equal(seen[SUN + 1], null)
  assert.equal(held(seen).length, 167)
})

test('an hour the clocks have twice is free if either of them is, free winning over if needed', () => {
  const back = weekOf('2026-10-21T12:00:00Z', 'Europe/London')

  assert.equal(inViewersWeek([cell(6, 1, 'ifNeeded')], 'Europe/London', back)[SUN + 1], 'ifNeeded')
  assert.equal(inViewersWeek([cell(6, 1)], 'Europe/London', back)[SUN + 1], 'free')
})

test('somebody with no zone, or with no hours, is never free', () => {
  const week = weekOf('2026-06-10T12:00:00Z', 'Europe/London')

  assert.equal(held(inViewersWeek([cell(1, 18)], null, week)).length, 0)
  assert.equal(held(inViewersWeek([], 'Europe/London', week)).length, 0)
})

test('the zone list holds the browser zones, UTC and a saved zone the list leaves out, once each and sorted', () => {
  const choices = zoneChoices(['Not/InTheList', 'Europe/London', null, undefined])

  assert.ok(choices.includes('UTC'))
  assert.ok(choices.includes('Not/InTheList'))
  assert.equal(choices.filter((zone) => zone === 'Europe/London').length, 1)
  assert.deepEqual(choices, [...choices].sort((a, b) => a.localeCompare(b)))
})

test('a zone the browser does not know is said not to be known', () => {
  assert.equal(isKnownZone('Europe/London'), true)
  assert.equal(isKnownZone('Not/AZone'), false)
})

test('hours are written 09:00 on a 24 hour clock and 9 AM on a 12 hour one, noon and midnight included', () => {
  assert.equal(hourText(9), '09:00')
  assert.equal(hourText(17, '24h'), '17:00')
  assert.equal(hourText(0, '12h'), '12 AM')
  assert.equal(hourText(1, '12h'), '1 AM')
  assert.equal(hourText(11, '12h'), '11 AM')
  assert.equal(hourText(12, '12h'), '12 PM')
  assert.equal(hourText(13, '12h'), '1 PM')
  assert.equal(hourText(17, '12h'), '5 PM')
  assert.equal(hourText(23, '12h'), '11 PM')
})

test('an hour that ends a stretch at midnight reads as midnight', () => {
  assert.equal(hourText(24), '00:00')
  assert.equal(hourText(24, '12h'), '12 AM')
})

test('an hour along the top of a grid is two digits on a 24 hour clock and written out on a 12 hour one', () => {
  assert.equal(hourHeading(9), '09')
  assert.equal(hourHeading(18, '24h'), '18')
  assert.equal(hourHeading(18, '12h'), '6 PM')
})

test('a browser whose language has AM and PM starts on 12 hours, and one that runs to 23 on 24', () => {
  assert.equal(browserClock('en-US'), '12h')
  assert.equal(browserClock('en-GB'), '24h')
  assert.equal(browserClock('de-DE'), '24h')
})

test('the days are drawn from Sunday to Saturday and still name the stored day numbers', () => {
  assert.deepEqual(DAY_ORDER.map((day) => DAY_NAMES[day]), ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'])
  assert.deepEqual([...DAY_ORDER].sort(), [0, 1, 2, 3, 4, 5, 6])
})
