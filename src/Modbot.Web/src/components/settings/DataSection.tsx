import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DataSettings } from '@/lib/api'
import { followLink } from '@/lib/router'
import { Fact, Field, Hint, Outcome, Placeholder, Row } from './fields'
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
    <SettingsSection
      id="data"
      title="Data"
    >
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
          <DeploymentCard deployment={data.deployment} />
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
              className="flex items-center gap-2 rounded-lg border border-ok/40 bg-ok/10 px-4 py-3"
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
          <a href="/credits" onClick={followLink('/credits')}>
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
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const keepingEverything =
    current.moderationFactRetentionDays === 0 && current.presenceFactRetentionDays === 0

  const dirty =
    moderation !== String(current.moderationFactRetentionDays) ||
    presence !== String(current.presenceFactRetentionDays)

  const save = () => {
    setSaving(true)
    setProblem(null)
    api
      .setRetention({
        moderationFactRetentionDays: Number(moderation) || 0,
        presenceFactRetentionDays: Number(presence) || 0,
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
      <div className="grid max-w-sm grid-cols-2 gap-3">
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
      </div>
      <Hint>0 keeps forever.</Hint>
    </SettingsCard>
  )
}
