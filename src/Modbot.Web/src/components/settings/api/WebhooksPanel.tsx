import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Select } from '@/components/ui/select'
import {
  api,
  type EventTypeOption,
  type WebhookDeliveryView,
  type WebhookInput,
  type WebhooksResponse,
  type WebhookState,
  type WebhookView,
} from '@/lib/api'
import { CopyBox } from '@/pages/Users'
import { Field, LongField, Outcome, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { failure, when } from './shared'

const STATE_LABEL: Record<WebhookState, string> = {
  working: 'Working',
  failing: 'Failing',
  stopped: 'Turned off by Modbot',
  off: 'Off',
}

/**
 * Settings → API → Webhooks. The secret is shown once, when a webhook is made or its secret rolled.
 * Only its owner or an administrator may edit or test one (`canEdit`); anyone here may turn one off
 * or delete it.
 */
export function WebhooksPanel() {
  const [data, setData] = useState<WebhooksResponse | null>(null)
  const [types, setTypes] = useState<EventTypeOption[]>([])
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<WebhookView | 'new' | null>(null)
  const [logFor, setLogFor] = useState<WebhookView | null>(null)
  const [secret, setSecret] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .webhooks()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) => setError(failure(e, 'Could not load webhooks.'))),
    [],
  )

  useEffect(() => {
    void load()
    api.eventTypes().then(setTypes).catch(() => setTypes([]))
  }, [load])

  return (
    <SettingsSection id="api-webhooks" title="Webhooks">
      {error ? (
        <Placeholder>{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <>
          <SettingsCard
            title="Webhooks"
            span={12}
            action={
              <Button size="sm" onClick={() => setEditing('new')}>
                Create webhook
              </Button>
            }
          >
            <WebhookList
              webhooks={data.webhooks}
              onEdit={setEditing}
              onLog={setLogFor}
              onSecret={setSecret}
              onChanged={() => void load()}
            />
          </SettingsCard>
          <AddressCard data={data} onChanged={() => void load()} />
        </>
      )}

      <Dialog open={editing !== null} onOpenChange={(open) => !open && setEditing(null)}>
        <DialogContent title={editing === 'new' ? 'Create webhook' : 'Edit webhook'}>
          {editing !== null && (
            <WebhookForm
              key={editing === 'new' ? 'new' : editing.id}
              webhook={editing === 'new' ? null : editing}
              types={types}
              onSaved={() => void load()}
            />
          )}
        </DialogContent>
      </Dialog>

      <Dialog open={logFor !== null} onOpenChange={(open) => !open && setLogFor(null)}>
        <DialogContent title="Delivery log">{logFor && <DeliveryLog webhook={logFor} />}</DialogContent>
      </Dialog>

      <Dialog open={secret !== null} onOpenChange={(open) => !open && setSecret(null)}>
        <DialogContent title="Signing secret">{secret && <CopyBox text={secret} />}</DialogContent>
      </Dialog>
    </SettingsSection>
  )
}

