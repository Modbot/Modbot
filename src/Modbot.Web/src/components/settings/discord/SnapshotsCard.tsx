import { useCallback, useEffect, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { api, ApiError, type DiscordSnapshotSettings } from '@/lib/api'
import { Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

/**
 * Settings → Discord → Server snapshots: how often Modbot saves the server's roles, channels and
 * permissions, and whether permissions may be put back from one. Its own card with its own Save,
 * because it saves to its own endpoint.
 */
export function SnapshotsCard() {
  const [data, setData] = useState<DiscordSnapshotSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordSnapshotSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError ? e.message : 'Could not load the server snapshot settings.'),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Server snapshots">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </EmptyRow>
      </SettingsCard>
    )
  }

  return <SnapshotsForm settings={data} onSaved={setData} />
}

function SnapshotsForm({
  settings,
  onSaved,
}: {
  settings: DiscordSnapshotSettings
  onSaved: (next: DiscordSnapshotSettings) => void
}) {
  const [schedule, setSchedule] = useState<string>(settings.schedule)
  const [restoreOn, setRestoreOn] = useState(settings.restoreOn)

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordSnapshotSettings({ schedule: schedule as DiscordSnapshotSettings['schedule'], restoreOn })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Server snapshots"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save server snapshots'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-lg flex-col gap-3">
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Save</span>
          <Select value={schedule} onChange={setSchedule} aria-label="Save">
            <option value="off">Off</option>
            <option value="weekly">Weekly</option>
            <option value="every-two-weeks">Every 2 weeks</option>
            <option value="monthly">Monthly</option>
          </Select>
        </label>

        <Switch checked={restoreOn} onChange={setRestoreOn}>
          Allow restore
        </Switch>
      </div>
    </SettingsCard>
  )
}
