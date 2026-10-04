import { useMemo, useState } from 'react'
import { Outcome } from '@/components/settings/fields'
import { VRChatPermissionMissing } from '@/components/VRChatPermissionMissing'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { SwitchBank } from '@/components/ui/switch-bank'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, type MissingGroupPermission } from '@/lib/api'
import {
  ANNOUNCEMENT_MESSAGE_MAX,
  ANNOUNCEMENT_STATE_WORDS,
  ANNOUNCEMENT_TITLE_MAX,
  announcementsApi,
  type Announcement,
  type AnnouncementState,
} from '@/lib/announcements'
import { dateTime } from '@/lib/format'
import { missingPermissionOf } from '@/lib/vrchatPermissions'

const STATE_BADGE: Record<AnnouncementState, 'ok' | 'warn' | 'destructive' | 'secondary'> = {
  scheduled: 'secondary',
  sending: 'warn',
  sent: 'ok',
  refused: 'destructive',
  failed: 'destructive',
  cancelled: 'secondary',
}

function browserZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
  } catch {
    return 'UTC'
  }
}

/**
 * The Announce form for one instance: a title, a message, and Now or Later. Now asks VRChat while
 * the form waits and closes only once VRChat took it; a refusal stays on the form with VRChat's own
 * words. Later saves it, and the server sends it once at that time.
 */
export function AnnounceDialog({
  instanceId,
  instanceLabel,
  open,
  onOpenChange,
  onDone,
}: {
  instanceId: string
  /** The instance as the card names it, under the title. */
  instanceLabel: string
  open: boolean
  onOpenChange: (open: boolean) => void
  onDone: () => void
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {/* Mounted only while open, so each opening starts empty. */}
      {open && (
        <AnnounceForm
          instanceId={instanceId}
          instanceLabel={instanceLabel}
          onClose={() => onOpenChange(false)}
          onDone={onDone}
        />
      )}
    </Dialog>
  )
}

function AnnounceForm({
  instanceId,
  instanceLabel,
  onClose,
  onDone,
}: {
  instanceId: string
  instanceLabel: string
  onClose: () => void
  onDone: () => void
}) {
  const [title, setTitle] = useState('')
  const [message, setMessage] = useState('')
  const [when, setWhen] = useState<'now' | 'later'>('now')
  const [sendAt, setSendAt] = useState('')
  const [timeZone, setTimeZone] = useState(browserZone)
  const [sending, setSending] = useState(false)
  const [problems, setProblems] = useState<string[]>([])
  const [missing, setMissing] = useState<MissingGroupPermission | null>(null)

  const zones = useMemo(() => {
    try {
      return Intl.supportedValuesOf('timeZone')
    } catch {
      return ['UTC']
    }
  }, [])

  const send = () => {
    setSending(true)
    setProblems([])
    setMissing(null)

    announcementsApi
      .send({ instanceId, title, message, when, sendAt, timeZone })
      .then((saved) => {
        onDone()

        if (saved.state === 'sent' || saved.state === 'scheduled') {
          onClose()
          return
        }

        setMissing(saved.missingGroupPermission)
        setProblems([saved.status ? `${saved.error ?? 'VRChat said no.'} (${saved.status})` : saved.error ?? 'Not sent.'])
      })
      .catch((e: unknown) => {
        if (e instanceof ApiError) {
          const detail = e.detail as { problems?: unknown } | null
          const listed = Array.isArray(detail?.problems) ? detail.problems.filter((p): p is string => typeof p === 'string') : []
          setProblems(listed.length > 0 ? listed : [e.message])
          setMissing(missingPermissionOf(e.detail))
        } else {
          setProblems(['Could not send it.'])
        }
      })
      .finally(() => setSending(false))
  }

  return (
    <DialogContent
      title="Announce"
      subtitle={instanceLabel}
      className="max-w-[520px]"
      foot={
        <DialogFoot>
          <Button size="sm" variant="outline" onClick={onClose} disabled={sending}>
            Cancel
          </Button>
          <Button size="sm" onClick={send} disabled={sending}>
            {sending ? 'Sending…' : when === 'now' ? 'Send' : 'Schedule'}
          </Button>
        </DialogFoot>
      }
    >
      <div className="flex flex-col gap-3">
        <Labelled label="Title" count={`${title.length}/${ANNOUNCEMENT_TITLE_MAX}`}>
          <Input value={title} maxLength={ANNOUNCEMENT_TITLE_MAX} onChange={(e) => setTitle(e.target.value)} />
        </Labelled>
        <Labelled label="Message" count={`${message.length}/${ANNOUNCEMENT_MESSAGE_MAX}`}>
          <Textarea
            value={message}
            rows={4}
            maxLength={ANNOUNCEMENT_MESSAGE_MAX}
            onChange={(e) => setMessage(e.target.value)}
          />
        </Labelled>
        <SwitchBank
          value={when}
          onChange={setWhen}
          label="When"
          options={[
            { value: 'now', label: 'Now' },
            { value: 'later', label: 'Later' },
          ]}
        />
        {when === 'later' && (
          <div className="grid gap-3 sm:grid-cols-2">
            <Labelled label="Date and time">
              <Input type="datetime-local" value={sendAt} onChange={(e) => setSendAt(e.target.value)} />
            </Labelled>
            <Labelled label="Time zone">
              <Select value={timeZone} onChange={setTimeZone} aria-label="Time zone">
                {!zones.includes(timeZone) && <option value={timeZone}>{timeZone}</option>}
                {zones.map((z) => (
                  <option key={z} value={z}>
                    {z}
                  </option>
                ))}
              </Select>
            </Labelled>
          </div>
        )}
        {missing ? (
          <VRChatPermissionMissing missing={missing} className="text-destructive" />
        ) : (
          problems.map((p) => (
            <Outcome key={p} tone="problem">
              {p}
            </Outcome>
          ))
        )}
      </div>
    </DialogContent>
  )
}

