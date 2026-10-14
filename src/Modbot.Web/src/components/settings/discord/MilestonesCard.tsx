import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { api, ApiError, type DiscordChannelPermission, type DiscordMilestoneSettings } from '@/lib/api'
import { NumberField, Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

const POST_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages', 'embedLinks']

/**
 * Settings → Discord → Member milestones: the event numbers that count as a first event and as a
 * Regular, the days away that count as coming back, which of those moments get a post and where,
 * and the role a Regular is given. Its own card with its own Save, because it saves to its own
 * endpoint. The marks on a profile need none of it.
 */
export function MilestonesCard() {
  const [data, setData] = useState<DiscordMilestoneSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordMilestoneSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load member milestone settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Member milestones">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </EmptyRow>
      </SettingsCard>
    )
  }

  return <MilestonesForm settings={data} onSaved={setData} />
}

function MilestonesForm({
  settings,
  onSaved,
}: {
  settings: DiscordMilestoneSettings
  onSaved: (next: DiscordMilestoneSettings) => void
}) {
  const [firstAt, setFirstAt] = useState(String(settings.firstAt))
  const [regularAt, setRegularAt] = useState(String(settings.regularAt))
  const [backDays, setBackDays] = useState(String(settings.backDays))
  const [channelId, setChannelId] = useState(settings.channelId ?? '')
  const [postFirst, setPostFirst] = useState(settings.postFirst)
  const [postRegular, setPostRegular] = useState(settings.postRegular)
  const [postBack, setPostBack] = useState(settings.postBack)
  const [roleId, setRoleId] = useState(settings.regularRoleId ?? '')

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordMilestoneSettings({
        firstAt: Number(firstAt),
        regularAt: Number(regularAt),
        backDays: Number(backDays),
        channelId,
        postFirst,
        postRegular,
        postBack,
        regularRoleId: roleId,
      })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Member milestones"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save member milestones'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem ?? settings.problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-lg flex-col gap-3">
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          <NumberField label="First event number" min={1} max={1000} value={firstAt} onChange={setFirstAt} />
          <NumberField label="Regular at event number" min={1} max={1000} value={regularAt} onChange={setRegularAt} />
          <NumberField label="Welcome back after days" min={1} max={3650} value={backDays} onChange={setBackDays} />
        </div>

        <ChannelPicker label="Post channel" value={channelId} onChange={setChannelId} needs={POST_NEEDS} />

        <Switch checked={postFirst} onChange={setPostFirst}>
          Post first events
        </Switch>
        <Switch checked={postRegular} onChange={setPostRegular}>
          Post new Regulars
        </Switch>
        <Switch checked={postBack} onChange={setPostBack}>
          Post welcome backs
        </Switch>

        <RolePicker label="Regular role" value={roleId} onChange={setRoleId} needsAssign hideDangerous />
      </div>
    </SettingsCard>
  )
}
