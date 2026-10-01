// Relative, with the extension, so the Node test runner can load it (see lib/notes.ts).
import type { CurrentUser } from './api.ts'
import { can } from './permissions.ts'

/** Whether this account is offered AI briefs: Chat and briefs are on, and it may use AI chat. */
export function offersBriefs(me: CurrentUser): boolean {
  return me.briefsOn && can(me, 'UseAiChat')
}

/** One piece of a brief: plain text, or one citation with its ids and which of them are links. */
export type BriefPiece = { text: string } | { ids: { id: number; link: boolean }[] }

/** One citation as the model is asked to write it: `[#1234]` or `[#1234, #1240]`. */
const CITATION = /\[\s*#\d+(?:\s*,\s*#\d+)*\s*\]/g

/**
 * A brief cut into its text and its citations (AI chat design §14).
 *
 * Only ids in square brackets count, the way the model is asked to write them, so a number in a
 * sentence is never taken for one. An id is a link only when it is in `sources`, the ids that were
 * among the entries the model was given: one it made up stays plain text.
 */
export function briefPieces(text: string, sources: readonly number[]): BriefPiece[] {
  const known = new Set(sources)
  const pieces: BriefPiece[] = []
  let last = 0

  for (const match of text.matchAll(CITATION)) {
    const at = match.index ?? 0
    if (at > last) pieces.push({ text: text.slice(last, at) })

    const ids = [...match[0].matchAll(/#(\d+)/g)].map((m) => Number(m[1]))
    pieces.push({ ids: ids.map((id) => ({ id, link: known.has(id) })) })

    last = at + match[0].length
  }

  if (last < text.length) pieces.push({ text: text.slice(last) })

  return pieces
}
