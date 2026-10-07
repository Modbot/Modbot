import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  DEFAULT_PINS,
  NAV,
  menuPages,
  menuTiles,
  pinList,
  pinnedPages,
  withPinToggled,
} from '../src/lib/nav.ts'
import type { CurrentUser } from '../src/lib/api.ts'

function person(...permissionNames: string[]): CurrentUser {
  return { permissionNames } as CurrentUser
}

const everything = person(
  'ViewProfile', 'ViewMembers', 'ViewLiveInstances', 'ViewCalendar', 'ViewAnalytics', 'ViewAuditLog', 'ReviewTickets',
  'ViewJoinRequests', 'ViewPosts', 'ViewGiveaways', 'EnterAvailability', 'ManageSettings', 'ViewOperationalLog',
)

const ids = (pages: readonly { id: string }[]) => pages.map((p) => p.id)

test('before anything is pinned the pins are Now, People, Live and Calendar', () => {
  assert.deepEqual([...DEFAULT_PINS], ['now', 'people', 'live', 'calendar'])
  assert.deepEqual(ids(pinnedPages(everything, null)), ['now', 'people', 'live', 'calendar'])
  assert.deepEqual(ids(pinnedPages(everything, undefined)), ['now', 'people', 'live', 'calendar'])
})

test('the default pins leave out what this person may not open', () => {
  // Now needs nothing; People needs See profiles or See members; Live and Calendar need their own.
  assert.deepEqual(ids(pinnedPages(person(), null)), ['now'])
  assert.deepEqual(ids(pinnedPages(person('ViewMembers', 'ViewCalendar'), null)), ['now', 'people', 'calendar'])
  assert.deepEqual(ids(pinnedPages(person('ViewLiveInstances'), null)), ['now', 'live'])
})

test('a saved list replaces the defaults, and an empty one is nothing pinned, not the defaults', () => {
  assert.deepEqual(ids(pinnedPages(everything, ['bans', 'now'])), ['bans', 'now'])
  assert.deepEqual(ids(pinnedPages(everything, [])), [])
  assert.deepEqual([...pinList([])], [])
  assert.deepEqual([...pinList(null)], [...DEFAULT_PINS])
})

test('pinned pages come in the order they were pinned, each once', () => {
  assert.deepEqual(ids(pinnedPages(everything, ['calendar', 'now', 'calendar', 'live'])), ['calendar', 'now', 'live'])
})

test('a pin of a page this person can no longer open is skipped', () => {
  const saved = ['bans', 'now', 'live']

  assert.deepEqual(ids(pinnedPages(everything, saved)), ['bans', 'now', 'live'])
  // Without See audit log, Bans has no tile; the other two stay.
  assert.deepEqual(ids(pinnedPages(person('ViewLiveInstances'), saved)), ['now', 'live'])
})

test('a pin of a page that is off for this person, or that no longer exists, is skipped', () => {
  const off = { permissionNames: ['UseAiChat'], chatOn: false } as CurrentUser
  const on = { permissionNames: ['UseAiChat'], chatOn: true } as CurrentUser

  assert.deepEqual(ids(pinnedPages(off, ['chat', 'now'])), ['now'])
  assert.deepEqual(ids(pinnedPages(on, ['chat', 'now'])), ['chat', 'now'])
  assert.deepEqual(ids(pinnedPages(everything, ['gone-page', 'now'])), ['now'])
})

test('a page that is not a tile of its own is never pinned', () => {
  // Members is part of People, and Health is reached from the status rows: neither has a tile.
  assert.deepEqual(ids(pinnedPages(everything, ['members', 'health', 'now'])), ['now'])
})

test('the sheet lists the pinned pages first, then every other page in the order it had', () => {
  const tiles = menuTiles(everything, ['calendar', 'now'])
  const rest = menuPages(everything).filter((p) => p.id !== 'calendar' && p.id !== 'now')

  assert.deepEqual(ids(tiles.map((t) => t.item)), ['calendar', 'now', ...ids(rest)])
  assert.deepEqual(tiles.map((t) => t.pinned), [true, true, ...rest.map(() => false)])
})

test('the sheet still has every page once, pinned or not', () => {
  const tiles = menuTiles(everything, ['live', 'people', 'bans', 'gone-page'])

  assert.deepEqual(ids(tiles.map((t) => t.item)).sort(), ids(menuPages(everything)).sort())
})

test('with nothing pinned the sheet is the menu as it was', () => {
  const tiles = menuTiles(everything, [])

  assert.deepEqual(ids(tiles.map((t) => t.item)), ids(menuPages(everything)))
  assert.ok(tiles.every((t) => !t.pinned))
})

test('the first tap on a page keeps the defaults the person was already seeing', () => {
  assert.deepEqual(withPinToggled(null, 'bans'), ['now', 'people', 'live', 'calendar', 'bans'])
  assert.deepEqual(withPinToggled(null, 'live'), ['now', 'people', 'calendar'])
})

test('a tap pins a page at the end, and another tap on it takes it out', () => {
  const once = withPinToggled(['now'], 'live')
  assert.deepEqual(once, ['now', 'live'])
  assert.deepEqual(withPinToggled(once, 'live'), ['now'])
})

test('taking out the last pin leaves an empty list, which is saved as none pinned', () => {
  assert.deepEqual(withPinToggled(['now'], 'now'), [])
})

test('pins this person cannot see are kept when they pin something else, and names the app lacks are not', () => {
  assert.deepEqual(withPinToggled(['bans', 'gone-page', 'now'], 'live'), ['bans', 'now', 'live'])
})

test('every default pin is a page the app has', () => {
  const known = new Set<string>(NAV.map((n) => n.id))

  assert.ok(DEFAULT_PINS.every((id) => known.has(id)))
})
