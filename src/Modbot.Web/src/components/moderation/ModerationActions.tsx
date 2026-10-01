import { useEffect, useMemo, useState } from 'react'
import { Popover } from 'radix-ui'
import { MoreHorizontal } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { ReasonButtons, WrittenReasonBox } from '@/components/CaseFileForm'
import { AddFileButton, BanFileList } from '@/components/moderation/BanFiles'
import { useBanFiles } from '@/components/moderation/useBanFiles'
import { NotesBeforeActing } from '@/components/subject/PersonNotes'
import { Outcome } from '@/components/settings/fields'
import { Textarea } from '@/components/ui/textarea'
import { VRChatPermissionMissing } from '@/components/VRChatPermissionMissing'
import {
  api,
  ApiError,
  type BanReasonList,
  type CaseFileSummary,
  type CurrentUser,
  type EvidenceDelivery,
  type ModerationActionName,
  type ModerationActionResult,
} from '@/lib/api'
import { formatDay } from '@/lib/format'
import {
  actionsFor,
  confirmTitle,
  discordText,
  noPermissionText,
  noteRequired,
  reasonRequired,
  reasonsFor,
  resultText,
  type PersonStanding,
} from '@/lib/moderationActions'
import { can } from '@/lib/permissions'
import { openCase } from '@/lib/subject'

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
              className="z-50 flex flex-col gap-1 rounded-sm border-(length:--hairline) bg-popover p-1 shadow-sm"
            >
              {buttons}
            </Popover.Content>
          </Popover.Portal>
        </Popover.Root>
      ) : (
        <div className="flex flex-wrap items-center gap-1.5">{buttons}</div>
      )}

      <ModerationDialog me={me} action={open} person={person} name={name} onClose={() => setOpen(null)} onDone={onDone} />
    </>
  )
}

/**
 * The confirmation for one kick, ban or unban, open while `action` is set.
 *
 * On its own as well as behind the buttons, because the palette's "Ban X…" opens the person popup
 * with it already open: the moderator still reads the same sentence and presses the same Confirm.
 */
export function ModerationDialog({
  me,
  action,
  person,
  name,
  onClose,
  onDone,
}: {
  me: CurrentUser
  action: ModerationActionName | null
  person: PersonStanding
  name?: string | null
  onClose: () => void
  onDone?: (result: ModerationActionResult) => void
}) {
  return (
    <Dialog open={action !== null} onOpenChange={(next) => !next && onClose()}>
      {action !== null && (
        <ConfirmAction
          me={me}
          action={action}
          userId={person.userId ?? ''}
          name={name ?? person.userId ?? ''}
          isMember={person.isMember}
          onClose={onClose}
          onDone={onDone}
        />
      )}
    </Dialog>
  )
}

