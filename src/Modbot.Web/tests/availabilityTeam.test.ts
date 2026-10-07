import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  bestTimes,
  bestTimeText,
  HOUR_RANGES,
  NO_FILTERS,
  rolesOf,
  shadeOf,
  shownDays,
  shownHours,
  shownPeople,
  tally,
  type Slot,
  type TeamFilters,
  type TeamPerson,
} from '../src/lib/availabilityTeam.ts'
import type { AvailabilityState } from '../src/lib/availabilityZones.ts'

const person = (id: string, roles: string[] = []): TeamPerson => ({ id, name: id.toUpperCase(), roles, timeZone: 'Europe/London', cells: [] })

/** A person's week in the viewer's grid, with only these hours set. */
const weekWith = (...hours: [day: number, hour: number, state: AvailabilityState][]): (AvailabilityState | null)[] => {
  const week: (AvailabilityState | null)[] = Array.from({ length: 168 }, () => null)
  for (const [day, hour, state] of hours) week[day * 24 + hour] = state
  return week
}

/** A week of hours with these counts and nobody named. */
const slotsWith = (...counts: [day: number, hour: number, count: number][]): Slot[] => {
  const slots: Slot[] = Array.from({ length: 168 }, () => ({ count: 0, people: [] }))
  for (const [day, hour, count] of counts) slots[day * 24 + hour] = { count, people: [] }
  return slots
}

const ALL_DAYS = [0, 1, 2, 3, 4, 5, 6]
const ALL_HOURS = Array.from({ length: 24 }, (_, hour) => hour)
const filters = (change: Partial<TeamFilters> = {}): TeamFilters => ({ ...NO_FILTERS, ...change })

test('with no filters everybody is shown, every day and every hour', () => {
  const people = [person('a'), person('b')]

  assert.deepEqual(shownPeople(people, NO_FILTERS), people)
  assert.deepEqual(shownDays(NO_FILTERS), ALL_DAYS)
  assert.deepEqual(shownHours(NO_FILTERS), ALL_HOURS)
  assert.equal(NO_FILTERS.ifNeeded, true)
})

test('the role filter keeps the people who hold the role, and the person filter the people picked', () => {
  const people = [person('a', ['Moderator']), person('b', ['Moderator', 'Host']), person('c', ['Host'])]

  assert.deepEqual(shownPeople(people, filters({ role: 'Host' })).map((p) => p.id), ['b', 'c'])
  assert.deepEqual(shownPeople(people, filters({ people: ['a', 'c'] })).map((p) => p.id), ['a', 'c'])
  assert.deepEqual(shownPeople(people, filters({ role: 'Host', people: ['a', 'c'] })).map((p) => p.id), ['c'])
  assert.deepEqual(shownPeople(people, filters({ role: 'Nobody holds this' })), [])
})

test('the roles to pick from are the ones somebody holds, once each and sorted', () => {
  assert.deepEqual(rolesOf([person('a', ['Moderator', 'Host']), person('b', ['Host']), person('c')]), ['Host', 'Moderator'])
})

test('weekdays are Monday to Friday and the weekend is the two days after', () => {
  assert.deepEqual(shownDays(filters({ days: 'weekdays' })), [0, 1, 2, 3, 4])
  assert.deepEqual(shownDays(filters({ days: 'weekend' })), [5, 6])
})

test('night, daytime and evening share no hour between them and leave none out', () => {
  const night = shownHours(filters({ hours: 'night' }))
  const daytime = shownHours(filters({ hours: 'daytime' }))
  const evening = shownHours(filters({ hours: 'evening' }))

  assert.deepEqual(night, [0, 1, 2, 3, 4, 5])
  assert.deepEqual(daytime, [6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17])
  assert.deepEqual(evening, [18, 19, 20, 21, 22, 23])
  assert.deepEqual([...night, ...daytime, ...evening], ALL_HOURS)
  assert.deepEqual(HOUR_RANGES.all, [0, 23])
})

test('an hour counts the people free in it, and those free if needed only when the filter says so', () => {
  const people = [person('a'), person('b'), person('c')]
  const weeks = new Map([
    ['a', weekWith([1, 18, 'free'], [1, 19, 'free'], [1, 20, 'free'], [1, 21, 'free'])],
    ['b', weekWith([1, 19, 'free'], [1, 20, 'ifNeeded'])],
    ['c', weekWith([2, 10, 'free'])],
  ])

  const counted = tally(people, weeks, true)
  assert.equal(counted[24 + 18].count, 1)
  assert.equal(counted[24 + 19].count, 2)
  assert.equal(counted[24 + 20].count, 2)
  assert.equal(counted[2 * 24 + 10].count, 1)
  assert.equal(counted[0].count, 0)
  assert.deepEqual(counted[24 + 20].people, [
    { id: 'a', name: 'A', state: 'free' },
    { id: 'b', name: 'B', state: 'ifNeeded' },
  ])

  const strict = tally(people, weeks, false)
  assert.equal(strict[24 + 20].count, 1)
  assert.deepEqual(strict[24 + 20].people, [{ id: 'a', name: 'A', state: 'free' }])
  assert.equal(strict[24 + 19].count, 2)
})

test('somebody the grid has no week for is free in no hour', () => {
  const slots = tally([person('a'), person('late')], new Map([['a', weekWith([0, 0, 'free'])]]), true)

  assert.equal(slots[0].count, 1)
  assert.ok(slots.slice(1).every((slot) => slot.count === 0))
})

