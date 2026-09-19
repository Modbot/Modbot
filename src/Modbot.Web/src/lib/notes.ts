// Relative, with the extension, rather than the '@/' alias the rest of the app uses: the Node test
// runner resolves neither the alias nor an extensionless path, and this is the piece of the feature
// worth testing on its own.
import type { Note } from './api.ts'

/**
 * The longest a note may be, matching the server's own cap.
 *
 * Repeated here so the button can disable itself before a round trip, never so the browser can
 * decide: the server refuses a longer one whatever this says.
 */
export const MAX_NOTE_LENGTH = 2000

/**
 * What stops this note being written, or null when nothing does.
 *
 * The same two refusals the server makes, in the same words, so a moderator reads one sentence
 * rather than two different ones for the same problem.
 */
export function noteProblem(text: string): string | null {
  const trimmed = text.trim()

  if (trimmed.length === 0) return 'A note needs something in it.'
  if (trimmed.length > MAX_NOTE_LENGTH)
    return `That note is too long (at most ${MAX_NOTE_LENGTH} characters).`

  return null
}

/**
 * Who a note is from.
 *
 * A note carried in from another system often has no name behind it at all, and saying "Imported"
 * is the honest answer: nobody in this Modbot wrote it, and pretending otherwise would put a
 * stranger's words under a colleague's name.
 */
export function noteAuthor(note: Pick<Note, 'authorName' | 'imported'>): string {
  if (note.authorName && note.authorName.length > 0) return note.authorName
  return note.imported ? 'Imported' : 'Unknown'
}

/** The notes that still stand — the ones a moderator is being asked to weigh. */
export function standingNotes(notes: readonly Note[]): Note[] {
  return notes.filter((n) => !n.takenBack)
}

/**
 * The few notes worth putting in front of somebody about to act.
 *
 * Standing ones only, newest first, cut to `max`. A confirmation is not the notes tab: what
 * belongs there is the handful that might change the decision, and a taken-back note is by
 * definition one the group decided should not.
 */
export function notesBeforeActing(notes: readonly Note[], max = 3): Note[] {
  return standingNotes(notes)
    .slice()
    .sort((a, b) => Date.parse(b.writtenAt) - Date.parse(a.writtenAt) || b.id - a.id)
    .slice(0, max)
}