function ConfirmAction({
  me,
  action,
  userId,
  name,
  isMember,
  onClose,
  onDone,
}: {
  me: CurrentUser
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

  const [list, setList] = useState<BanReasonList | null>(null)
  const [picked, setPicked] = useState<string[]>([])
  const [note, setNote] = useState('')
  const [sending, setSending] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [result, setResult] = useState<ModerationActionResult | null>(null)

  // A ban is the write-up: its note is the case file's "What happened", and screenshots picked
  // here are attached to the case file the ban writes (UX review finding 2). Kick and unban write
  // no case file, so they have nothing to attach to.
  const writesCaseFile = action === 'ban'
  const [delivery, setDelivery] = useState<EvidenceDelivery | null>(null)
  const files = useBanFiles()

  useEffect(() => {
    api
      .banReasons()
      .then(setList)
      .catch(() => setList({ reasons: [], canEdit: false, reasonAlwaysRequired: false }))
  }, [])

  // Only the reasons marked for this action: an unban asks why the ban is lifted, not why it began.
  const reasons = useMemo(() => (list ? reasonsFor(list.reasons, action) : null), [list, action])

  useEffect(() => {
    if (!writesCaseFile || !can(me, 'UploadEvidence')) return
    api
      .evidenceDelivery()
      .then(setDelivery)
      .catch(() => setDelivery(null))
  }, [writesCaseFile, me])

  // A store that is not set up has nothing to offer; one that is refusing says why beside the button.
  const attachable = delivery?.configured ? delivery : null

  // The server decides both of these too. The list says whether the group requires a reason
  // beyond bans, so the button waits for one rather than sending and being refused.
  const needsReason = reasonRequired(action, list?.reasonAlwaysRequired ?? false)
  const needsNote = noteRequired(reasons ?? [], picked)

  const send = () => {
    setSending(true)
    setProblem(null)

    SEND[action]({ userId, key, reasonIds: picked, note })
      .then((r) => {
        setResult(r)
        if (writesCaseFile)
          files.settle(
            r.done && r.caseId
              ? { caseId: r.caseId }
              : { message: r.done ? 'Not attached: no case file was written.' : 'Not attached: nothing was banned.' },
          )
        if (r.done) onDone?.(r)
      })
      .catch((e: unknown) =>
        setProblem(
          e instanceof ApiError
            ? e.status === 403
              ? noPermissionText(action)
              : e.message
            : 'Modbot could not reach VRChat.',
        ),
      )
      .finally(() => setSending(false))
  }

  // The Discord half of a ban or unban, said under the VRChat result. It never replaces it: the
  // VRChat action stands whatever Discord answered.
  const discord = result ? discordText(action, result) : null

  return (
    <DialogContent
      title={confirmTitle(action, name, isMember)}
      className="max-w-[460px]"
      foot={
        result === null ? (
          <DialogFoot>
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
          </DialogFoot>
        ) : (
          <DialogFoot>
            {/* A ban writes a case file and the server hands back its id. Offered here, so another
                screenshot can be attached without searching for the person all over again, and
                the case file opens over the page rather than instead of it. */}
            {result.done && result.caseId && attachable && (
              <AddFileButton
                delivery={attachable}
                label="Add another screenshot"
                onPick={(file) => files.add(file, attachable)}
              />
            )}
            {result.done && result.caseId && (
              <Button
                size="sm"
                variant="outline"
                onClick={() => {
                  const caseId = result.caseId!
                  onClose()
                  openCase(caseId)
                }}
              >
                Open the case file
              </Button>
            )}
            <Button size="sm" onClick={onClose}>
              {writesCaseFile && result.done ? 'Done' : 'Close'}
            </Button>
          </DialogFoot>
        )
      }
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

            {/* The write-up of the ban being lifted, so the reasons it was imposed for are in front
                of whoever lifts it (M4 §9). Nothing is drawn when there is none. */}
            {action === 'unban' && can(me, 'ViewProfile') && (
              <CaseBeforeUnban
                userId={userId}
                onOpen={(caseId) => {
                  onClose()
                  openCase(caseId)
                }}
              />
            )}

            {reasons === null ? (
              <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                Loading the reasons…
              </p>
            ) : action === 'unban' ? (
              <div className="flex flex-col gap-1">
                <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  Why lift the ban?{needsReason ? ' (required)' : ''}
                </span>
                <ReasonButtons reasons={reasons} picked={picked} onChange={setPicked} />
              </div>
            ) : (
              <ReasonButtons reasons={reasons} picked={picked} onChange={setPicked} />
            )}

            {writesCaseFile ? (
              <WrittenReasonBox reasons={reasons ?? []} picked={picked} value={note} onChange={setNote} rows={3} />
            ) : (
              <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
                <span className="text-muted-foreground">Note {needsNote ? '(required)' : '(optional)'}</span>
                <Textarea
                  rows={3}
                  value={note}
                  onChange={(e) => setNote(e.target.value)}
                />
              </label>
            )}

            {attachable && (
              <div className="flex flex-col gap-2">
                <BanFileList items={files.items} onRemove={sending ? undefined : files.remove} />
                <div>
                  <AddFileButton
                    delivery={attachable}
                    label="Add screenshot or video"
                    onPick={(file) => files.add(file, attachable)}
                  />
                </div>
                {!attachable.uploadsAllowed && (
                  <p className="text-warn" style={{ fontSize: 'var(--text-small)' }}>
                    Uploads are refused right now: {attachable.storeExplanation}
                  </p>
                )}
              </div>
            )}

            <Outcome tone="problem">{problem}</Outcome>
          </>
        )}

        {result !== null && (
          <>
            {!result.done && result.missingGroupPermission ? (
              <VRChatPermissionMissing missing={result.missingGroupPermission} className="text-destructive" />
            ) : (
              <p className={result.done ? '' : 'text-destructive'}>
                {resultText(action, result)}
                {writesCaseFile && result.done && result.caseId ? ' Case file written.' : ''}
                {action === 'unban' && result.done && result.caseId ? ' Case file updated.' : ''}
              </p>
            )}

            {result.caseFileError && <p className="text-destructive">{result.caseFileError}</p>}

            {discord && <p className={discord.failed ? 'text-destructive' : ''}>{discord.text}</p>}

            {writesCaseFile && <BanFileList items={files.items} />}
          </>
        )}
      </div>
    </DialogContent>
  )
}

/**
 * The person's newest case file that stands, on the unban confirmation, while its ban has not been
 * lifted already: the one this unban will mark as lifted. One row, opening the case file.
 */
function CaseBeforeUnban({ userId, onOpen }: { userId: string; onOpen: (caseId: string) => void }) {
  const [file, setFile] = useState<CaseFileSummary | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .cases({ userId, limit: 1 })
      .then((list) => {
        const newest = list.cases[0]
        if (!cancelled && newest && !newest.liftedAt) setFile(newest)
      })
      .catch(() => undefined)

    return () => {
      cancelled = true
    }
  }, [userId])

  if (!file) return null

  return (
    <div
      className="flex flex-wrap items-center gap-x-2 gap-y-1 rounded-sm border-(length:--hairline) px-2 py-1.5"
      style={{ fontSize: 'var(--text-small)' }}
    >
      <span className="text-muted-foreground">Case file</span>
      <button
        type="button"
        onClick={() => onOpen(file.id)}
        className="rounded-sm text-left font-medium hover:underline focus-visible:outline-2 focus-visible:outline-ring"
      >
        {file.reasons.map((r) => r.label).join(', ') || 'No reason recorded'}
      </button>
      <span className="text-muted-foreground">
        <span className="font-mono">{formatDay(file.bannedAt ?? file.createdAt)}</span> · {file.authorUsername}
      </span>
    </div>
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
