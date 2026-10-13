import { useCallback, useEffect, useState } from 'react'
import { X } from 'lucide-react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { Field, LongField, Outcome } from '@/components/settings/fields'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { ApiError } from '@/lib/api'
import { formatDay } from '@/lib/format'
import {
  PARTNER_STATUSES,
  partnerApi,
  partnerStatusLabel,
  type Partner,
  type PartnerInput,
  type PartnerStatus,
} from '@/lib/partners'
import { useShortcuts } from '@/lib/shortcuts'
import { PageMessage } from '@/pages/analytics/shared'

/** A day typed as `yyyy-MM-dd`, shown in the viewer's own calendar. */
function dayText(day: string): string {
  return formatDay(`${day}T12:00:00`)
}

const STATUS_BADGE: Record<PartnerStatus, 'ok' | 'warn' | 'outline'> = {
  active: 'ok',
  paused: 'warn',
  ended: 'outline',
}

/**
 * Partners: the other groups this one works with, who to talk to there, who here looks after the
 * friendship, the last collab and notes. Staff only, and every field is what staff typed.
 */
export function Partners() {
  const [partners, setPartners] = useState<Partner[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<Partner | 'new' | null>(null)
  const [deleting, setDeleting] = useState<Partner | null>(null)

  useShortcuts([{ label: 'New partner', group: 'Page', page: true, run: () => setEditing('new') }])

  const load = useCallback(() => {
    partnerApi
      .all()
      .then((answer) => {
        setPartners(answer.partners)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to see this.'
            : e instanceof ApiError
              ? e.message
              : 'Could not load the partners.',
        ),
      )
  }, [])

  useEffect(() => {
    load()
  }, [load])

  if (!partners) return <PageMessage tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</PageMessage>

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex-1" />
        <Button onClick={() => setEditing('new')}>New partner</Button>
      </div>

      {partners.length === 0 ? (
        <PageMessage>No partners.</PageMessage>
      ) : (
        partners.map((partner) => (
          <PartnerCard key={partner.id} partner={partner} onEdit={() => setEditing(partner)} onDelete={() => setDeleting(partner)} />
        ))
      )}

      {editing && (
        <PartnerForm
          partner={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            load()
          }}
        />
      )}

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={`Delete the partner “${deleting?.name ?? ''}”?`}
        action="Delete"
        failed="Could not delete the partner."
        onConfirm={() => (deleting ? partnerApi.remove(deleting.id) : Promise.resolve())}
        onDone={() => {
          setDeleting(null)
          load()
        }}
      />
    </div>
  )
}

/** One partner: its name and status in the header, then what is known about it. */
function PartnerCard({ partner, onEdit, onDelete }: { partner: Partner; onEdit: () => void; onDelete: () => void }) {
  const rows: [string, React.ReactNode][] = [
    ['Envoy', partner.envoy],
    ['Contacts', partner.contacts.length > 0 ? partner.contacts.join(', ') : null],
    [
      'Last collab',
      partner.lastCollabOn
        ? [dayText(partner.lastCollabOn), partner.lastCollabNote].filter((x) => x).join(' · ')
        : partner.lastCollabNote,
    ],
    ['Notes', partner.notes],
  ]

  return (
    <Card className="flex flex-col">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-b-(length:--hairline) px-(--panel-pad) py-1.5">
        <span className="font-medium [overflow-wrap:anywhere]">{partner.name}</span>
        <Badge variant={STATUS_BADGE[partner.status] ?? 'outline'}>{partnerStatusLabel(partner.status)}</Badge>
        {partner.groupId && (
          <span className="font-mono text-muted-foreground [overflow-wrap:anywhere]" style={{ fontSize: 'var(--text-small)' }}>
            {partner.groupId}
          </span>
        )}
        <div className="flex-1" />
        <span className="flex gap-2">
          <Button size="sm" variant="outline" onClick={onEdit}>
            Edit
          </Button>
          <Button size="sm" variant="destructive" onClick={onDelete}>
            Delete
          </Button>
        </span>
      </div>

      <dl className="flex flex-col gap-1 px-(--panel-pad) py-2" style={{ fontSize: 'var(--text-small)' }}>
        {rows
          .filter(([, value]) => value)
          .map(([label, value]) => (
            <div key={label} className="flex flex-col gap-0.5 sm:flex-row sm:gap-3">
              <dt className="text-muted-foreground sm:w-24 sm:shrink-0">{label}</dt>
              <dd className="min-w-0 whitespace-pre-wrap [overflow-wrap:anywhere]">{value}</dd>
            </div>
          ))}
      </dl>
    </Card>
  )
}

