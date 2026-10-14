import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { ApiError } from '@/lib/api'
import { supporterApi, type SupporterSettings } from '@/lib/supporters'
import { NumberField, Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

const NEVER = -1

/**
 * Settings → Ko-fi and Settings → Patreon: how many days after the paid month ends a supporter on
 * that site loses the roles, or never. One card for both sites, saved on its own.
 */
export function RolesOffCard({
  site,
  settings,
  onSaved,
}: {
  site: 'kofi' | 'patreon'
  settings: SupporterSettings
  onSaved: (next: SupporterSettings) => void
}) {
  const stored = site === 'kofi' ? settings.kofiRolesOffDays : settings.patreonRolesOffDays
  const [never, setNever] = useState(stored === NEVER)
  const [days, setDays] = useState(stored === NEVER ? '3' : String(stored))
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const count = Number(days)
  const valid = never || (days.trim() !== '' && Number.isInteger(count) && count >= 0 && count <= settings.mostRolesOffDays)
  const value = never ? NEVER : count

  const save = () => {
    setBusy(true)
    setSaved(null)
    setProblem(null)

    supporterApi
      .setSettings(site === 'kofi' ? { kofiRolesOffDays: value } : { patreonRolesOffDays: value })
      .then((next) => {
        onSaved(next)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Roles come off"
      footer={
        <>
          <Button type="button" size="xs" disabled={busy || !valid || value === stored} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Switch checked={never} disabled={busy} onChange={setNever}>
        Never
      </Switch>
      {!never && <NumberField label="Days after the paid month ends" value={days} min={0} max={settings.mostRolesOffDays} onChange={setDays} />}
    </SettingsCard>
  )
}
