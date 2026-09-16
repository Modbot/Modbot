import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import {
  api,
  ApiError,
  type CloudStatusView,
  type DataSettings,
  type LinkCodeView,
  type LogSettings,
  type PublicAddressView,
} from '@/lib/api'
import { CREDITS_PATH } from '@/lib/nav'
import { followLink } from '@/lib/router'
import { Checkbox, Fact, Field, Hint, Outcome, Placeholder, Row } from './fields'
import { SettingsCard, SettingsSection } from './SettingsCard'
import { StorageChart } from './StorageChart'
import { CheckCircle2 } from 'lucide-react'
import { GB, bytes, hasPlentyOfStorage, remember, remembered } from './units'

/**
 * Data: what Modbot is keeping, what it costs, and for how long (spec 5.5).
 *
 * Modbot has no default retention window, so the storage card comes first and takes the full
 * width: "keep everything" is only a defensible default while the operator can see what it costs.
 */
export function DataSection() {
  const [data, setData] = useState<DataSettings | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [cost, setCost] = useState(() => remembered('modbot.costPerGbMonth'))
  const [capacity, setCapacity] = useState(() => remembered('modbot.capacityGb'))

  // Only the disk size goes to the server, for the fill date. The cost is applied in the chart,
  // so typing a price does not refetch.
  const load = useCallback(() => {
    const budget = {
      capacityBytes: Number(capacity) ? Number(capacity) * GB : undefined,
    }
    api
      .dataSettings(budget)
      .then((next) => {
        setData(next)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(e instanceof ApiError ? e.message : 'Could not load settings.'),
      )
  }, [capacity])

  useEffect(() => load(), [load])

  return (
    <SettingsSection id="data" title="Host & Database">
      {error ? (
        <Placeholder>{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <>
          <StorageCard
            storage={data.storage}
            cost={cost}
            capacity={capacity}
            onCost={(v) => {
              setCost(v)
              remember('modbot.costPerGbMonth', v)
            }}
            onCapacity={(v) => {
              setCapacity(v)
              remember('modbot.capacityGb', v)
            }}
          />
          <RetentionCard current={data.retention} onSaved={load} />
          <LogsCard />
          <DeploymentCard deployment={data.deployment} />
          <PublicAddressCard />
          <CloudCard />
        </>
      )}
    </SettingsSection>
  )
}

function StorageCard({
  storage,
  cost,
  capacity,
  onCost,
  onCapacity,
}: {
  storage: DataSettings['storage']
  cost: string
  capacity: string
  onCost: (v: string) => void
  onCapacity: (v: string) => void
}) {
  const capacityBytes = Number(capacity) ? Number(capacity) * GB : null

  return (
    <SettingsCard span={12} title="Storage">
      <div className="grid gap-6 lg:grid-cols-[minmax(0,18rem)_minmax(0,1fr)]">
        <div className="flex flex-col gap-4">
          <div className="grid grid-cols-2 gap-x-4 gap-y-3">
            <Fact label="Database" value={bytes(storage.bytes)} />
            <Fact label="Facts recorded" value={storage.facts.toLocaleString()} />
            <Fact
              label="Per fact"
              value={storage.facts > 0 ? `${Math.round(storage.bytesPerFact)} bytes` : '—'}
            />
            <Fact
              label="Arriving"
              value={
                storage.observedDays >= 1
                  ? `${Math.round(storage.factsPerDay).toLocaleString()} facts/day`
                  : 'Not measurable yet'
              }
            />
          </div>

          {/* items-end: a label that wraps grows upward, and the two inputs stay on one line. */}
          <div className="grid max-w-sm grid-cols-2 items-end gap-3">
            <Field label="Cost per GB/mo" placeholder="0.25" value={cost} onChange={onCost} />
            <Field label="Disk size (GB)" placeholder="500" value={capacity} onChange={onCapacity} />
          </div>

          {hasPlentyOfStorage(storage, capacityBytes) && (
            <div
              className="flex items-center gap-2 rounded-xl border border-ok/40 bg-ok/10 px-4 py-3"
              style={{ borderWidth: 'var(--hairline)' }}
            >
              <CheckCircle2 className="size-4 shrink-0 text-ok" aria-hidden />
              <span className="font-medium">You have plenty of storage for the foreseeable future</span>
            </div>
          )}
        </div>

        <div className="flex min-w-0 flex-col gap-3">
          <StorageChart
            storage={storage}
            capacityBytes={capacityBytes}
            costPerGbMonth={Number(cost) || null}
          />
        </div>
      </div>
    </SettingsCard>
  )
}

function DeploymentCard({ deployment }: { deployment: DataSettings['deployment'] }) {
  return (
    <SettingsCard
      title="Deployment"
      footer={
        <Button asChild size="sm" variant="outline">
          <a href={CREDITS_PATH} onClick={followLink(CREDITS_PATH)}>
            Credits
          </a>
        </Button>
      }
    >
      <div>
        <Row label="Version" value={deployment.version} />
        <Row
          label="Version commit"
          value={deployment.commit ? deployment.commit.slice(0, 7) : 'Unknown'}
          title={deployment.commit ?? undefined}
        />
        <Row label="Release branch" value={deployment.branch ?? 'Unknown'} />
        <Row label="Host" value={deployment.platform} />
        <Row
          label="Log files"
          value={deployment.logFilesWritten ? 'Written to disk' : 'Console and Seq only'}
        />
      </div>
    </SettingsCard>
  )
}

function RetentionCard({
  current,
  onSaved,
}: {
  current: DataSettings['retention']
  onSaved: () => void
}) {
  const [moderation, setModeration] = useState(String(current.moderationFactRetentionDays))
  const [presence, setPresence] = useState(String(current.presenceFactRetentionDays))
  const [messages, setMessages] = useState(String(current.discordMessageRetentionDays))
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const keepingEverything =
    current.moderationFactRetentionDays === 0 &&
    current.presenceFactRetentionDays === 0 &&
    current.discordMessageRetentionDays === 0

  const dirty =
    moderation !== String(current.moderationFactRetentionDays) ||
    presence !== String(current.presenceFactRetentionDays) ||
    messages !== String(current.discordMessageRetentionDays)

  const save = () => {
    setSaving(true)
    setProblem(null)
    api
      .setRetention({
        moderationFactRetentionDays: Number(moderation) || 0,
        presenceFactRetentionDays: Number(presence) || 0,
        discordMessageRetentionDays: Number(messages) || 0,
      })
      .then(onSaved)
      .catch((e: unknown) =>
        setProblem(e instanceof ApiError ? e.message : 'Could not save.'),
      )
      .finally(() => setSaving(false))
  }

  return (
    <SettingsCard
      title="Retention"
      footer={
        <>
          <Button size="sm" disabled={!dirty || saving} onClick={save}>
            {saving ? 'Saving…' : 'Save retention'}
          </Button>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      {!keepingEverything && <Hint>Facts past the retention window are destroyed permanently.</Hint>}
      <div className="grid max-w-lg grid-cols-3 gap-3">
        <Field
          label="Moderation facts (days)"
          placeholder="0"
          value={moderation}
          onChange={setModeration}
        />
        <Field
          label="Presence facts (days)"
          placeholder="0"
          value={presence}
          onChange={setPresence}
        />
        <Field
          label="Discord messages (days)"
          placeholder="0"
          value={messages}
          onChange={setMessages}
        />
      </div>
      <Hint>0 keeps forever.</Hint>
    </SettingsCard>
  )
}

/**
 * How long Modbot's own log lines are kept in the database, and whether the same lines go to
 * Modbot Cloud. Here rather than on the Logs page so every retention window is edited in one
 * place; the Logs page reads, this writes.
 */
function LogsCard() {
  const [current, setCurrent] = useState<LogSettings | null>(null)
  const [keepDays, setKeepDays] = useState('')
  const [sendToCloud, setSendToCloud] = useState(true)
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .logSettings()
        .then((next) => {
          setCurrent(next)
          setKeepDays(String(next.keepDays))
          setSendToCloud(next.sendToCloud)
          setProblem(null)
        })
        .catch((e: unknown) =>
          setProblem(e instanceof ApiError ? e.message : 'Could not load the log settings.'),
        ),
    [],
  )

  useEffect(() => void load(), [load])

  const dirty =
    current !== null &&
    (keepDays !== String(current.keepDays) || sendToCloud !== current.sendToCloud)

  const save = () => {
    setSaving(true)
    setProblem(null)
    api
      .setLogSettings({ keepDays: Number(keepDays) || 0, sendToCloud })
      .then(() => load())
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setSaving(false))
  }

  return (
    <SettingsCard
      title="Logs"
      footer={
        <>
          <Button size="sm" disabled={!dirty || saving} onClick={save}>
            {saving ? 'Saving...' : 'Save logs'}
          </Button>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="grid max-w-lg gap-3 sm:grid-cols-2">
        <Field label="Keep for (days)" placeholder="180" value={keepDays} onChange={setKeepDays} />
      </div>
      <Checkbox
        checked={sendToCloud}
        disabled={current === null || !current.cloudAllowed}
        onChange={setSendToCloud}
      >
        Send logs to Modbot Cloud
      </Checkbox>
      <Hint>0 keeps forever.</Hint>
    </SettingsCard>
  )
}

/**
 * Modbot Cloud: what this server last reported, and the code that claims it on a Cloud account
 * (Cloud accounts and registry spec 3.3).
 *
 * The code is made by this server and typed into Cloud, never the other way round. Only somebody
 * who can already sign in here and change settings sees it, which is the proof of ownership.
 */
function CloudCard() {
  const [status, setStatus] = useState<CloudStatusView | null>(null)
  const [code, setCode] = useState<LinkCodeView | null>(null)
  const [asking, setAsking] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .cloudStatus()
      .then(setStatus)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load.'))
  }, [])

  const ask = () => {
    setAsking(true)
    setError(null)

    api
      .cloudLinkCode()
      .then(setCode)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not get a code.'))
      .finally(() => setAsking(false))
  }

  const reported = !status
    ? '…'
    : status.lastReportAt === null
      ? 'Not sent yet'
      : status.lastReportOk
        ? new Date(status.lastReportAt).toLocaleString()
        : (status.lastReportProblem ?? 'Failed')

  return (
    <SettingsCard
      title="Modbot Cloud"
      footer={
        <>
          <Button
            type="button"
            size="sm"
            onClick={ask}
            disabled={asking || !status || status.disabled}
          >
            {asking ? 'Working…' : 'Get link code'}
          </Button>
          <Outcome tone="problem">{error}</Outcome>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <Fact label="Cloud" value={status ? (status.disabled ? 'Turned off' : status.endpoint) : '…'} />
        <Fact label="Registered" value={status ? (status.registered ? 'Yes' : 'No') : '…'} />
        <Fact label="Last report" value={reported} />
        {code && (
          <Fact
            label="Link code"
            value={`${code.code} · ${code.expiresInMinutes} min`}
          />
        )}
      </div>
    </SettingsCard>
  )
}

/**
 * The address people use to reach this Modbot, used to build links sent by email or Discord
 * (accounts and access design §4.2). Saved through its own endpoint, which records the change in
 * the audit log, rather than with the email settings it used to share a form with.
 */
function PublicAddressCard() {
  const [view, setView] = useState<PublicAddressView | null>(null)
  const [value, setValue] = useState('')
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .publicAddress()
      .then((next) => {
        setView(next)
        setValue(next.publicAddress ?? '')
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the public address.'))
  }, [])

  const save = (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setSaved(false)
    setError(null)

    api
      .setPublicAddress(value)
      .then((next) => {
        setView((current) => ({ publicAddress: next.publicAddress, suggestion: current?.suggestion ?? null }))
        setValue(next.publicAddress ?? '')
        setSaved(true)
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setSaving(false))
  }

  const suggestion = view?.suggestion ?? null

  return (
    <SettingsCard
      title="Public address"
      footer={
        <>
          <Button type="submit" form="public-address-form" size="sm" disabled={saving || !view}>
            {saving ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{error}</Outcome>
        </>
      }
    >
      <form id="public-address-form" onSubmit={save} className="flex flex-col gap-3">
        <Fact label="Address" value={view ? (view.publicAddress ?? 'Not set') : '…'} />
        <div className="max-w-lg">
          <Field
            label="Public address"
            value={value}
            onChange={setValue}
            placeholder={suggestion ?? window.location.origin}
          />
        </div>
        {suggestion && !value && (
          <button
            type="button"
            className="self-start text-link underline-offset-2 hover:underline"
            style={{ fontSize: 'var(--text-small)' }}
            onClick={() => setValue(suggestion)}
          >
            Use {suggestion}
          </button>
        )}
      </form>
    </SettingsCard>
  )
}
