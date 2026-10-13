import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { EmptyRow } from '@/components/PanelGrid'
import { NumberField, Outcome, Switch } from '@/components/settings/fields'
import { SettingsCard } from '@/components/settings/SettingsCard'
import { Button } from '@/components/ui/button'
import { ApiError, type DiscordChannelPermission } from '@/lib/api'
import {
  calendarAlertApi,
  LEAST_MINUTES_BEFORE,
  MOST_MINUTES_BEFORE,
  type CalendarAlertSettings,
} from '@/lib/calendarAlerts'

/** The ping is a post that mentions members, so the bot must see the channel and send there. */
const PING_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages']

/**
 * Alerts on the event categories page: whether members who signed up for a category are told
 * before a matching event starts, by direct message, by a ping in a channel, or both, and how many
 * minutes before the start. Saves to its own endpoint, so it has its own Save.
 */
export function EventAlertsCard() {
  const [data, setData] = useState<CalendarAlertSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      calendarAlertApi
        .get()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the alert settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Alerts" span={12}>
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </EmptyRow>
      </SettingsCard>
    )
  }

  return <EventAlertsForm settings={data} onSaved={setData} />
}

function EventAlertsForm({
  settings,
  onSaved,
}: {
  settings: CalendarAlertSettings
  onSaved: (next: CalendarAlertSettings) => void
}) {
  const [on, setOn] = useState(settings.on)
  const [directMessage, setDirectMessage] = useState(settings.directMessage)
  const [ping, setPing] = useState(settings.ping)
  const [channelId, setChannelId] = useState(settings.channelId ?? '')
  const [minutes, setMinutes] = useState(String(settings.minutesBefore))
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const edit = <T,>(set: (value: T) => void) => (value: T) => {
    set(value)
    setSaved(false)
  }

  const save = () => {
    const minutesBefore = Number(minutes)
    if (!Number.isInteger(minutesBefore) || minutesBefore < LEAST_MINUTES_BEFORE || minutesBefore > MOST_MINUTES_BEFORE) {
      setProblem(`Minutes before must be ${LEAST_MINUTES_BEFORE} to ${MOST_MINUTES_BEFORE}.`)
      return
    }

    setSaving(true)
    setSaved(false)
    setProblem(null)

    calendarAlertApi
      .set({ on, directMessage, ping, channelId: channelId || null, minutesBefore })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save the alert settings.'))
      .finally(() => setSaving(false))
  }

  return (
    <SettingsCard
      title="Alerts"
      span={12}
      footer={
        <>
          <Button size="xs" disabled={saving} onClick={save}>
            {saving ? 'Saving…' : 'Save alerts'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-md flex-col gap-3">
        <Switch checked={on} onChange={edit(setOn)}>
          On
        </Switch>
        <Switch checked={directMessage} onChange={edit(setDirectMessage)}>
          Direct message
        </Switch>
        <Switch checked={ping} onChange={edit(setPing)}>
          Ping
        </Switch>
        {ping && (
          <ChannelPicker label="Ping channel" value={channelId} onChange={edit(setChannelId)} needs={PING_NEEDS} />
        )}
        <NumberField
          label="Minutes before"
          min={LEAST_MINUTES_BEFORE}
          max={MOST_MINUTES_BEFORE}
          value={minutes}
          onChange={edit(setMinutes)}
        />
      </div>
    </SettingsCard>
  )
}
