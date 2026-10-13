import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DiscordChannelPermission, type DiscordPermissionWatchSettings } from '@/lib/api'
import { Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** The staff channel gets one embed for each permission gained. */
const STAFF_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages', 'embedLinks']

/**
 * Settings → Discord → Permission watch: where staff are told when a role or member gains a
 * dangerous permission, and whether the bot also takes it away. Its own card with its own Save,
 * because it saves to its own endpoint.
 */
export function PermissionWatchCard() {
  const [data, setData] = useState<DiscordPermissionWatchSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordPermissionWatchSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError ? e.message : 'Could not load permission watch settings.'),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Permission watch">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</EmptyRow>
      </SettingsCard>
    )
  }

  return <PermissionWatchForm settings={data} onSaved={setData} />
}

function PermissionWatchForm({
  settings,
  onSaved,
}: {
  settings: DiscordPermissionWatchSettings
  onSaved: (next: DiscordPermissionWatchSettings) => void
}) {
  const [channelId, setChannelId] = useState(settings.channelId ?? '')
  const [removes, setRemoves] = useState(settings.removes)

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordPermissionWatchSettings({ channelId, removes })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Permission watch"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save permission watch'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-lg flex-col gap-3">
        <ChannelPicker label="Staff channel" value={channelId} onChange={setChannelId} needs={STAFF_NEEDS} />
        <Switch checked={removes} onChange={setRemoves}>
          Remove automatically
        </Switch>
      </div>
    </SettingsCard>
  )
}
