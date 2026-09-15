import type { ChatReference } from '@/lib/api'
import { go } from '@/lib/router'
import { openDiscordMessage, openSubject, type SubjectKind } from '@/lib/subject'

/** The kinds that open a popup. Everything else opens a page. */
const POPUPS = new Set<ChatReference['kind']>(['person', 'world', 'instance', 'discord-person'])

export function opensPopup(kind: ChatReference['kind']): boolean {
  return POPUPS.has(kind)
}

/**
 * Opens whatever a tool returned, where it lives.
 *
 * Nothing here is built from the model's words: every one of these came back from a tool with its
 * own id, so a chip always lands on a real row.
 */
export function openReference(reference: ChatReference): void {
  switch (reference.kind) {
    case 'fact':
      go(`/audit?fact=${encodeURIComponent(reference.id)}`)
      return

    case 'case':
      go(`/cases/${encodeURIComponent(reference.id)}`)
      return

    case 'event':
      go(`/calendar?event=${encodeURIComponent(reference.id)}`)
      return

    case 'message':
      if (reference.author) openDiscordMessage(reference.author, reference.id)
      return

    default:
      openSubject({ kind: reference.kind as SubjectKind, id: reference.id })
  }
}

/** The same thing twice is one source, whichever lookup found it. */
export function uniqueSources(references: readonly ChatReference[]): ChatReference[] {
  const seen = new Set<string>()
  const sources: ChatReference[] = []

  for (const reference of references) {
    const key = `${reference.kind}:${reference.id}`
    if (reference.id && !seen.has(key) && (reference.kind !== 'message' || reference.author)) {
      seen.add(key)
      sources.push(reference)
    }
  }

  return sources
}
