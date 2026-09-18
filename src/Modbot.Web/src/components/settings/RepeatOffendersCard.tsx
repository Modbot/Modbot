import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, type RepeatOffenderRules } from '@/lib/api'
import { Checkbox, NumberField, Outcome } from './fields'
import { SettingsCard } from './SettingsCard'

/**
 * Settings → Moderation: the rules the repeat-offender status is decided by.
 *
 * Both were fixed before this card existed — the threshold in a settings document no screen could
 * reach, and the kinds of action compiled into the counting query. Saving rebuilds every person's
 * counts on the server before it answers, so the standings on the Bans page match the rule as soon
 * as this says it saved.
 */
export function RepeatOffendersCard() {
  const [rules, setRules] = useState<RepeatOffenderRules | null>(null)
  const [threshold, setThreshold] = useState('')
  const [types, setTypes] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState('')
  const [error, setError] = useState('')

  const load = (next: RepeatOffenderRules) => {
    setRules(next)
    setThreshold(String(next.threshold))
    setTypes(next.types.filter((t) => t.counts).map((t) => t.value))
  }

  useEffect(() => {
    api
      .repeatOffenderRules()
      .then(load)
      .catch(() => setError('Could not load the rules.'))
  }, [])

  const toggle = (value: string, on: boolean) =>
    setTypes((current) => (on ? [...current, value] : current.filter((t) => t !== value)))

  const save = () => {
    setBusy(true)
    setSaved('')
    setError('')

    api
      .setRepeatOffenderRules(Number(threshold), types)
      .then((next) => {
        load(next)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Repeat offenders"
      footer={
        <>
          <Button size="sm" disabled={busy || !rules} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{error}</Outcome>
        </>
      }
    >
      <NumberField
        label="Repeat offender threshold"
        value={threshold}
        min={2}
        max={100}
        onChange={setThreshold}
      />

      <div className="flex flex-col gap-1">
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Repeat offender types
        </span>
        {(rules?.types ?? []).map((type) => (
          <Checkbox
            key={type.value}
            checked={types.includes(type.value)}
            onChange={(on) => toggle(type.value, on)}
          >
            {type.label}
          </Checkbox>
        ))}
      </div>
    </SettingsCard>
  )
}
