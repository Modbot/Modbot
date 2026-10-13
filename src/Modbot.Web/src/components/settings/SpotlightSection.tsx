import { useCallback, useEffect, useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { api, ApiError, type SpotlightSettings, type TwitchPostPlaces } from '@/lib/api'
import { placesEqual } from '@/lib/twitch'
import { Outcome, Placeholder, Switch } from './fields'
import { PostPlacesPicker } from './PostPlacesPicker'
import { SettingsCard, SettingsSection } from './SettingsCard'

/**
 * Settings → Spotlight (spotlight design §3): whether Modbot makes a weekly draft featuring a member
 * who opted in, the day and hour it is made, who picks the member, and the sites the post goes to.
 * The draft is finished and sent from the Marketing tab, so nothing goes out by itself.
 */
export function SpotlightSection() {
  const [data, setData] = useState<SpotlightSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .spotlightSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load spotlight settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="spotlight" title="Spotlight">
      {error || !data ? (
        <Placeholder tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </Placeholder>
      ) : (
        <Form stored={data} onSaved={setData} />
      )}
    </SettingsSection>
  )
}

const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']

const HOURS = Array.from({ length: 24 }, (_, h) => h)

function zones(): string[] {
  try {
    return Intl.supportedValuesOf('timeZone')
  } catch {
    return []
  }
}

function Form({ stored, onSaved }: { stored: SpotlightSettings; onSaved: (next: SpotlightSettings) => void }) {
  const [on, setOn] = useState(stored.on)
  const [day, setDay] = useState(stored.day)
  const [hour, setHour] = useState(stored.hour)
  const [timeZone, setTimeZone] = useState(stored.timeZone ?? '')
  const [mode, setMode] = useState(stored.mode)
  const [places, setPlaces] = useState<TwitchPostPlaces>(stored.places)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const zoneList = useMemo(() => zones(), [])

  const changed =
    on !== stored.on ||
    day !== stored.day ||
    hour !== stored.hour ||
    timeZone !== (stored.timeZone ?? '') ||
    mode !== stored.mode ||
    !placesEqual(places, stored.places)

  const save = () => {
    setBusy(true)
    setSaved(null)
    setProblem(null)

    api
      .setSpotlightSettings({ on, day, hour, timeZone, mode, places })
      .then((next) => {
        onSaved(next)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Weekly spotlight"
      span={12}
      footer={
        <>
          <Button type="button" size="xs" disabled={busy || !changed} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <Switch checked={on} onChange={setOn}>
          On
        </Switch>

        <div className="grid gap-3 sm:grid-cols-4">
          <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">Day</span>
            <Select value={String(day)} onChange={(d) => setDay(Number(d))} aria-label="Day of the week">
              {WEEKDAYS.map((name, i) => (
                <option key={name} value={i}>
                  {name}
                </option>
              ))}
            </Select>
          </label>

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

          <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">Who picks</span>
            <Select value={mode} onChange={(m) => setMode(m as SpotlightSettings['mode'])} aria-label="Who picks">
              <option value="random">Modbot picks</option>
              <option value="staff">Staff pick</option>
            </Select>
          </label>
        </div>

        <PostPlacesPicker places={places} setPlaces={setPlaces} vrChatRoles={stored.vrChatRoles} />
      </div>
    </SettingsCard>
  )
}
