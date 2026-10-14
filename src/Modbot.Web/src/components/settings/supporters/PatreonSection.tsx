import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { ApiError } from '@/lib/api'
import { supporterApi, type SupporterSettings } from '@/lib/supporters'
import { Fact, Outcome, PasswordField, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

/**
 * Settings → Patreon: whether Modbot takes in Patreon's membership messages, the webhook secret that
 * proves they came from Patreon (write-only: only whether one is saved is shown back), and the
 * address to give Patreon. The thank-you message is the one under Ko-fi, the same for both.
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
        <ConnectionCard settings={data} onSaved={setData} />
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
