import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DataSettings } from '@/lib/api'
import { Fact, Field, Hint, Outcome, Placeholder, Row } from './fields'
import { SettingsCard, SettingsSection } from './SettingsCard'
import { GB, bytes, remember, remembered } from './units'

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

  const load = useCallback(() => {
    const budget = {
      costPerGbMonth: Number(cost) || undefined,
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
  }, [cost, capacity])

  useEffect(() => load(), [load])

  return (
    <SettingsSection
      id="data"
      title="Data"
      description="What Modbot is keeping, what it costs, and for how long."
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
  return (
    <SettingsCard
      span={12}
      title="Storage"
      description="Measured from the database, and where it is heading if facts keep arriving at this rate."
    >
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
          <Hint>
            Sizes include indexes.{' '}
            {storage.observedDays >= 1
              ? `The arrival rate is measured over the last ${Math.round(storage.observedDays)} days.`
              : 'The arrival rate needs a day of history to measure.'}
          </Hint>

          <div className="grid grid-cols-2 gap-3">
            <Field label="Cost per GB / month" placeholder="0.25" value={cost} onChange={onCost} />
            <Field label="Disk size (GB)" placeholder="500" value={capacity} onChange={onCapacity} />
          </div>
          <Hint>
            What-if inputs, remembered by this browser only. Nothing in Modbot changes for having
            been told them.
          </Hint>
        </div>

        <div className="min-w-0">
          {/* Always shown, however little history there is. The confidence label below carries
              the caveat; withholding the number was tried and the operator preferred to see it. */}
          <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="text-muted-foreground">
              <tr>
                <th className="text-left font-normal">If this rate continues</th>
                <th className="text-right font-normal">Size</th>
                <th className="text-right font-normal">Cost / month</th>
              </tr>
            </thead>
            <tbody>
              {storage.horizons.map((h) => (
                <tr key={h.months}>
                  <td className="py-1">In {h.months} months</td>
                  <td className="py-1 text-right tabular-nums">{bytes(h.estimatedBytes)}</td>
                  <td className="py-1 text-right tabular-nums">
                    {h.monthlyCost === null ? '—' : `$${h.monthlyCost.toFixed(2)}`}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <Hint className="mt-2">
            A straight line, which real growth is not — a group that opens more instances
            generates more facts per member. Treat it as an order of magnitude.
            {storage.confidence === 'Low' &&
              ' Based on under a month of history, so a single busy weekend still moves it a lot.'}
            {storage.confidence === 'Insufficient' &&
              ' Based on less than a day of history — a guess, and one that will change a lot by tomorrow.'}
          </Hint>

          {storage.capacityExhausted && (
            <p className="mt-2 text-destructive" style={{ fontSize: 'var(--text-small)' }}>
              At this rate that disk fills around{' '}
              {new Date(storage.capacityExhausted).toLocaleDateString()}.
            </p>
          )}
        </div>
      </div>
    </SettingsCard>
  )
}

function DeploymentCard({ deployment }: { deployment: DataSettings['deployment'] }) {
  return (
    <SettingsCard title="Deployment" description="What this copy of Modbot is running on.">
      <div>
        <Row label="Version" value={deployment.version} />
        <Row
          label="Host"
          value={
            deployment.platform +
            (deployment.platformEvidence ? ` (detected from ${deployment.platformEvidence})` : '')
          }
        />
        <Row
          label="Log files"
          value={deployment.logFilesWritten ? 'Written to disk' : 'Console and Seq only'}
        />
      </div>
      <Hint>{deployment.persistenceExplanation}</Hint>
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
      description="How long facts are kept before they are destroyed."
      footer={
        <>
          <Button size="sm" disabled={!dirty || saving} onClick={save}>
            {saving ? 'Saving…' : 'Save retention'}
          </Button>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Hint>
        {keepingEverything
          ? 'Modbot is keeping everything, which is the default. History cannot be filled in later: whatever is deleted is gone, and no amount of API access brings it back.'
          : 'A retention window is set. Facts past it are destroyed permanently.'}
      </Hint>
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
      <Hint>
        0 keeps forever. Daily totals are never aged out, so charts keep their full history even
        where the underlying facts have been removed.
      </Hint>
    </SettingsCard>
  )
}
