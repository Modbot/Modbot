/**
 * A JSON document split into the pieces a reader tells apart by colour.
 *
 * Pure and total. Joining every piece's text back together gives the document again, character
 * for character: nothing the server sent can be dropped, reordered or reworded on the way to the
 * screen, and the caller renders each piece as text, so a record containing markup stays a record
 * containing markup.
 *
 * Text that is not JSON is not rejected either. Anything unrecognised comes back as one `other`
 * piece, so a truncated document, or one a person pasted in by hand, still draws.
 */

export type JsonPieceKind = 'key' | 'string' | 'number' | 'boolean' | 'null' | 'punctuation' | 'other'

export type JsonPiece = { kind: JsonPieceKind; text: string }

const SPACE = new Set([' ', '\t', '\n', '\r'])
const MARKS = new Set(['{', '}', '[', ']', ',', ':'])

/**
 * Numbers as JSON writes them. Leading `+`, a bare `.5` and a trailing `5.` are not JSON, and
 * fall through to `other` rather than being coloured as if they were.
 */
const NUMBER = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y

export function jsonPieces(text: string): JsonPiece[] {
  const pieces: JsonPiece[] = []
  let at = 0

  while (at < text.length) {
    const char = text[at]

    // Braces, brackets, commas, colons and the space between them are one piece, not one piece
    // each: a pretty-printed record is mostly indentation, and a span per space is a span wasted.
    if (SPACE.has(char) || MARKS.has(char)) {
      let end = at + 1
      while (end < text.length && (SPACE.has(text[end]) || MARKS.has(text[end]))) end++
      pieces.push({ kind: 'punctuation', text: text.slice(at, end) })
      at = end
      continue
    }

    if (char === '"') {
      const end = endOfString(text, at)
      pieces.push({ kind: nextMark(text, end) === ':' ? 'key' : 'string', text: text.slice(at, end) })
      at = end
      continue
    }

    if (text.startsWith('true', at) || text.startsWith('false', at)) {
      const word = text.startsWith('true', at) ? 'true' : 'false'
      if (wordEnds(text, at + word.length)) {
        pieces.push({ kind: 'boolean', text: word })
        at += word.length
        continue
      }
    }

    if (text.startsWith('null', at) && wordEnds(text, at + 4)) {
      pieces.push({ kind: 'null', text: 'null' })
      at += 4
      continue
    }

    NUMBER.lastIndex = at
    const number = NUMBER.exec(text)
    if (number && number.index === at && wordEnds(text, at + number[0].length)) {
      pieces.push({ kind: 'number', text: number[0] })
      at += number[0].length
      continue
    }

    let end = at + 1
    while (end < text.length && !SPACE.has(text[end]) && !MARKS.has(text[end]) && text[end] !== '"') end++
    pieces.push({ kind: 'other', text: text.slice(at, end) })
    at = end
  }

  return pieces
}

/** One past the closing quote, or the end of the document when the quote never comes. */
function endOfString(text: string, start: number): number {
  let at = start + 1

  while (at < text.length) {
    const char = text[at]
    // A backslash takes the next character with it, so `"a\\"` ends and `"a\""` does not.
    if (char === '\\') {
      at += 2
      continue
    }
    if (char === '"') return at + 1
    at++
  }

  return text.length
}

/** The next mark after a value, whitespace skipped: a string followed by `:` is a key. */
function nextMark(text: string, from: number): string | null {
  let at = from
  while (at < text.length && SPACE.has(text[at])) at++
  return at < text.length ? text[at] : null
}

/** Whether a bare word ends here, rather than running on into something that is not JSON. */
function wordEnds(text: string, at: number): boolean {
  if (at >= text.length) return true
  const char = text[at]
  return SPACE.has(char) || MARKS.has(char) || char === '"'
}
