import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DataSettings } from '@/lib/api'
import { Field, Placeholder, Row, Section } from './fields'
import { GB, bytes, remember, remembered } from './units'

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

  if (error) return <Placeholder>{error}</Placeholder>
  if (!data) return <Placeholder>Loading…</Placeholder>

  const { storage, deployment, retention } = data
  const keepingEverything =
    retention.moderationFactRetentionDays === 0 && retention.presenceFactRetentionDays === 0

  return (
    <div className="flex flex-col gap-4">
      <Section title="Deployment">
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
        <p className="mt-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {deployment.persistenceExplanation}
        </p>
      </Section>

      <Section title="Storage">
        <Row label="Database" value={bytes(storage.bytes)} />
        <Row label="Facts recorded" value={storage.facts.toLocaleString()} />
        {storage.facts > 0 && (
          <Row label="Per fact" value={`${Math.round(storage.bytesPerFact)} bytes, indexes included`} />
        )}
        <Row
          label="Arriving"
          value={
            storage.observedDays >= 1
              ? `${Math.round(storage.factsPerDay).toLocaleString()} facts/day, measured over ${Math.round(storage.observedDays)} days`
              : 'Not enough history to measure yet'
          }
        />

        <div className="mt-4 grid grid-cols-2 gap-3">
          <Field
            label="Cost per GB / month"
            placeholder="0.25"
            value={cost}
            onChange={(v) => {
              setCost(v)
              remember('modbot.costPerGbMonth', v)
            }}
          />
          <Field
            label="Disk size (GB)"
            placeholder="500"
            value={capacity}
            onChange={(v) => {
              setCapacity(v)
              remember('modbot.capacityGb', v)
            }}
          />
        </div>

        {/* Always shown, however little history there is. The confidence label below carries
            the caveat; withholding the number was tried and the operator preferred to see it. */}
        <>
            <table className="mt-4 w-full" style={{ fontSize: 'var(--text-small)' }}>
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

            <p className="mt-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              A straight line, which real growth is not — a group that opens more instances
              generates more facts per member. Treat it as an order of magnitude.
              {storage.confidence === 'Low' &&
                ' Based on under a month of history, so a single busy weekend still moves it a lot.'}
              {storage.confidence === 'Insufficient' &&
                ' Based on less than a day of history — a guess, and one that will change a lot by tomorrow.'}
            </p>

            {storage.capacityExhausted && (
              <p className="mt-2 text-destructive" style={{ fontSize: 'var(--text-small)' }}>
                At this rate that disk fills around{' '}
                {new Date(storage.capacityExhausted).toLocaleDateString()}.
              </p>
            )}
        </>
      </Section>

      <Section title="Retention">
        <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {keepingEverything
            ? 'Modbot is keeping everything, which is the default. History cannot be filled in later: whatever is deleted is gone, and no amount of API access brings it back.'
            : 'A retention window is set. Facts past it are destroyed permanently.'}
        </p>
        <RetentionForm current={retention} onSaved={load} />
      </Section>
    </div>
  )
}

function RetentionForm({
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
    <div className="mt-3">
      <div className="grid grid-cols-2 gap-3">
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
      <p className="mt-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        0 keeps forever. Daily totals are never aged out, so charts keep their full history even where
        the underlying facts have been removed.
      </p>
      {problem && (
        <p className="mt-2 text-destructive" style={{ fontSize: 'var(--text-small)' }}>
          {problem}
        </p>
      )}
      <Button className="mt-3" size="sm" disabled={!dirty || saving} onClick={save}>
        {saving ? 'Saving…' : 'Save retention'}
      </Button>
    </div>
  )
}