function WebhookList({
  webhooks,
  onEdit,
  onLog,
  onSecret,
  onChanged,
}: {
  webhooks: WebhookView[]
  onEdit: (w: WebhookView) => void
  onLog: (w: WebhookView) => void
  onSecret: (secret: string) => void
  onChanged: () => void
}) {
  const [problem, setProblem] = useState<string | null>(null)
  const [tested, setTested] = useState<{ id: string; result: WebhookDeliveryView } | null>(null)

  if (webhooks.length === 0) return <p className="text-muted-foreground">No webhooks.</p>

  const run = (action: Promise<unknown>, what: string) => {
    setProblem(null)
    action.then(onChanged).catch((e: unknown) => setProblem(failure(e, what)))
  }

  const input = (w: WebhookView, enabled: boolean): WebhookInput => ({
    name: w.name,
    url: w.url,
    eventTypes: w.eventTypes,
    subjectIds: w.subjectIds,
    enabled,
  })

  return (
    <div className="flex flex-col divide-y" style={{ fontSize: 'var(--text-small)' }}>
      {webhooks.map((w) => (
        <div key={w.id} className="flex flex-col gap-1 py-3">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium">{w.name}</span>
            <Badge variant={w.state === 'working' ? 'secondary' : w.state === 'off' ? 'outline' : 'destructive'}>
              {STATE_LABEL[w.state]}
            </Badge>
            <span className="truncate font-mono text-muted-foreground" title={w.url}>
              {w.url}
            </span>
            <div className="flex-1" />
            {w.canEdit && (
              <>
                <Button size="xs" variant="ghost" onClick={() => onEdit(w)}>
                  Edit
                </Button>
                <Button
                  size="xs"
                  variant="ghost"
                  onClick={() => {
                    setProblem(null)
                    api
                      .testWebhook(w.id)
                      .then((result) => setTested({ id: w.id, result }))
                      .catch((e: unknown) => setProblem(failure(e, 'Could not send the test.')))
                  }}
                >
                  Send test
                </Button>
                <Button
                  size="xs"
                  variant="ghost"
                  onClick={() =>
                    api
                      .rollWebhookSecret(w.id)
                      .then((r) => onSecret(r.secret))
                      .catch((e: unknown) => setProblem(failure(e, 'Could not roll the secret.')))
                  }
                >
                  Roll secret
                </Button>
              </>
            )}
            <Button size="xs" variant="ghost" onClick={() => onLog(w)}>
              Delivery log
            </Button>
            {(w.enabled || w.canEdit) && (
              <Button
                size="xs"
                variant="ghost"
                onClick={() => run(api.updateWebhook(w.id, input(w, !w.enabled)), 'Could not change it.')}
              >
                {w.enabled ? 'Turn off' : 'Turn on'}
              </Button>
            )}
            <Button size="xs" variant="ghost" onClick={() => run(api.deleteWebhook(w.id), 'Could not delete it.')}>
              Delete
            </Button>
          </div>
          <div className="flex flex-wrap gap-x-4 gap-y-1 text-muted-foreground">
            <span>Owner: {w.ownerName ?? '—'}</span>
            <span>Events: {w.eventTypes.join(', ')}</span>
            {w.subjectIds.length > 0 && <span>Subjects: {w.subjectIds.length}</span>}
            <span>Last delivered: {w.lastSuccessAt ? when(w.lastSuccessAt) : 'Never'}</span>
            {w.failingSince && <span>Failing since: {when(w.failingSince)}</span>}
            {w.nextAttemptAt && <span>Next try: {when(w.nextAttemptAt)}</span>}
          </div>
          {w.disabledReason && <Outcome tone="problem">{w.disabledReason}</Outcome>}
          {!w.disabledReason && w.lastError && <Outcome tone="problem">{w.lastError}</Outcome>}
          {tested?.id === w.id && (
            <Outcome tone={tested.result.outcome === 'delivered' ? 'ok' : 'problem'}>
              {tested.result.statusCode ? `Test: ${tested.result.statusCode}` : 'Test failed'}
              {tested.result.error ? `: ${tested.result.error}` : ''} ({tested.result.durationMs} ms)
            </Outcome>
          )}
        </div>
      ))}
      <Outcome tone="problem">{problem}</Outcome>
    </div>
  )
}

