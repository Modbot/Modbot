import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Outcome } from '@/components/settings/fields'
import { api, ApiError, type Brief, type CurrentUser } from '@/lib/api'
import { briefPieces } from '@/lib/briefs'
import { can } from '@/lib/permissions'
import { followLink } from '@/lib/router'
import { useBrief, type BriefState } from '@/components/subject/useBrief'

/** Where a brief's notes go: the account a person's notes are filed under. */
export type BriefNoteTarget = { userId: string; platform: string }

/** The control that asks for a brief: a small text button, like **Open in Audit log** beside it. */
export function BriefLink({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="rounded-sm text-muted-foreground hover:text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring"
      style={{ fontSize: 'var(--text-small)' }}
    >
      {label}
    </button>
  )
}

/**
 * The button that asks for an AI brief, with its dialog, for a place that does not remount while
 * the dialog is open (the instance popup's Activity tab). The person popup holds `useBrief` itself
 * and draws `BriefLink` and `BriefDialog` apart.
 */
export function BriefButton({
  label,
  ask,
  me,
}: {
  label: string
  ask: (timeZone: string) => Promise<Brief>
  me: CurrentUser
}) {
  const state = useBrief(ask)

  return (
    <>
      <BriefLink label={label} onClick={state.start} />
      <BriefDialog state={state} me={me} />
    </>
  )
}

/**
 * One AI brief, in a dialog (AI chat design §14).
 *
 * **What is shown is checkable.** The text is drawn as text, never as Markdown, like a note. Each
 * line ends with the ids of the entries it rests on, and only ids that were among the entries sent
 * are links, to the audit log at that entry; an id the model made up stays plain. The line under
 * the title says what the brief was built from.
 */
export function BriefDialog({
  state,
  me,
  note,
  onSaved,
}: {
  state: BriefState
  me: CurrentUser
  /** Where "Save as note" files it. Left out, the brief can be copied but not saved. */
  note?: BriefNoteTarget | null
  onSaved?: () => void
}) {
  const { open, setOpen, brief, problem } = state

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogContent
        title="AI brief"
        subtitle={brief?.builtFrom ?? undefined}
        foot={brief?.text ? <BriefFoot brief={brief} me={me} note={note} onSaved={onSaved} /> : undefined}
      >
        {problem ? (
          <p className="text-destructive">{problem}</p>
        ) : !brief ? (
          <p className="text-muted-foreground">Writing…</p>
        ) : brief.text === null ? (
          <p className="text-muted-foreground">Nothing recorded.</p>
        ) : (
          <BriefText text={brief.text} sources={brief.sources} onFollow={() => setOpen(false)} />
        )}
      </DialogContent>
    </Dialog>
  )
}

/** Copy, and Save as note where there is somewhere to file it and the reader may write notes. */
function BriefFoot({
  brief,
  me,
  note,
  onSaved,
}: {
  brief: Brief
  me: CurrentUser
  note?: BriefNoteTarget | null
  onSaved?: () => void
}) {
  const [copied, setCopied] = useState(false)
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  // The source line goes with the words wherever they go, so a copy or a note says what it is.
  const whole = brief.builtFrom ? `${brief.text}\n\n${brief.builtFrom}` : (brief.text ?? '')

  const copy = () => {
    void navigator.clipboard?.writeText(whole).then(() => {
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1500)
    })
  }

  const save = () => {
    if (!note || !brief.callId) return
    setSaving(true)
    setProblem(null)

    api
      .writeNote({ userId: note.userId, platform: note.platform, text: whole, briefCallId: brief.callId })
      .then(() => {
        setSaved(true)
        onSaved?.()
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.'))
      .finally(() => setSaving(false))
  }

  const canSave = note && brief.callId && can(me, 'WriteNotes')

  return (
    <DialogFoot>
      <Outcome tone="problem">{problem}</Outcome>
      <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
      <Button size="sm" variant="outline" onClick={copy}>
        {copied ? 'Copied' : 'Copy'}
      </Button>
      {canSave && (
        <Button size="sm" disabled={saving || saved} onClick={save}>
          {saving ? 'Saving…' : 'Save as note'}
        </Button>
      )}
    </DialogFoot>
  )
}


/** The brief as text, with each cited id that was among the entries a link to that entry. */
function BriefText({
  text,
  sources,
  onFollow,
}: {
  text: string
  sources: readonly number[]
  /** Called when a link is followed, so the dialog does not stay over the page it leads to. */
  onFollow?: () => void
}) {
  return (
    <p className="break-words whitespace-pre-wrap">
      {briefPieces(text, sources).map((piece, at) =>
        'text' in piece ? (
          piece.text
        ) : (
          <span key={at} className="font-mono text-muted-foreground">
            [
            {piece.ids.map(({ id, link }, i) => {
              const href = `/audit?fact=${id}`
              return (
                <span key={`${id}-${i}`}>
                  {i > 0 && ', '}
                  {link ? (
                    <a
                      href={href}
                      onClick={(e) => {
                        followLink(href)(e)
                        if (e.defaultPrevented) onFollow?.()
                      }}
                      className="text-link hover:underline"
                    >
                      #{id}
                    </a>
                  ) : (
                    `#${id}`
                  )}
                </span>
              )
            })}
            ]
          </span>
        ),
      )}
    </p>
  )
}
