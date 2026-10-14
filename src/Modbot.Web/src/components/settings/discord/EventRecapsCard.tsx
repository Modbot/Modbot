import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DiscordChannelPermission, type DiscordEventRecapSettings } from '@/lib/api'
import { Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

const POST_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages', 'embedLinks', 'readMessageHistory']

/**
 * Settings → Discord → Event recaps: the post that follows an event once its instance has closed,
 * and the channel it goes in. Its own card with its own Save, because it saves to its own endpoint.
 */
export function EventRecapsCard() {
  const [data, setData] = useState<DiscordEventRecapSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordEventRecapSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load event recap settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Event recaps">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </EmptyRow>
      </SettingsCard>
    )
  }

  return <EventRecapsForm settings={data} onSaved={setData} />
}

function EventRecapsForm({
  settings,
  onSaved,
}: {
  settings: DiscordEventRecapSettings
  onSaved: (next: DiscordEventRecapSettings) => void
}) {
  const [on, setOn] = useState(settings.on)
  const [channelId, setChannelId] = useState(settings.channelId ?? '')

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordEventRecapSettings({ on, channelId })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Event recaps"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save event recaps'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem ?? settings.problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-lg flex-col gap-3">
        <Switch checked={on} onChange={setOn}>
          On
        </Switch>

        <ChannelPicker label="Recap channel" value={channelId} onChange={setChannelId} needs={POST_NEEDS} />
      </div>
    </SettingsCard>
  )
}
