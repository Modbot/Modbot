import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import {
  BAR_PAGES,
  DEFAULT_PINS,
  NAV,
  barPages,
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

test('the phone bar shows four pinned pages', () => {
  assert.equal(BAR_PAGES, 4)
})

test('the bar takes the first four pins, and the rest are only in the sheet', () => {
  const saved = ['live', 'bans', 'now', 'calendar', 'people', 'audit']

  assert.deepEqual(ids(barPages(everything, saved)), ['live', 'bans', 'now', 'calendar'])
  assert.deepEqual(ids(pinnedPages(everything, saved)), saved)
})

test('before anything is pinned the bar is the four default pins', () => {
  assert.deepEqual(ids(barPages(everything, null)), ['now', 'people', 'live', 'calendar'])
})

test('a pin this person cannot open leaves room for the next pin on the bar, not a gap', () => {
  const saved = ['bans', 'now', 'live', 'calendar', 'people']

  assert.deepEqual(ids(barPages(person('ViewLiveInstances', 'ViewCalendar', 'ViewMembers'), saved)), [
    'now',
    'live',
    'calendar',
    'people',
  ])
})

test('with fewer than four pins the bar has fewer pages, and nothing pinned leaves only More', () => {
  assert.deepEqual(ids(barPages(everything, ['live'])), ['live'])
  assert.deepEqual(ids(barPages(everything, [])), [])
  assert.deepEqual(ids(barPages(person(), null)), ['now'])
})

// The components are not rendered here (the suites run in Node, with no document), so what the
// phone's bar and sheet carry is read from their source, as sheet.test.ts reads the stylesheet.
const chrome = readFileSync(new URL('../src/components/Chrome.tsx', import.meta.url), 'utf8')

function body(start: string, end: string): string {
  const from = chrome.indexOf(start)
  const to = chrome.indexOf(end, from + start.length)
  assert.ok(from >= 0 && to > from, `${start} .. ${end} is in Chrome.tsx`)
  return chrome.slice(from, to)
}

test('Search and Actions are in the Menu sheet, where the phone bar no longer has them', () => {
  const sheet = body('function PageGrid(', 'function HealthButton(')

  assert.match(sheet, /onClick=\{closeThen\(nav\.onSearch\)\}\s+aria-label="Search"/)
  assert.match(sheet, /actions && \(\s*<Button[^>]*onClick=\{closeThen\(onActions\)\}\s+aria-label="Actions"/)
  // Actions only where the page has some, as on the bar it replaces.
  assert.match(sheet, /const actions = hasPageActions\(useShortcutList\(\)\)/)

  const bar = body('function PinnedBar(', 'function MenuBar(')
  assert.ok(!/label="Search"|label="Actions"/.test(bar))
})

test('the phone bar is the first four pins and More, which opens the sheet', () => {
  const bar = body('function PinnedBar(', 'function MenuBar(')

  assert.match(bar, /barPages\(me, pins\)/)
  assert.match(bar, /label="More" onClick=\{onMore\}/)
})

test('the bar and the sheet ask the same question for what a phone is', () => {
  assert.match(body('export function BottomBar(', 'function PinnedBar('), /usePhoneMenu\(place\)/)
  assert.match(body('export function NavSheet(', 'const PAGE_ICONS'), /usePhoneMenu\(appearance\.place\)/)
  assert.match(
    body('function usePhoneMenu(', 'export function NavSheet('),
    /useMedia\(SHEET\)[\s\S]*usePhoneLayout\(\)[\s\S]*place !== 'headset'/,
  )
})

test('the phone bar leaves the safe area clear and marks the current page', () => {
  const bar = body('function PinnedBar(', 'function MenuBar(')

  assert.match(bar, /pb-\[env\(safe-area-inset-bottom\)\]/)
  assert.match(bar, /current=\{lit === item\.id\}/)
  // The page's own scroll box reserves the bar's height and the safe area under it.
  const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
  assert.match(app, /pb-\[calc\(3\.25rem\+env\(safe-area-inset-bottom\)\)\]/)
})
