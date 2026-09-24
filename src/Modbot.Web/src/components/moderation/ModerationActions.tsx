import { useEffect, useMemo, useState } from 'react'
import { Popover } from 'radix-ui'
import { MoreHorizontal } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { ReasonButtons } from '@/components/CaseFileForm'
import { NotesBeforeActing, Textarea } from '@/components/subject/PersonNotes'
import {
  api,
  ApiError,
  type BanReasonView,
  type CurrentUser,
  type ModerationActionName,
  type ModerationActionResult,
} from '@/lib/api'
import {
  actionsFor,
  confirmTitle,
  reasonRequired,
  resultText,
  type PersonStanding,
} from '@/lib/moderationActions'

const SEND: Record<ModerationActionName, (body: Parameters<typeof api.kickPerson>[0]) => Promise<ModerationActionResult>> = {
  kick: api.kickPerson,
  ban: api.banPerson,
  unban: api.unbanPerson,
}

/**
 * Kick, ban and unban, wherever a person is listed.
 *
 * Every press opens a confirmation naming the person and the action, because these are the only
 * controls in Modbot that change something in VRChat and the one that is costly to get wrong is
 * one click away from the one that is not (M4 §5, foundation §5.8.1).
 *
 * Two things stop one confirmation acting twice. The button disables itself while the request is
 * in flight, which handles the impatient second click; and the key made when the dialog opens
 * travels with the request, so the server refuses a second one even if the browser retries behind
 * the page's back. The second is the guarantee — the first is only a courtesy.
 */
export function ModerationActions({
  me,
  person,
  name,
  onDone,
  size = 'sm',
  layout = 'row',
}: {
  me: CurrentUser
  person: PersonStanding
  /** The person's display name, when one is known. Falls back to the id.  */
  name?: string | null
  /** Called after VRChat accepted, so the page that offered this can read the change back. */
  onDone?: (result: ModerationActionResult) => void
  size?: 'sm' | 'xs'
  /** `menu` puts the buttons behind a row control, for a table that has no room for them. */
  layout?: 'row' | 'menu'
}) {
  const [open, setOpen] = useState<ModerationActionName | null>(null)
  const offered = actionsFor(me, person)

  if (offered.length === 0) return null

  const buttons = offered.map((o) => (
    <Button
      key={o.action}
      size={size}
      // Red, not an outline. These are the buttons that take somebody out of the group, and a
      // row where Kick and Unban look the same is a row where the wrong one gets pressed. The
      // confirmation is what stops a misclick; the colour is what stops the reach.
      variant={o.destructive ? 'destructive' : 'ghost'}
      onClick={() => setOpen(o.action)}
    >
      {o.label}
    </Button>
  ))

  return (
    <>
      {layout === 'menu' ? (
        // The menu only wraps the triggers. The dialog is a sibling of it and a child of this
        // component, so dismissing the menu on the click that opens the dialog cannot take the
        // dialog's state down with it.
        <Popover.Root>
          <Popover.Trigger asChild>
            <Button size="icon-xs" variant="ghost" aria-label={`Actions for ${name ?? person.userId}`}>
              <MoreHorizontal />
            </Button>
          </Popover.Trigger>
          <Popover.Portal>
            <Popover.Content
              align="end"
              sideOffset={4}
              className="z-50 flex flex-col gap-1 rounded-sm border bg-popover p-1 shadow-sm"
              style={{ borderWidth: 'var(--hairline)' }}
            >
              {buttons}
            </Popover.Content>
          </Popover.Portal>
        </Popover.Root>
      ) : (
        <div className="flex flex-wrap items-center gap-1.5">{buttons}</div>
      )}

      <Dialog open={open !== null} onOpenChange={(next) => !next && setOpen(null)}>
        {open !== null && (
          <ConfirmAction
            action={open}
            userId={person.userId ?? ''}
            name={name ?? person.userId ?? ''}
            isMember={person.isMember}
            onClose={() => setOpen(null)}
            onDone={onDone}
          />
        )}
      </Dialog>
    </>
  )
}