test('only the people shown are counted', () => {
  const weeks = new Map([
    ['a', weekWith([0, 9, 'free'])],
    ['b', weekWith([0, 9, 'free'])],
  ])

  assert.equal(tally([person('a')], weeks, true)[9].count, 1)
  assert.equal(tally([person('a'), person('b')], weeks, true)[9].count, 2)
})

test('the shade is the share of the people shown who are free, in four steps', () => {
  assert.equal(shadeOf(0, 8, 1), 0)
  assert.equal(shadeOf(1, 8, 1), 1)
  assert.equal(shadeOf(2, 8, 1), 1)
  assert.equal(shadeOf(3, 8, 1), 2)
  assert.equal(shadeOf(4, 8, 1), 2)
  assert.equal(shadeOf(6, 8, 1), 3)
  assert.equal(shadeOf(7, 8, 1), 4)
  assert.equal(shadeOf(8, 8, 1), 4)
  assert.equal(shadeOf(1, 3, 1), 2)
  assert.equal(shadeOf(2, 3, 1), 3)
  assert.equal(shadeOf(3, 3, 1), 4)
  assert.equal(shadeOf(1, 1, 1), 4)
})

test('the scale follows the number of people shown, so one free of two is darker than one free of eight', () => {
  assert.ok(shadeOf(1, 2, 1) > shadeOf(1, 8, 1))
})

test('an hour with fewer free than the minimum is drawn as none, and with nobody shown nothing is drawn', () => {
  assert.equal(shadeOf(1, 8, 2), 0)
  assert.equal(shadeOf(2, 8, 2), 1)
  assert.equal(shadeOf(3, 8, 4), 0)
  assert.equal(shadeOf(4, 8, 4), 2)
  assert.equal(shadeOf(0, 0, 1), 0)
})

test("a day's best time is its longest stretch of hours in a row with the most people free", () => {
  // Tuesday: 18:00 one, 19:00 and 20:00 two, 21:00 one. Wednesday: 10:00 one.
  const slots = slotsWith([1, 18, 1], [1, 19, 2], [1, 20, 2], [1, 21, 1], [2, 10, 1])

  assert.deepEqual(bestTimes(slots, ALL_DAYS, ALL_HOURS, 1), [
    { day: 1, from: 19, to: 21, count: 2 },
    { day: 2, from: 10, to: 11, count: 1 },
  ])
})

test('of two stretches with the same count the longer wins, and of two as long the earlier', () => {
  const longer = slotsWith([0, 9, 2], [0, 10, 1], [0, 11, 2], [0, 12, 2])
  assert.deepEqual(bestTimes(longer, ALL_DAYS, ALL_HOURS, 1), [{ day: 0, from: 11, to: 13, count: 2 }])

  const earlier = slotsWith([0, 9, 2], [0, 10, 2], [0, 11, 1], [0, 12, 2], [0, 13, 2])
  assert.deepEqual(bestTimes(earlier, ALL_DAYS, ALL_HOURS, 1), [{ day: 0, from: 9, to: 11, count: 2 }])
})

test('the best times come most people first, then longest, then in the order of the week', () => {
  const slots = slotsWith([0, 9, 1], [1, 9, 3], [2, 9, 3], [2, 10, 3], [4, 9, 2])

  assert.deepEqual(
    bestTimes(slots, ALL_DAYS, ALL_HOURS, 1).map((t) => t.day),
    [2, 1, 4, 0],
  )
})

test('a day with nobody free, or fewer than the minimum, has no best time', () => {
  const slots = slotsWith([0, 9, 1], [1, 9, 3])

  assert.deepEqual(bestTimes(slots, ALL_DAYS, ALL_HOURS, 1).map((t) => t.day), [1, 0])
  assert.deepEqual(bestTimes(slots, ALL_DAYS, ALL_HOURS, 2).map((t) => t.day), [1])
  assert.deepEqual(bestTimes(slots, ALL_DAYS, ALL_HOURS, 4), [])
  assert.deepEqual(bestTimes(slotsWith(), ALL_DAYS, ALL_HOURS, 1), [])
})

test('the day and hour filters narrow which hours can be a best time', () => {
  const slots = slotsWith([0, 10, 5], [0, 19, 2], [5, 19, 4])

  assert.deepEqual(bestTimes(slots, ALL_DAYS, ALL_HOURS, 1).map((t) => t.day), [0, 5])
  assert.deepEqual(bestTimes(slots, shownDays(filters({ days: 'weekdays' })), shownHours(filters({ hours: 'evening' })), 1), [
    { day: 0, from: 19, to: 20, count: 2 },
  ])
  assert.deepEqual(bestTimes(slots, shownDays(filters({ days: 'weekend' })), ALL_HOURS, 1), [{ day: 5, from: 19, to: 20, count: 4 }])
})

test('a best time reads day, hours and how many of those shown are free', () => {
  assert.equal(bestTimeText({ day: 1, from: 19, to: 21, count: 2 }, 3), 'Tue 19:00-21:00 · 2 of 3 free')
  assert.equal(bestTimeText({ day: 6, from: 22, to: 24, count: 5 }, 8), 'Sun 22:00-00:00 · 5 of 8 free')
})