/** The add and edit form. */
function PartnerForm({ partner, onClose, onSaved }: { partner: Partner | null; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(partner?.name ?? '')
  const [groupId, setGroupId] = useState(partner?.groupId ?? '')
  const [status, setStatus] = useState<PartnerStatus>(partner?.status ?? 'active')
  const [envoy, setEnvoy] = useState(partner?.envoy ?? '')
  const [contacts, setContacts] = useState<string[]>(partner?.contacts ?? [])
  const [lastCollabOn, setLastCollabOn] = useState(partner?.lastCollabOn ?? '')
  const [lastCollabNote, setLastCollabNote] = useState(partner?.lastCollabNote ?? '')
  const [notes, setNotes] = useState(partner?.notes ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setError(null)

    const body: PartnerInput = {
      name,
      groupId,
      status,
      envoy,
      contacts: contacts.filter((c) => c.trim() !== ''),
      lastCollabOn: lastCollabOn === '' ? null : lastCollabOn,
      lastCollabNote,
      notes,
    }
    const request = partner ? partnerApi.update(partner.id, body) : partnerApi.create(body)

    request
      .then(onSaved)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save the partner.'))
      .finally(() => setBusy(false))
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={partner ? 'Edit partner' : 'New partner'}
        className="max-w-[640px]"
        bodyClassName="max-h-[78vh] overflow-y-auto"
        foot={
          <DialogFoot>
            {error && <Outcome tone="problem">{error}</Outcome>}
            <Button size="sm" variant="outline" onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button size="sm" onClick={save} disabled={busy}>
              {busy ? 'Saving…' : 'Save'}
            </Button>
          </DialogFoot>
        }
      >
        <div className="flex flex-col gap-4" style={{ fontSize: 'var(--text-small)' }}>
          <Field label="Name" value={name} maxLength={100} onChange={setName} />
          <Field label="Group id" value={groupId} maxLength={64} mono onChange={setGroupId} />

          <div className="flex flex-col gap-1">
            <span className="text-muted-foreground">Status</span>
            <Select aria-label="Status" value={status} onChange={(v) => setStatus(v as PartnerStatus)}>
              {PARTNER_STATUSES.map((s) => (
                <option key={s.value} value={s.value}>
                  {s.label}
                </option>
              ))}
            </Select>
          </div>

          <Field label="Envoy" value={envoy} maxLength={100} onChange={setEnvoy} />

          <div className="flex flex-col gap-2">
            <span className="text-muted-foreground">Contacts</span>
            {contacts.map((contact, at) => (
              <div key={at} className="flex gap-2">
                <Input
                  aria-label={`Contact ${at + 1}`}
                  maxLength={200}
                  value={contact}
                  onChange={(e) => setContacts((current) => current.map((c, i) => (i === at ? e.target.value : c)))}
                />
                <button
                  type="button"
                  aria-label={`Remove contact ${at + 1}`}
                  className="grid shrink-0 place-items-center rounded-sm text-muted-foreground hover:bg-muted hover:text-foreground"
                  style={{ height: 'var(--control-h)', width: 'var(--control-h)' }}
                  onClick={() => setContacts((current) => current.filter((_, i) => i !== at))}
                >
                  <X className="size-4" />
                </button>
              </div>
            ))}
            <div>
              <Button size="sm" variant="outline" onClick={() => setContacts((current) => [...current, ''])}>
                Add contact
              </Button>
            </div>
          </div>

          <div className="flex flex-col gap-4 border-t border-t-(length:--hairline) pt-3">
            <label className="flex flex-col gap-1">
              <span className="text-muted-foreground">Last collab date</span>
              <Input type="date" value={lastCollabOn} onChange={(e) => setLastCollabOn(e.target.value)} />
            </label>
            <Field label="Last collab" value={lastCollabNote} maxLength={300} onChange={setLastCollabNote} />
          </div>

          <LongField label="Notes" value={notes} placeholder="" rows={4} onChange={setNotes} />
        </div>
      </DialogContent>
    </Dialog>
  )
}
