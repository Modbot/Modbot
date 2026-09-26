import { useCallback, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { dateTime } from '@/components/charts'
import { Block, Empty, Note as Muted } from '@/components/subject/shared'
import { api, ApiError, type Note, type NoteList } from '@/lib/api'
import { MAX_NOTE_LENGTH, noteAuthor, noteProblem, notesBeforeActing } from '@/lib/notes'
import { useLoad } from '@/lib/useLoad'

/**
 * A person's notes: what moderators have written about them, and a box to write another.
 *
 * **Its own tab, not a corner of Logs.** The merged timeline already carries every note as a fact,
 * and that is exactly the problem — a note is somebody's deliberate sentence about this person,
 * and reading twelve of them means scrolling past four hundred joins, leaves and avatar changes.
 * A short list of the group's own remarks is a different question from "what has happened to
 * them", and it gets its own answer.
 *
 * **Taken-back notes stay on the list, marked.** A note that was written and withdrawn is not the
 * same thing as one nobody ever wrote, and a list that hid them would let somebody write a note,
 * take it back, and leave no trace on the screen where notes are read.
 *
 * The text is rendered as text. Not Markdown — the case file's written reason is Markdown because
 * it is a document; a note is a remark, and a remark that can draw a heading or an image is a
 * remark that can be made to look like something Modbot said.
 */
export function PersonNotes({
  subjectId,
  name,
  platform,
}: {
  subjectId: string
  /** The person's name, for the take-back confirmation. Falls back to the id. */
  name?: string | null
  /** `VRChat` or `Discord`. Which of the person's accounts these notes are filed under. */
  platform?: string
}) {
  // Bumped after a write or a take-back, which reloads the list from the server rather than
  // guessing at what it now says.
  const [version, setVersion] = useState(0)

  const load = useCallback(
    () => api.notes({ userId: subjectId, platform, limit: 100 }),
    [subjectId, platform],
  )
  const { data, error } = useLoad<NoteList>(load, version)

  const again = () => setVersion((n) => n + 1)

  return (
    <div className="flex min-h-0 flex-col">
      {error && <Empty tone="danger">{error}</Empty>}
      {!error && !data && <Empty>Loading…</Empty>}

      {data?.canWrite && <WriteNote subjectId={subjectId} platform={platform} onWritten={again} />}

      {data && data.notes.length === 0 && <Empty>No notes.</Empty>}

      {data && data.notes.length > 0 && (
        <ol className="flex shrink-0 flex-col border-b border-b-(length:--hairline)">
          {data.notes.map((note) => (
            <NoteRow key={note.id} note={note} about={name ?? subjectId} onTakenBack={again} />
          ))}
        </ol>
      )}
    </div>
  )
}

function NoteRow({ note, about, onTakenBack }: { note: Note; about: string; onTakenBack: () => void }) {
  const [confirming, setConfirming] = useState(false)

  return (
    <li
      className="border-t border-t-(length:--hairline) px-(--panel-pad) py-2 first:border-t-0"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{noteAuthor(note)}</span>
        {note.imported && <Badge variant="secondary">imported</Badge>}
        {note.takenBack && <Badge variant="secondary">taken back</Badge>}
        <span className="flex-1" />
        <span className="font-mono text-muted-foreground">{dateTime(note.writtenAt)}</span>
        {note.canTakeBack && (
          <Button size="xs" variant="ghost" onClick={() => setConfirming(true)}>
            Take back
          </Button>
        )}
      </div>

      <p className={`mt-1 break-words whitespace-pre-wrap ${note.takenBack ? 'text-muted-foreground' : ''}`}>
        {note.text}
      </p>

      {note.takenBack && note.takenBackAt && (
        <Muted>
          {note.takenBackByName ?? 'Somebody'} · <span className="font-mono">{dateTime(note.takenBackAt)}</span>
        </Muted>
      )}

      {note.canTakeBack && (
        <ConfirmDialog
          open={confirming}
          onOpenChange={setConfirming}
          title={`Take back the note about ${about}?`}
          subtitle={`${noteAuthor(note)} · ${dateTime(note.writtenAt)}`}
          action="Take back"
          failed="Could not take that note back."
          onConfirm={() => api.takeBackNote(note.id)}
          onDone={onTakenBack}
        />
      )}
    </li>
  )
}

function WriteNote({
  subjectId,
  platform,
  onWritten,
}: {
  subjectId: string
  platform?: string
  onWritten: () => void
}) {
  const [text, setText] = useState('')
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const stops = noteProblem(text)

  const send = () => {
    setSending(true)
    setProblem(null)

    api
      .writeNote({ userId: subjectId, platform, text: text.trim() })
      .then(() => {
        setText('')
        onWritten()
      })
      .catch((e: unknown) =>
        setProblem(e instanceof ApiError ? e.message : 'Could not write that note.'),
      )
      .finally(() => setSending(false))
  }

  return (
    <Block className="gap-2">
      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Note</span>
        <Textarea
          rows={3}
          maxLength={MAX_NOTE_LENGTH}
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
      </label>

      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" onClick={send} disabled={sending || stops !== null}>
          {sending ? 'Saving…' : 'Add note'}
        </Button>
        {problem && <Muted className="text-destructive">{problem}</Muted>}
      </div>
    </Block>
  )
}

/**
 * This person's standing notes, on the confirmation that opens before a kick, a ban or an unban.
 *
 * M4 §8.1: the moment a moderator is about to act is the moment the group's own remarks about
 * somebody are worth having, and the only moment at which showing them costs nothing. Read-only
 * and short — three at most — because this is a confirmation, not the notes tab.
 *
 * Draws nothing at all when there are none or when the caller may not read them. A confirmation
 * must not grow a row that says "no notes": the question on the screen is whether to ban somebody,
 * and an absence is not evidence.
 *
 * A read that fails does say so. Otherwise "the notes did not load" and "there are no notes" look
 * the same at the one moment the difference matters (UX review 2026-09-25, finding 14).
 */
export function NotesBeforeActing({ userId }: { userId: string }) {
  // A refusal is "may not read them", which draws nothing, so it is not counted as a failure.
  const load = useCallback(
    () =>
      api.notes({ userId, limit: 20 }).catch((e: unknown) => {
        if (e instanceof ApiError && e.status === 403) return null
        throw e
      }),
    [userId],
  )
  const { data, error } = useLoad<NoteList | null>(userId.length > 0 ? load : null)

  if (error) {
    return (
      <p className="text-warn" style={{ fontSize: 'var(--text-small)' }}>
        Could not read the notes
      </p>
    )
  }

  const showing = notesBeforeActing(data?.notes ?? [])

  if (showing.length === 0) return null

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">Notes</span>
      <ol className="flex flex-col border border-(length:--hairline)">
        {showing.map((note) => (
          <li key={note.id} className="border-t border-t-(length:--hairline) px-2 py-1 first:border-t-0">
            <div className="flex flex-wrap items-center gap-2 text-muted-foreground">
              <span>{noteAuthor(note)}</span>
              <span className="flex-1" />
              <span className="font-mono">{dateTime(note.writtenAt)}</span>
            </div>
            <p className="break-words whitespace-pre-wrap">{note.text}</p>
          </li>
        ))}
      </ol>
    </div>
  )
}
