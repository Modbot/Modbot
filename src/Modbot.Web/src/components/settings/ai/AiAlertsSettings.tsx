import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import {
  api,
  ApiError,
  type AiAlertSettings as Stored,
  type AlertSensitivity,
  type DiscordChannelPermission,
} from '@/lib/api'
import { Outcome, Placeholder } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

/**
 * Settings → AI → Alerts: what Modbot watches for unusual activity, how sensitive each watcher
 * is, where alerts are posted, and how long the same watcher stays quiet (AI insights design §8.6).
 *
 * One form for the page, saved from either card, because the quiet time and the channel on the
 * first card apply to every watcher on the second.
 */
export function AiAlertsSettings() {
  const [data, setData] = useState<Stored | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .aiAlertSettings()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to change AI settings.'
              : 'Could not load alert settings.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="ai-alerts" title="Unusual activity">
      {error ? (
        <Placeholder tone="danger">{error}</Placeholder>
      ) : !data ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <Form stored={data} onSaved={setData} />
      )}
    </SettingsSection>
  )
}

/** An alert is posted as one card. */
const POST_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages', 'embedLinks']

const SENSITIVITIES: { value: AlertSensitivity; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'low', label: 'Low' },
  { value: 'normal', label: 'Normal' },
  { value: 'high', label: 'High' },
]

const QUIET_HOURS = [1, 2, 3, 6, 12, 24, 48]

const failure = (e: unknown) =>
  e instanceof ApiError && e.status === 403
    ? 'You do not have permission to change AI settings.'
    : e instanceof ApiError
      ? e.message
      : 'Could not reach the Modbot server.'

function Form({ stored, onSaved }: { stored: Stored; onSaved: (next: Stored) => void }) {
  const [channel, setChannel] = useState(stored.discordChannelId ?? '')
  const [quietHours, setQuietHours] = useState(stored.quietHours)
  const [writeSentence, setWriteSentence] = useState(stored.writeSentence)
  const [watchers, setWatchers] = useState<Record<string, AlertSensitivity>>(() =>
    Object.fromEntries(stored.watchers.map((w) => [w.watcher, w.sensitivity])),
  )

  const [saving, setSaving] = useState<string | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<{ card: string; message: string } | null>(null)

  const save = (card: string) => {
    setSaving(card)
    setSaved(null)
    setProblem(null)

    api
      .setAiAlertSettings({
        discordChannelId: channel.trim() || null,
        quietHours,
        writeSentence,
        watchers: stored.watchers.map((w) => ({
          watcher: w.watcher,
          sensitivity: watchers[w.watcher] ?? 'off',
        })),
      })
      .then((next) => {
        setSaved(card)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem({ card, message: failure(e) }))
      .finally(() => setSaving(null))
  }

  const footer = (card: string) => (
    <>
      <Button size="sm" disabled={saving !== null} onClick={() => save(card)}>
        {saving === card ? 'Saving…' : 'Save'}
      </Button>
      <Outcome tone="ok">{saved === card && 'Saved.'}</Outcome>
      <Outcome tone="problem">{problem?.card === card && problem.message}</Outcome>
    </>
  )

  return (
    <>
      <SettingsCard title="Alerts" span={12} footer={footer('shared')}>
        {!stored.aiOn && <Outcome tone="problem">AI is off.</Outcome>}

        <div className="grid max-w-2xl gap-3 sm:grid-cols-2">
          <ChannelPicker label="Discord channel" value={channel} onChange={setChannel} needs={POST_NEEDS} />

          <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">Quiet time</span>
            <Select
              value={String(quietHours)}
              onChange={(v) => setQuietHours(Number(v))}
              aria-label="Quiet time"
            >
              <option value="0">None</option>
              {QUIET_HOURS.map((h) => (
                <option key={h} value={h}>
                  {h === 1 ? '1 hour' : `${h} hours`}
                </option>
              ))}
            </Select>
          </label>

          <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">AI sentence</span>
            <Select
              value={writeSentence ? 'on' : 'off'}
              onChange={(v) => setWriteSentence(v === 'on')}
              aria-label="AI sentence"
            >
              <option value="on">On</option>
              <option value="off">Off</option>
            </Select>
          </label>
        </div>
      </SettingsCard>

      <SettingsCard title="What is watched" span={12} footer={footer('watchers')}>
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {stored.watchers.map((w) => (
            <label key={w.watcher} className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
              <span className="text-muted-foreground">{w.label}</span>
              <Select
                value={watchers[w.watcher] ?? 'off'}
                onChange={(v) => setWatchers((all) => ({ ...all, [w.watcher]: v as AlertSensitivity }))}
                aria-label={w.label}
              >
                {SENSITIVITIES.map((s) => (
                  <option key={s.value} value={s.value}>
                    {s.label}
                  </option>
                ))}
              </Select>
              {w.last && (
                <span className="text-muted-foreground">
                  Last <span className="font-mono">{new Date(w.last.at).toLocaleString()}</span>
                </span>
              )}
            </label>
          ))}
        </div>
      </SettingsCard>
    </>
  )
}