function ConfirmAction({
  action,
  userId,
  name,
  isMember,
  onClose,
  onDone,
}: {
  action: ModerationActionName
  userId: string
  name: string
  /** What the member list last said. Undefined or null: Modbot has not read it. */
  isMember?: boolean | null
  onClose: () => void
  onDone?: (result: ModerationActionResult) => void
}) {
  // Made once, when this confirmation opens, and kept for every press of its button. A second
  // press, a retry after a timeout, or a reload that resends all land on the same key and
  // therefore on the same action (M4 §4.3).
  const key = useMemo(() => crypto.randomUUID(), [])

  const [reasons, setReasons] = useState<BanReasonView[] | null>(null)
  const [picked, setPicked] = useState<string[]>([])
  const [note, setNote] = useState('')
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [result, setResult] = useState<ModerationActionResult | null>(null)

  useEffect(() => {
    api
      .banReasons()
      .then((list) => setReasons(list.reasons.filter((r) => r.isActive)))
      .catch(() => setReasons([]))
  }, [])

  // The server decides both of these too; the browser only knows whether a ban is in front of it.
  // A group that requires a reason on kicks gets the server's 400 and the message with it.
  const needsReason = reasonRequired(action, false)
  const needsNote =
    action === 'ban' && (reasons ?? []).some((r) => picked.includes(r.id) && r.needsWrittenReason)

  const send = () => {
    setSending(true)
    setProblem(null)

    SEND[action]({ userId, key, reasonIds: picked, note })
      .then((r) => {
        setResult(r)
        if (r.done) onDone?.(r)
      })
      .catch((e: unknown) =>
        setProblem(e instanceof ApiError ? e.message : 'Modbot could not reach VRChat.'),
      )
      .finally(() => setSending(false))
  }

  return (
    <DialogContent
      title={confirmTitle(action, name, isMember)}
      subtitle={<span className="font-mono" title={userId}>{userId}</span>}
      className="max-w-[460px]"
    >
      <div className="flex flex-col gap-3">
        {result === null && (
          <>
            {/*
              What the group has already written down about this person, before the button is
              pressed. M4 §8.1: this is the moment that information is worth having and the only
              moment at which showing it costs nothing. Nothing is drawn when there are none.
            */}
            <NotesBeforeActing userId={userId} />

            {reasons === null ? (
              <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                Loading the reasons…
              </p>
            ) : (
              <ReasonButtons reasons={reasons} picked={picked} onChange={setPicked} />
            )}

            <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
              <span className="text-muted-foreground">Note {needsNote ? '(required)' : '(optional)'}</span>
              <Textarea
                rows={3}
                value={note}
                onChange={(e) => setNote(e.target.value)}
              />
            </label>

            <div className="flex flex-wrap items-center justify-end gap-2">
              <Button size="sm" variant="outline" onClick={onClose} disabled={sending}>
                Cancel
              </Button>
              <Button
                size="sm"
                variant={action === 'unban' ? 'default' : 'destructive'}
                onClick={send}
                disabled={sending || (needsReason && picked.length === 0) || (needsNote && note.trim().length === 0)}
              >
                {sending ? 'Sending…' : confirmLabel(action)}
              </Button>
            </div>

            {problem && (
              <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
                {problem}
              </p>
            )}
          </>
        )}

        {result !== null && (
          <>
            <p className={result.done ? '' : 'text-destructive'}>{resultText(action, result)}</p>

            <div className="flex justify-end">
              <Button size="sm" onClick={onClose}>
                Close
              </Button>
            </div>
          </>
        )}
      </div>
    </DialogContent>
  )
}

function confirmLabel(action: ModerationActionName): string {
  switch (action) {
    case 'kick':
      return 'Kick'
    case 'ban':
      return 'Ban'
    case 'unban':
      return 'Unban'
  }
}