function WebhookForm({
  webhook,
  types,
  onSaved,
}: {
  webhook: WebhookView | null
  types: EventTypeOption[]
  onSaved: () => void
}) {
  const [name, setName] = useState(webhook?.name ?? '')
  const [url, setUrl] = useState(webhook?.url ?? '')
  const [eventTypes, setEventTypes] = useState((webhook?.eventTypes ?? ['*']).join('\n'))
  const [subjects, setSubjects] = useState((webhook?.subjectIds ?? []).join('\n'))
  const [enabled, setEnabled] = useState(webhook?.enabled ?? true)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [secret, setSecret] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  if (secret) {
    return (
      <div className="space-y-3">
        <p className="font-medium">Signing secret</p>
        <CopyBox text={secret} />
      </div>
    )
  }

  const lines = (text: string) =>
    text
      .split(/[\n,]/)
      .map((s) => s.trim())
      .filter(Boolean)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setProblem(null)
    setSaved(false)

    const body: WebhookInput = {
      name: name.trim(),
      url: url.trim(),
      eventTypes: lines(eventTypes),
      subjectIds: lines(subjects),
      enabled,
    }

    const action = webhook
      ? api.updateWebhook(webhook.id, body).then(() => setSaved(true))
      : api.createWebhook(body).then((created) => setSecret(created.secret))

    action
      .then(onSaved)
      .catch((e: unknown) => setProblem(failure(e, 'Could not save the webhook.')))
      .finally(() => setBusy(false))
  }

  const addType = (type: string) => {
    const current = lines(eventTypes).filter((t) => t !== '*')
    if (!current.includes(type)) setEventTypes([...current, type].join('\n'))
  }

  return (
    <form onSubmit={submit} className="space-y-4">
      <Field label="Name" value={name} placeholder="Our Discord bot" onChange={setName} />
      <Field label="Address" value={url} placeholder="https://example.com/modbot" onChange={setUrl} />
      <LongField label="Event types" value={eventTypes} placeholder="vrchat.group.member.*" rows={4} onChange={setEventTypes} />
      {types.length > 0 && (
        <Select
          className="w-full"
          value=""
          aria-label="Add an event type"
          onChange={(type) => type && addType(type)}
        >
          <option value="">Add an event type…</option>
          {types.map((t) => (
            <option key={t.type} value={t.type}>
              {t.label} ({t.type})
            </option>
          ))}
        </Select>
      )}
      <LongField label="Subjects" value={subjects} placeholder="usr_…" rows={2} onChange={setSubjects} />
      <Switch checked={enabled} onChange={setEnabled}>
        On
      </Switch>
      <div className="flex items-center gap-3">
        <Button type="submit" size="sm" disabled={busy || !name.trim() || !url.trim()}>
          {busy ? 'Saving…' : webhook ? 'Save' : 'Create'}
        </Button>
        <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </form>
  )
}

function DeliveryLog({ webhook }: { webhook: WebhookView }) {
  const [rows, setRows] = useState<WebhookDeliveryView[] | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  useEffect(() => {
    api
      .webhookDeliveries(webhook.id)
      .then(setRows)
      .catch((e: unknown) => setProblem(failure(e, 'Could not load the delivery log.')))
  }, [webhook.id])

  if (problem) return <Outcome tone="problem">{problem}</Outcome>
  if (!rows) return <p className="text-muted-foreground">Loading…</p>
  if (rows.length === 0) return <p className="text-muted-foreground">No deliveries.</p>

  return (
    <div className="max-h-[60vh] overflow-auto">
      <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
        <thead className="text-left text-muted-foreground">
          <tr>
            <th className="py-1 pr-3 font-normal">When</th>
            <th className="py-1 pr-3 font-normal">Event</th>
            <th className="py-1 pr-3 font-normal">Try</th>
            <th className="py-1 pr-3 font-normal">Status</th>
            <th className="py-1 pr-3 font-normal">Time</th>
            <th className="py-1 font-normal">Error</th>
          </tr>
        </thead>
        <tbody className="divide-y">
          {rows.map((d) => (
            <tr key={d.id}>
              <td className="py-1.5 pr-3 whitespace-nowrap">{when(d.attemptedAt)}</td>
              <td className="py-1.5 pr-3 font-mono">{d.test ? 'Test' : `${d.eventType} #${d.eventId}`}</td>
              <td className="py-1.5 pr-3 tabular-nums">{d.attempt}</td>
              <td className={d.outcome === 'delivered' ? 'py-1.5 pr-3 text-ok' : 'py-1.5 pr-3 text-destructive'}>
                {d.statusCode ?? '—'}
              </td>
              <td className="py-1.5 pr-3 tabular-nums">{d.durationMs} ms</td>
              <td className="py-1.5">{d.error}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function AddressCard({ data, onChanged }: { data: WebhooksResponse; onChanged: () => void }) {
  const [problem, setProblem] = useState<string | null>(null)

  return (
    <SettingsCard title="Addresses">
      <Switch
        checked={data.allowPrivateAddresses}
        disabled={!data.canChangeAllowPrivateAddresses}
        onChange={(allow) => {
          setProblem(null)
          api
            .setWebhookSettings({ allowPrivateAddresses: allow })
            .then(onChanged)
            .catch((e: unknown) => setProblem(failure(e, 'Could not change the setting.')))
        }}
      >
        Allow private addresses
      </Switch>
      <Outcome tone="problem">{problem}</Outcome>
    </SettingsCard>
  )
}
