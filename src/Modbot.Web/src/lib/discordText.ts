// The little of Discord's own text the calendar's preview needs to draw a card as Discord would:
// timestamps (`<t:1790000000:F>`), which Discord shows in each reader's own time, links
// (`[name](https://…)`), and the backslashes that keep a world's name from being read as formatting.
// Pure, so the Node test runner can load it.

export type DiscordPiece =
  | { kind: 'text'; text: string }
  | { kind: 'time'; at: Date; style: string }
  | { kind: 'link'; label: string; url: string }

const PIECES = /<t:(-?\d+)(?::([tTdDfFR]))?>|\[((?:\\.|[^\]\\])*)\]\((https?:\/\/[^)\s]+)\)/g

/** Takes off the backslashes Discord reads as "this character is not formatting". */
export function unescapeDiscord(text: string): string {
  return text.replace(/\\([^A-Za-z0-9\s])/g, '$1')
}

/** Splits Discord's text into plain text, timestamps and links, in order. */
export function discordPieces(text: string): DiscordPiece[] {
  const pieces: DiscordPiece[] = []
  let at = 0

  for (const match of text.matchAll(PIECES)) {
    const start = match.index ?? 0
    if (start > at) pieces.push({ kind: 'text', text: unescapeDiscord(text.slice(at, start)) })

    if (match[1] !== undefined) pieces.push({ kind: 'time', at: new Date(Number(match[1]) * 1000), style: match[2] ?? 'f' })
    else pieces.push({ kind: 'link', label: unescapeDiscord(match[3] ?? ''), url: match[4] ?? '' })

    at = start + match[0].length
  }

  if (at < text.length) pieces.push({ kind: 'text', text: unescapeDiscord(text.slice(at)) })
  return pieces
}

/**
 * A timestamp the way Discord writes it for its style letter, in the viewer's own time and
 * language: `t` 8:00 PM, `T` 8:00:00 PM, `d` 10/2/2026, `D` October 2, 2026, `f` October 2, 2026
 * 8:00 PM, `F` Friday, October 2, 2026 8:00 PM, `R` in 2 days.
 */
export function discordTime(at: Date, style: string, now: Date, locale?: string): string {
  const format = (options: Intl.DateTimeFormatOptions) => new Intl.DateTimeFormat(locale, options).format(at)

  switch (style) {
    case 't':
      return format({ hour: 'numeric', minute: '2-digit' })
    case 'T':
      return format({ hour: 'numeric', minute: '2-digit', second: '2-digit' })
    case 'd':
      return format({ year: 'numeric', month: 'numeric', day: 'numeric' })
    case 'D':
      return format({ year: 'numeric', month: 'long', day: 'numeric' })
    case 'F':
      return format({ weekday: 'long', year: 'numeric', month: 'long', day: 'numeric', hour: 'numeric', minute: '2-digit' })
    case 'R':
      return relative(at, now, locale)
    default:
      return format({ year: 'numeric', month: 'long', day: 'numeric', hour: 'numeric', minute: '2-digit' })
  }
}

function relative(at: Date, now: Date, locale?: string): string {
  const seconds = Math.round((at.getTime() - now.getTime()) / 1000)
  const steps: [Intl.RelativeTimeFormatUnit, number][] = [
    ['year', 365 * 24 * 3600],
    ['month', 30 * 24 * 3600],
    ['day', 24 * 3600],
    ['hour', 3600],
    ['minute', 60],
  ]
  const words = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })

  for (const [unit, size] of steps) {
    if (Math.abs(seconds) >= size) return words.format(Math.round(seconds / size), unit)
  }

  return words.format(seconds, 'second')
}
