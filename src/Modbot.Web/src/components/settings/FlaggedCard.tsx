import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, type FlagRules } from '@/lib/api'
import { Checkbox, NumberField, Outcome, Switch } from './fields'
import { SettingsCard } from './SettingsCard'

/**
 * Settings → Moderation: the rules that make a person Flagged — the companion's red join card,
 * its sound and voice line, and the red row on the roster and on Live (flagged rules design §3).
 *
 * Flagged is decided on every read, so a save reaches the next roster read and the next join with
 * nothing to rebuild.
 */
export function FlaggedCard() {
  const [rules, setRules] = useState<FlagRules | null>(null)
  const [kicksAndBans, setKicksAndBans] = useState(true)
  const [liftedStop, setLiftedStop] = useState(false)
  const [liftedDays, setLiftedDays] = useState('')
  const [warns, setWarns] = useState(true)
  const [warnsAtLeast, setWarnsAtLeast] = useState('')
  const [nuisance, setNuisance] = useState(true)
  const [autoMod, setAutoMod] = useState(true)
  const [everyRule, setEveryRule] = useState(true)
  const [chosen, setChosen] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState('')
  const [error, setError] = useState('')

  const load = (next: FlagRules) => {
    setRules(next)
    setKicksAndBans(next.kicksAndBans)
    setLiftedStop(next.liftedBansForDays !== null)
    setLiftedDays(next.liftedBansForDays !== null ? String(next.liftedBansForDays) : '90')
    setWarns(next.warns)
    setWarnsAtLeast(String(next.warnsAtLeast))
    setNuisance(next.nuisance)
    setAutoMod(next.autoMod)
    setEveryRule(next.everyAutoModRule)
    setChosen(next.autoModRules.filter((r) => r.counts).map((r) => r.id))
  }

  useEffect(() => {
    api
      .flagRules()
      .then(load)
      .catch(() => setError('Could not load the rules.'))
  }, [])

  const toggle = (id: string, on: boolean) =>
    setChosen((current) => (on ? [...current, id] : current.filter((r) => r !== id)))

  const save = () => {
    setBusy(true)
    setSaved('')
    setError('')

    api
      .setFlagRules({
        kicksAndBans,
        liftedBansForDays: liftedStop ? Number(liftedDays) : null,
        warns,
        warnsAtLeast: Number(warnsAtLeast),
        nuisance,
        autoMod,
        everyAutoModRule: everyRule,
        autoModRules: everyRule ? [] : chosen,
      })
      .then((next) => {
        load(next)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Flagged"
      span={12}
      footer={
        <>
          <Button size="xs" disabled={busy || !rules} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{error}</Outcome>
        </>
      }
    >
      <div className="flex flex-col gap-2">
        <Switch checked={kicksAndBans} onChange={setKicksAndBans}>
          Kicks and bans
        </Switch>
        {kicksAndBans && (
          <div className="flex flex-col gap-1 pl-6">
            <Checkbox checked={liftedStop} onChange={setLiftedStop}>
              Lifted bans stop counting
            </Checkbox>
            {liftedStop && (
              <div className="max-w-40">
                <NumberField label="Days after lifting" value={liftedDays} min={1} max={3650} onChange={setLiftedDays} />
              </div>
            )}
          </div>
        )}

        <Switch checked={warns} onChange={setWarns}>
          Warns
        </Switch>
        {warns && (
          <div className="max-w-40 pl-6">
            <NumberField label="At least" value={warnsAtLeast} min={1} max={99} onChange={setWarnsAtLeast} />
          </div>
        )}

        <Switch checked={nuisance} onChange={setNuisance}>
          Nuisance on VRChat
        </Switch>

        <Switch checked={autoMod} onChange={setAutoMod}>
          AutoMod
        </Switch>
        {autoMod && (
          <div className="flex flex-col gap-1 pl-6">
            <Checkbox checked={everyRule} onChange={setEveryRule}>
              Every rule
            </Checkbox>
            {!everyRule &&
              (rules?.autoModRules ?? []).map((rule) => (
                <Checkbox key={rule.id} checked={chosen.includes(rule.id)} onChange={(on) => toggle(rule.id, on)}>
                  {rule.name}
                </Checkbox>
              ))}
          </div>
        )}
      </div>
    </SettingsCard>
  )
}
