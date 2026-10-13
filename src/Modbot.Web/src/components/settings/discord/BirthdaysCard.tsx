import { useCallback, useEffect, useMemo, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { api, ApiError, type DiscordBirthdaySettings, type DiscordChannelPermission } from '@/lib/api'
import { LongField, Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'

const POST_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages']

const HOURS = Array.from({ length: 24 }, (_, h) => h)

function zones(): string[] {
  try {
    return Intl.supportedValuesOf('timeZone')
  } catch {
    return []
  }
}

/**
 * Settings → Discord → Birthdays: the daily post of the day's birthdays, the channel, the hour and
 * time zone, the text, and the role members hold for the day. Its own card with its own Save,
 * because it saves to its own endpoint.
 */
export function BirthdaysCard() {
  const [data, setData] = useState<DiscordBirthdaySettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordBirthdaySettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load birthday settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Birthdays">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </EmptyRow>
      </SettingsCard>
    )
  }

  return <BirthdaysForm settings={data} onSaved={setData} />
}

function BirthdaysForm({
  settings,
  onSaved,
}: {
  settings: DiscordBirthdaySettings
  onSaved: (next: DiscordBirthdaySettings) => void
}) {
  const [on, setOn] = useState(settings.on)
  const [channelId, setChannelId] = useState(settings.channelId ?? '')
  const [hour, setHour] = useState(settings.hour)
  const [timeZone, setTimeZone] = useState(settings.timeZone ?? '')
  const [message, setMessage] = useState(settings.message)
  const [roleId, setRoleId] = useState(settings.roleId ?? '')

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const zoneList = useMemo(() => zones(), [])

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setDiscordBirthdaySettings({ on, channelId, hour, timeZone, message, roleId })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Birthdays"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save birthdays'}
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

        <ChannelPicker label="Birthday channel" value={channelId} onChange={setChannelId} needs={POST_NEEDS} />

        <div className="grid gap-3 sm:grid-cols-2">
          <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">At</span>
            <Select value={String(hour)} onChange={(h) => setHour(Number(h))} aria-label="Time">
              {HOURS.map((h) => (
                <option key={h} value={h}>
                  {`${String(h).padStart(2, '0')}:00`}
                </option>
              ))}
            </Select>
          </label>

          <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">Time zone</span>
            <Select value={timeZone} onChange={setTimeZone} aria-label="Time zone">
              <option value="">UTC</option>
              {timeZone && !zoneList.includes(timeZone) && <option value={timeZone}>{timeZone}</option>}
              {zoneList.map((z) => (
                <option key={z} value={z}>
                  {z}
                </option>
              ))}
            </Select>
          </label>
        </div>

        <LongField label="Message" placeholder="" rows={3} value={message} onChange={setMessage} />

        <RolePicker label="Birthday role" value={roleId} onChange={setRoleId} needsAssign hideDangerous />
      </div>
    </SettingsCard>
  )
}
