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
