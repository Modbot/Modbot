import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DiscordChannelPermission, type DiscordGallerySettings } from '@/lib/api'
import { Outcome } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** The bot reads each picture as it is posted. */
const BOT_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'readMessageHistory']

/**
 * Settings → Discord → Gallery pictures: the channel members post pictures in for the group's VRChat
 * gallery. Its own card with its own Save, because it saves to its own endpoint.
 */
export function GalleryPicturesCard() {
  const [data, setData] = useState<DiscordGallerySettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordGallerySettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError ? e.message : 'Could not load gallery picture settings.'),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Gallery pictures">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</EmptyRow>
      </SettingsCard>
    )
  }

  return <GalleryPicturesForm settings={data} onSaved={setData} />
}

function GalleryPicturesForm({
  settings,
  onSaved,
}: {
  settings: DiscordGallerySettings
  onSaved: (next: DiscordGallerySettings) => void
}) {
  const [channelId, setChannelId] = useState(settings.channelId ?? '')

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordGallerySettings({ channelId })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Gallery pictures"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save gallery pictures'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-lg flex-col gap-3">
        <ChannelPicker label="Picture channel" value={channelId} onChange={setChannelId} needs={BOT_NEEDS} />
      </div>
    </SettingsCard>
  )
}
