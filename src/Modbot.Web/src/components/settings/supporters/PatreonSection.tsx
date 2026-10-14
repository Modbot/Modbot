import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { ApiError } from '@/lib/api'
import { dateTime } from '@/lib/format'
import { supporterApi, type SupporterSettings, type SupporterSettingsInput } from '@/lib/supporters'
import { Fact, Outcome, PasswordField, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'
import { RolesOffCard } from './RolesOffCard'

/**
 * Settings → Patreon: whether Modbot takes in Patreon's membership messages, the webhook secret that
 * proves they came from Patreon, the four keys of the Patreon client that let Modbot ask Patreon for each
 * patron's Discord account and read every current patron once a day (all write-only: only whether one is
 * saved is shown back), when the last catch-up ran, and the address to give Patreon. The thank-you message
 * is the one under Ko-fi, the same for both.
 *
 * No words explain the setup (CLAUDE.md): the how-to is the docs page `integrations/patreon`.
 */
export function PatreonSection() {
  const [data, setData] = useState<SupporterSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      supporterApi
        .settings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the Patreon settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="patreon" title="Patreon">
      {error || !data ? (
        <Placeholder tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </Placeholder>
      ) : (
        <>
          <ConnectionCard settings={data} onSaved={setData} />
          <KeysCard settings={data} onSaved={setData} />
          <RolesOffCard site="patreon" settings={data} onSaved={setData} />
        </>
      )}
    </SettingsSection>
  )
}

function ConnectionCard({ settings, onSaved }: { settings: SupporterSettings; onSaved: (next: SupporterSettings) => void }) {
  const [secret, setSecret] = useState('')
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const run = (body: { patreonOn?: boolean; patreonWebhookSecret?: string }, done?: string) => {
    setBusy(true)
    setSaved(null)
    setProblem(null)

    supporterApi
      .setSettings(body)
      .then((next) => {
        onSaved(next)
        setSecret('')
        if (done) setSaved(done)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Connection"
      footer={
        <>
          <Button
            type="button"
            size="xs"
            disabled={busy || secret.trim() === ''}
            onClick={() => run({ patreonWebhookSecret: secret.trim() }, 'Saved.')}
          >
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Switch
        checked={settings.patreonOn}
        disabled={busy || (!settings.patreonOn && !settings.patreonSecretStored)}
        onChange={(on) => run({ patreonOn: on })}
      >
        Patreon
      </Switch>
      <Fact label="Webhook secret" value={settings.patreonSecretStored ? 'Saved' : 'Not set'} />
      <PasswordField label="Webhook secret" mono value={secret} onChange={setSecret} />
      <Fact label="Webhook address" value={settings.patreonWebhookAddress ?? 'Public address not set'} />
    </SettingsCard>
  )
}

const KEYS = [
  { field: 'patreonClientId', stored: 'patreonClientIdStored', label: 'Client id' },
  { field: 'patreonClientSecret', stored: 'patreonClientSecretStored', label: 'Client secret' },
  { field: 'patreonAccessToken', stored: 'patreonAccessTokenStored', label: 'Creator access token' },
  { field: 'patreonRefreshToken', stored: 'patreonRefreshTokenStored', label: 'Creator refresh token' },
] as const

function KeysCard({ settings, onSaved }: { settings: SupporterSettings; onSaved: (next: SupporterSettings) => void }) {
  const [values, setValues] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const filled = KEYS.filter((key) => (values[key.field] ?? '').trim() !== '')

  const save = () => {
    setBusy(true)
    setSaved(null)
    setProblem(null)

    const body: SupporterSettingsInput = {}
    for (const key of filled) body[key.field] = (values[key.field] ?? '').trim()

    supporterApi
      .setSettings(body)
      .then((next) => {
        onSaved(next)
        setValues({})
        setSaved('Saved.')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Patreon client"
      footer={
        <>
          <Button type="button" size="xs" disabled={busy || filled.length === 0} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      {KEYS.map((key) => (
        <div key={key.field} className="flex flex-col gap-1">
          <Fact label={key.label} value={settings[key.stored] ? 'Saved' : 'Not set'} />
          <PasswordField
            label={key.label}
            mono
            value={values[key.field] ?? ''}
            onChange={(value) => setValues((previous) => ({ ...previous, [key.field]: value }))}
          />
        </div>
      ))}
      <Fact label="Last catch-up" value={settings.patreonCatchUpAt ? dateTime(settings.patreonCatchUpAt) : 'Not yet'} />
      {settings.patreonCatchUpProblem && <Fact label="Problem" value={settings.patreonCatchUpProblem} />}
    </SettingsCard>
  )
}
