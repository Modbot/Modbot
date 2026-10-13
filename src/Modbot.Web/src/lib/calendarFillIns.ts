// Nothing here talks to the server or the page, so the Node test runner can load this file as it is.
import type { CalendarTemplateFields } from './calendarTemplates.ts'

/**
 * Fill-ins: named blanks in a template's text, such as Host or What (calendar templates plan, step 3).
 *
 * A blank is stored as its name between double curly brackets, `{{Host}}`, anywhere in a title or a
 * description. A person never sees that: the template editor draws a blank as a box carrying its
 * name, and using a template asks for each name in a box of its own.
 *
 * Names are matched without regard to capitals or extra spaces, so `{{host}}` and `{{ Host }}` are
 * the same blank, and the spelling first met is the one shown. A later step can give some names a
 * meaning of their own (automatic links) by checking them in `splitFillIns`'s callers; the stored
 * form needs no change for that.
 */

/** The longest a fill-in's name may be. */
export const MAX_FILL_IN_NAME = 40

/** The template's texts a fill-in may stand in, in the order the form shows them. */
export const FILL_IN_TEXTS = [
  'title',
  'description',
  'vrChatTitle',
  'vrChatDescription',
  'discordTitle',
  'discordDescription',
  'googleTitle',
  'googleDescription',
] as const

type TextKey = (typeof FILL_IN_TEXTS)[number]

export type Piece = { kind: 'text'; text: string } | { kind: 'fillIn'; name: string }

const MARKER = /\{\{([^{}\r\n]{1,80})\}\}/g

/** What a name is compared by: trimmed, single spaces, lower case. */
export function fillInKey(name: string): string {
  return name.trim().replace(/\s+/g, ' ').toLowerCase()
}

/** A name as typed, made fit to keep: no brackets or line breaks, single spaces, at most 40 characters. */
export function cleanFillInName(raw: string): string {
  return raw
    .replace(/[{}\r\n]/g, ' ')
    .trim()
    .replace(/\s+/g, ' ')
    .slice(0, MAX_FILL_IN_NAME)
    .trim()
}

/** The stored form of a blank. */
export function fillInMarker(name: string): string {
  return `{{${cleanFillInName(name)}}}`
}

/** A text as its plain parts and its blanks, in order. A marker with no name in it stays plain text. */
export function splitFillIns(text: string): Piece[] {
  const pieces: Piece[] = []
  let from = 0

  for (const match of text.matchAll(MARKER)) {
    const name = cleanFillInName(match[1])
    if (name === '') continue

    if (match.index > from) pieces.push({ kind: 'text', text: text.slice(from, match.index) })
    pieces.push({ kind: 'fillIn', name })
    from = match.index + match[0].length
  }

  if (from < text.length) pieces.push({ kind: 'text', text: text.slice(from) })
  return pieces
}

/** The distinct blanks in some texts, in the order first met, each by the spelling first met. */
export function fillInsIn(texts: ReadonlyArray<string | null | undefined>): string[] {
  const seen = new Map<string, string>()

  for (const text of texts) {
    for (const piece of splitFillIns(text ?? '')) {
      if (piece.kind === 'fillIn' && !seen.has(fillInKey(piece.name))) seen.set(fillInKey(piece.name), piece.name)
    }
  }

  return [...seen.values()]
}

/** The texts of a template (or of anything with the same text fields), in the form's order. */
export function textsOf(fields: Pick<CalendarTemplateFields, TextKey>): Array<string | null> {
  return FILL_IN_TEXTS.map((key) => fields[key])
}

/** The blanks a template's texts hold. */
export function fillInsOfTemplate(fields: Pick<CalendarTemplateFields, TextKey>): string[] {
  return fillInsIn(textsOf(fields))
}

/** One text with every blank replaced by its value, or by nothing when it has none. Values are not read for blanks again. */
export function fillInText(text: string, values: Readonly<Record<string, string>>): string {
  return splitFillIns(text)
    .map((piece) => (piece.kind === 'text' ? piece.text : (values[fillInKey(piece.name)] ?? '').trim()))
    .join('')
}

/** The values typed, by name as shown, as the lookup `fillInText` reads. */
export function valuesByKey(names: readonly string[], typed: Readonly<Record<string, string>>): Record<string, string> {
  return Object.fromEntries(names.map((name) => [fillInKey(name), typed[name] ?? '']))
}

/** The blanks whose value is still empty, in order. */
export function unfilled(names: readonly string[], typed: Readonly<Record<string, string>>): string[] {
  return names.filter((name) => (typed[name] ?? '').trim() === '')
}

/** A template's fields with every blank in every text replaced, the main and each site's own alike. */
export function fillInFields(fields: CalendarTemplateFields, typed: Readonly<Record<string, string>>): CalendarTemplateFields {
  const values = valuesByKey(fillInsOfTemplate(fields), typed)
  const filled: CalendarTemplateFields = { ...fields }

  for (const key of FILL_IN_TEXTS) {
    const text = fields[key]
    if (text !== null && text !== undefined) filled[key] = fillInText(text, values)
  }

  return filled
}
