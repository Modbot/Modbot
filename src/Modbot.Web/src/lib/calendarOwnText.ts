// A service's own title and description, edited in place on its card (calendar design §2.1, 2b). Nothing
// here talks to the server or the page, so the Node test runner can load this file as it is.
import type { CalendarEventInput } from './calendar.ts'
import { BLUESKY_DESCRIPTION_LIMIT, counted, countedGraphemes, DESCRIPTION_LIMIT, TITLE_LIMIT, type CalendarServiceTab } from './calendarPlaces.ts'

/** The fields of the form that hold a service's own words. */
export type OwnKey =
  | 'vrChatTitle'
  | 'vrChatDescription'
  | 'discordTitle'
  | 'discordDescription'
  | 'googleTitle'
  | 'googleDescription'
  | 'blueskyTitle'
  | 'blueskyDescription'

/** The own fields each service's card edits. The Discord event and the Discord post share one pair. */
export const OWN_TEXT: Record<CalendarServiceTab, { title: OwnKey; description: OwnKey }> = {
  vrchat: { title: 'vrChatTitle', description: 'vrChatDescription' },
  discordEvent: { title: 'discordTitle', description: 'discordDescription' },
  channelPost: { title: 'discordTitle', description: 'discordDescription' },
  googleCalendar: { title: 'googleTitle', description: 'googleDescription' },
  bluesky: { title: 'blueskyTitle', description: 'blueskyDescription' },
}

/** Every own field, in the form's order. */
export const OWN_KEYS: readonly OwnKey[] = [...new Set(Object.values(OWN_TEXT).flatMap((pair) => [pair.title, pair.description]))]

const isTitle = (key: OwnKey) => key.endsWith('Title')

/** The own fields that hold words: what the form starts with, and what "Use event text" empties. */
export function ownKeysWithText(input: Pick<CalendarEventInput, OwnKey>): Set<OwnKey> {
  return new Set(OWN_KEYS.filter((key) => (input[key] ?? '').trim() !== ''))
}

/**
 * The words a card's field holds: the service's own while it has some or is being edited (so a field
 * emptied to be retyped stays empty), else the event's.
 */
export function textShown(input: Pick<CalendarEventInput, OwnKey | 'title' | 'description'>, key: OwnKey, edited: ReadonlySet<OwnKey>): string {
  const own = input[key] ?? ''
  if (edited.has(key) || own.trim() !== '') return own
  return isTitle(key) ? input.title : input.description
}

/** The counter beside a card's field, in the service's own limit; red only for words the service owns. */
export function countOf(key: OwnKey, text: string, own: boolean): { label: string; over: boolean } {
  const count =
    key === 'blueskyDescription'
      ? countedGraphemes(text, BLUESKY_DESCRIPTION_LIMIT)
      : counted(text, isTitle(key) ? TITLE_LIMIT : DESCRIPTION_LIMIT)
  return { label: count.label, over: count.over && own }
}

/**
 * What the server's drawing of a text holds around the words it was given (Discord's category and join
 * lines, the world after Google's description): the stretch before them and the stretch after. Null
 * when the words are not found whole in it (a link filled in, the text cut), when nothing extra is drawn.
 */
export function around(drawn: string | null | undefined, words: string): { before: string; after: string } | null {
  const own = words.trim()
  if (!drawn || own === '') return null

  const at = drawn.indexOf(own)
  if (at < 0) return null

  const before = drawn.slice(0, at).trim()
  const after = drawn.slice(at + own.length).trim()
  return before === '' && after === '' ? null : { before, after }
}

/**
 * The lines a Bluesky post holds around its title and description: the time and the world before the
 * description, the link after it. The title is its first line and is left out, a box of its own. With no
 * description, everything after the title is "before". Empty when the description is not found whole in
 * the post (cut, or a link filled in).
 */
export function postAround(text: string, title: string, description: string): { before: string; after: string } {
  const lines = text.split('\n')
  const first = title.trim().replace(/\s+/g, ' ')
  const rest = (first !== '' && lines[0] === first ? lines.slice(1) : lines).join('\n').trim()

  if (description.trim() === '') return { before: rest, after: '' }
  return around(rest, description) ?? { before: '', after: '' }
}
