// Google Calendar in the calendar page: the Preview's repeat in words, and the feed row's "Add to
// Google Calendar" (Google Calendar design §3.8, §3.9). Nothing here talks to the server or the
// page, so the Node test runner can load this file as it is.
import type { CalendarGoogleRepeat } from './calendar.ts'

const DAY_NAMES: Record<string, string> = {
  MO: 'Monday',
  TU: 'Tuesday',
  WE: 'Wednesday',
  TH: 'Thursday',
  FR: 'Friday',
  SA: 'Saturday',
  SU: 'Sunday',
}

const UNIT: Record<string, [string, string, string]> = {
  daily: ['Daily', 'day', 'days'],
  weekly: ['Weekly', 'week', 'weeks'],
  monthly: ['Monthly', 'month', 'months'],
}

/**
 * The repeat Google is sent, in words: "Weekly on Monday and Friday", "Every 2 weeks on Monday,
 * until 2026-12-31", "Daily, 6 times". A word Google is sent that Modbot does not write is shown
 * as it is.
 */
export function googleRepeatWords(repeat: CalendarGoogleRepeat): string {
  const unit = UNIT[repeat.frequency]
  const every = repeat.every > 1 ? repeat.every : 1
  const head = !unit ? repeat.frequency : every > 1 ? `Every ${every} ${unit[2]}` : unit[0]
  const days = repeat.frequency === 'weekly' ? repeat.days.map((d) => DAY_NAMES[d] ?? d) : []
  const end = repeat.until ? `, until ${repeat.until}` : repeat.times !== null ? `, ${repeat.times} times` : ''

  return `${head}${days.length > 0 ? ` on ${list(days)}` : ''}${end}`
}

/** "a, b and c". */
function list(items: string[]): string {
  if (items.length <= 1) return items.join('')
  return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`
}

/**
 * Google Calendar's "add this calendar" page for the calendar feed: `cid=` and the feed's address
 * as `webcal://`, the way Google takes a calendar to subscribe to. Null for an address that is not
 * a web one.
 */
export function addToGoogleLink(feedUrl: string): string | null {
  let url: URL

  try {
    url = new URL(feedUrl)
  } catch {
    return null
  }

  if (url.protocol !== 'https:' && url.protocol !== 'http:') return null

  const webcal = `webcal://${url.host}${url.pathname}${url.search}`
  return `https://calendar.google.com/calendar/r?cid=${encodeURIComponent(webcal)}`
}

/**
 * The date line of Google's event popup, in the event's own zone: "Friday, October 17 ⋅ 8:00 – 9:00 PM",
 * and for an event that runs past midnight both days: "Friday, October 17 ⋅ 11:00 PM – Saturday, October 18 ⋅ 1:00 AM".
 * A zone this browser does not know is read in the viewer's own.
 */
export function googleWhen(startsAt: string, endsAt: string, timeZone: string, locale?: string): string {
  const start = new Date(startsAt)
  const end = new Date(endsAt)

  const formats = (zone: string | undefined) => ({
    day: new Intl.DateTimeFormat(locale, { weekday: 'long', month: 'long', day: 'numeric', timeZone: zone }),
    clock: new Intl.DateTimeFormat(locale, { hour: 'numeric', minute: '2-digit', timeZone: zone }),
    key: new Intl.DateTimeFormat('en-CA', { year: 'numeric', month: '2-digit', day: '2-digit', timeZone: zone }),
  })

  let f: ReturnType<typeof formats>
  try {
    f = formats(timeZone)
  } catch {
    f = formats(undefined)
  }

  const dot = ' ⋅ '
  if (f.key.format(start) === f.key.format(end)) {
    return `${f.day.format(start)}${dot}${f.clock.format(start)} – ${f.clock.format(end)}`
  }

  return `${f.day.format(start)}${dot}${f.clock.format(start)} – ${f.day.format(end)}${dot}${f.clock.format(end)}`
}
