import { useCallback, useEffect, useMemo, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { InsightBody } from '@/components/insights/InsightBody'
import { insightDays } from '@/components/insights/days'
import {
  api,
  ApiError,
  type AiInsightsSettings as Stored,
  type DiscordChannelPermission,
  type Insight,
  type InsightEvery,
  type InsightKind,
  type InsightKindSettings,
} from '@/lib/api'
import { Outcome, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { ModelField } from './ModelField'

/**
 * Settings → AI → Insights: when each kind of AI-written summary is written, in which time zone,
 * with which model, and which Discord channel it goes to (AI insights design §2).
 *
 * One form for the page, saved from any card, because the time zone on the first card decides
 * what the hour on every other card means.
 */
export function AiInsightsSettings() {
  const [data, setData] = useState<Stored | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .aiInsightsSettings()
        .then((d) => {
          setData(d)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to change AI settings.'
              : 'Could not load insight settings.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="ai-insights" title="AI insights">
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

type KindForm = Omit<InsightKindSettings, 'label' | 'last'>

/** An insight is posted as one card. */
const POST_NEEDS: DiscordChannelPermission[] = ['viewChannel', 'sendMessages', 'embedLinks']

const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']

const HOURS = Array.from({ length: 24 }, (_, h) => h)

function zones(): string[] {
  try {
    return Intl.supportedValuesOf('timeZone')
  } catch {
    return []
  }
}

const failure = (e: unknown) =>
  e instanceof ApiError && e.status === 403
    ? 'You do not have permission to change AI settings.'
    : e instanceof ApiError
      ? e.message
      : 'Could not reach the Modbot server.'

function Form({ stored, onSaved }: { stored: Stored; onSaved: (next: Stored) => void }) {
  const [timeZone, setTimeZone] = useState(stored.timeZone ?? '')
  const [model, setModel] = useState(stored.model ?? '')
  const [kinds, setKinds] = useState<KindForm[]>(() =>
    stored.kinds.map(({ kind, enabled, every, hour, weekday, discordChannelId }) => ({
      kind,
      enabled,
      every,
      hour,
      weekday,
      discordChannelId: discordChannelId ?? '',
    })),
  )

  // The last attempt of each kind: the stored one, replaced by Generate now.
  const [last, setLast] = useState<Record<string, Insight | null>>(() =>
    Object.fromEntries(stored.kinds.map((k) => [k.kind, k.last])),
  )

  const [saving, setSaving] = useState<string | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<{ card: string; message: string } | null>(null)
  const [generating, setGenerating] = useState<InsightKind | null>(null)

  const zoneList = useMemo(() => zones(), [])

  const change = (kind: InsightKind, patch: Partial<KindForm>) =>
    setKinds((all) => all.map((k) => (k.kind === kind ? { ...k, ...patch } : k)))

  const save = (card: string) => {
    setSaving(card)
    setSaved(null)
    setProblem(null)

    api
      .setAiInsightsSettings({
        timeZone: timeZone || null,
        model: model.trim() || null,
        kinds: kinds.map((k) => ({ ...k, discordChannelId: k.discordChannelId?.trim() || null })),
      })
      .then((next) => {
        setSaved(card)
        onSaved(next)
      })
      .catch((e: unknown) => setProblem({ card, message: failure(e) }))
      .finally(() => setSaving(null))
  }

  const generate = (kind: InsightKind) => {
    setGenerating(kind)
    setSaved(null)
    setProblem(null)

    api
      .generateInsight(kind)
      .then((insight) => setLast((all) => ({ ...all, [kind]: insight })))
      .catch((e: unknown) => setProblem({ card: kind, message: failure(e) }))
      .finally(() => setGenerating(null))
  }

  const footer = (card: string, extra?: React.ReactNode) => (
    <>
      <Button size="sm" disabled={saving !== null} onClick={() => save(card)}>
        {saving === card ? 'Saving…' : 'Save'}
      </Button>
      {extra}
      <Outcome tone="ok">{saved === card && 'Saved.'}</Outcome>
      <Outcome tone="problem">{problem?.card === card && problem.message}</Outcome>
    </>
  )

  return (
    <>
      <SettingsCard title="Insights" span={12} footer={footer('shared')}>
        {!stored.aiOn && <Outcome tone="problem">AI is off.</Outcome>}
        <div className="grid max-w-2xl gap-3 sm:grid-cols-2">
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
          <ModelField feature="insights" value={model} placeholder={stored.baseModel ?? ''} onChange={setModel} />
        </div>
      </SettingsCard>

      {stored.kinds.map(({ kind, label }) => {
        const form = kinds.find((k) => k.kind === kind)!
        const attempt = last[kind]

        return (
          <SettingsCard
            key={kind}
            title={label}
            footer={footer(
              kind,
              <Button
                size="xs"
                variant="outline"
                disabled={generating !== null || !stored.aiOn}
                onClick={() => generate(kind)}
              >
                {generating === kind ? 'Generating…' : 'Generate now'}
              </Button>,
            )}
          >
            <Switch checked={form.enabled} onChange={(enabled) => change(kind, { enabled })}>
              On
            </Switch>

            <div className="grid gap-3 sm:grid-cols-3">
              <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
                <span className="text-muted-foreground">Every</span>
                <Select
                  value={form.every}
                  onChange={(every) => change(kind, { every: every as InsightEvery })}
                  aria-label="Every"
                >
                  <option value="day">Day</option>
                  <option value="week">Week</option>
                </Select>
              </label>

              {form.every === 'week' && (
                <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
                  <span className="text-muted-foreground">Day</span>
                  <Select
                    value={String(form.weekday)}
                    onChange={(d) => change(kind, { weekday: Number(d) })}
                    aria-label="Day of the week"
                  >
                    {WEEKDAYS.map((name, i) => (
                      <option key={name} value={i}>
                        {name}
                      </option>
                    ))}
                  </Select>
                </label>
              )}

              <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
                <span className="text-muted-foreground">At</span>
                <Select value={String(form.hour)} onChange={(h) => change(kind, { hour: Number(h) })} aria-label="Time">
                  {HOURS.map((h) => (
                    <option key={h} value={h}>
                      {`${String(h).padStart(2, '0')}:00`}
                    </option>
                  ))}
                </Select>
              </label>
            </div>

            <div className="max-w-sm">
              <ChannelPicker
                label="Discord channel"
                value={form.discordChannelId ?? ''}
                onChange={(discordChannelId) => change(kind, { discordChannelId })}
                needs={POST_NEEDS}
              />
            </div>

            {attempt && (
              <div className="flex flex-col gap-2 border-t border-t-(length:--hairline) pt-3">
                <span className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
                  {insightDays(attempt)}
                </span>
                {attempt.text ? (
                  <InsightBody insight={attempt} />
                ) : (
                  <Outcome tone="problem">{attempt.error}</Outcome>
                )}
                {attempt.discordError && <Outcome tone="problem">{attempt.discordError}</Outcome>}
              </div>
            )}
          </SettingsCard>
        )
      })}
    </>
  )
}
