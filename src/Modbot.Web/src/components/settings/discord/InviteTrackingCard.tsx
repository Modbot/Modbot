import { useCallback, useEffect, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DiscordInviteTrackingSettings } from '@/lib/api'
import { Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

/**
 * Settings → Discord → Invite tracking: whether Modbot records which invite each new member used.
 * Its own card with its own Save, because it saves to its own endpoint.
 */
export function InviteTrackingCard() {
  const [data, setData] = useState<DiscordInviteTrackingSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordInviteTrackingSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError ? e.message : 'Could not load invite tracking settings.'),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Invite tracking">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </EmptyRow>
      </SettingsCard>
    )
  }

  return <InviteTrackingForm settings={data} onSaved={setData} />
}

function InviteTrackingForm({
  settings,
  onSaved,
}: {
  settings: DiscordInviteTrackingSettings
  onSaved: (next: DiscordInviteTrackingSettings) => void
}) {
  const [on, setOn] = useState(settings.on)

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordInviteTrackingSettings({ on })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Invite tracking"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save invite tracking'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-lg flex-col gap-3">
        <Switch checked={on} onChange={setOn}>
          On
        </Switch>
      </div>
    </SettingsCard>
  )
}