function Labelled({ label, count, children }: { label: string; count?: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="flex text-muted-foreground">
        {label}
        {count && <span className="ml-auto font-mono">{count}</span>}
      </span>
      {children}
    </label>
  )
}

/**
 * The announcements for one instance, newest first: the title, when, and how it went. A refusal
 * shows VRChat's own words; a scheduled one can be cancelled by whoever may send.
 */
export function InstanceAnnouncements({
  rows,
  canSend,
  onChanged,
}: {
  rows: Announcement[]
  canSend: boolean
  onChanged: () => void
}) {
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  if (rows.length === 0) return null

  const cancel = (id: string) => {
    setBusy(id)
    setProblem(null)
    announcementsApi
      .cancel(id)
      .then(onChanged)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not cancel it.'))
      .finally(() => setBusy(null))
  }

  return (
    <section className="flex flex-col border-t border-t-(length:--hairline)">
      <CardHeader>
        <CardTitle>Announcements</CardTitle>
        <span className="ml-auto font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {rows.length}
        </span>
      </CardHeader>
      <ul className="divide-y-(length:--hairline) divide-border" style={{ fontSize: 'var(--text-small)' }}>
        {rows.map((a) => (
          <li key={a.id} className="flex flex-col gap-1 px-(--panel-pad) py-1.5">
            <div className="flex min-w-0 items-center gap-2">
              <span className="min-w-0 flex-1 truncate font-semibold">{a.title}</span>
              <Badge variant={STATE_BADGE[a.state]}>{ANNOUNCEMENT_STATE_WORDS[a.state]}</Badge>
              <span className="shrink-0 font-mono text-muted-foreground">{dateTime(a.sentAt ?? a.sendAt)}</span>
              {canSend && a.state === 'scheduled' && (
                <Button size="xs" variant="outline" disabled={busy === a.id} onClick={() => cancel(a.id)}>
                  Cancel
                </Button>
              )}
            </div>
            <span className="[overflow-wrap:anywhere] text-muted-foreground">{a.message}</span>
            {(a.state === 'refused' || a.state === 'failed') &&
              (a.missingGroupPermission ? (
                <VRChatPermissionMissing missing={a.missingGroupPermission} className="text-destructive" />
              ) : (
                <span className="text-destructive">
                  {a.error}
                  {a.status ? ` (${a.status})` : null}
                </span>
              ))}
          </li>
        ))}
      </ul>
      {problem && (
        <p className="px-(--panel-pad) pb-1.5">
          <Outcome tone="problem">{problem}</Outcome>
        </p>
      )}
    </section>
  )
}
