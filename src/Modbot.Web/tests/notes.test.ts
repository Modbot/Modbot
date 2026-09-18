import test from 'node:test'
import assert from 'node:assert/strict'
import {
  MAX_NOTE_LENGTH,
  noteAuthor,
  noteProblem,
  notesBeforeActing,
  standingNotes,
} from '../src/lib/notes.ts'
import type { Note } from '../src/lib/api.ts'

/** One note, with only the fields a helper reads spelled out. */
const note = (fields: Partial<Note> & Pick<Note, 'id'>): Note =>
  ({
    writtenAt: '2026-03-10T12:00:00Z',
    text: 'Asked twice to stop.',
    subjectPlatform: 'VRChat',
    subjectId: 'usr_1',
    authorAccountId: null,
    authorName: 'Gunner24',
    imported: false,
    takenBack: false,
    takenBackAt: null,
    takenBackByName: null,
    canTakeBack: false,
    ...fields,
  }) as Note

test('an empty note is refused before it is sent', () => {
  assert.equal(noteProblem(''), 'A note needs something in it.')
  assert.equal(noteProblem('   \n  '), 'A note needs something in it.')
})

test('a note at the cap is fine and one past it is not', () => {
  assert.equal(noteProblem('x'.repeat(MAX_NOTE_LENGTH)), null)
  assert.match(noteProblem('x'.repeat(MAX_NOTE_LENGTH + 1)) ?? '', /too long/)
})

test('the cap counts the trimmed text, the same as the server', () => {
  assert.equal(noteProblem(`  ${'x'.repeat(MAX_NOTE_LENGTH)}  `), null)
})

test('a written note is fine', () => {
  assert.equal(noteProblem('Asked twice to stop.'), null)
})

test('a note with no name behind it says where it came from rather than inventing an author', () => {
  assert.equal(noteAuthor({ authorName: 'Gunner24', imported: false }), 'Gunner24')
  assert.equal(noteAuthor({ authorName: null, imported: true }), 'Imported')
  assert.equal(noteAuthor({ authorName: '', imported: true }), 'Imported')
  assert.equal(noteAuthor({ authorName: null, imported: false }), 'Unknown')
})

test('taken-back notes do not count as standing', () => {
  const notes = [note({ id: 1 }), note({ id: 2, takenBack: true }), note({ id: 3 })]

  assert.deepEqual(standingNotes(notes).map((n) => n.id), [1, 3])
})

test('the confirmation shows the newest standing notes, never a taken-back one', () => {
  const notes = [
    note({ id: 1, writtenAt: '2026-03-01T12:00:00Z' }),
    note({ id: 2, writtenAt: '2026-03-09T12:00:00Z', takenBack: true }),
    note({ id: 3, writtenAt: '2026-03-08T12:00:00Z' }),
    note({ id: 4, writtenAt: '2026-03-05T12:00:00Z' }),
    note({ id: 5, writtenAt: '2026-03-07T12:00:00Z' }),
  ]

  assert.deepEqual(notesBeforeActing(notes).map((n) => n.id), [3, 5, 4])
})

test('the confirmation shows nothing when every note was taken back', () => {
  const notes = [note({ id: 1, takenBack: true }), note({ id: 2, takenBack: true })]

  assert.deepEqual(notesBeforeActing(notes), [])
})

test('reading the notes for a confirmation does not reorder the caller’s list', () => {
  const notes = [note({ id: 1, writtenAt: '2026-03-01T12:00:00Z' }), note({ id: 2, writtenAt: '2026-03-09T12:00:00Z' })]

  notesBeforeActing(notes)

  assert.deepEqual(notes.map((n) => n.id), [1, 2])
})
