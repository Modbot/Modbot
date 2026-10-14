import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/lib/api'
import { supporterApi, type SupporterSettings } from '@/lib/supporters'
import { Fact, Outcome, PasswordField, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

/**
 * Settings → Ko-fi: whether Modbot takes in Ko-fi's payment messages, the verification token
 * that proves they came from Ko-fi (write-only: only whether one is saved is shown back), the
 * thank-you message, and the address to give Ko-fi.
 *
 * No words explain the setup (CLAUDE.md): the how-to is the docs page `integrations/ko-fi`.
 */
export function KofiSection() {
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
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the Ko-fi settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="kofi" title="Ko-fi">
      {error || !data ? (
        <Placeholder tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </Placeholder>
      ) : (
        <>
          <ConnectionCard settings={data} onSaved={setData} />
          <ThanksCard settings={data} onSaved={setData} />
        </>
      )}
    </SettingsSection>
  )
}

function ConnectionCard({ settings, onSaved }: { settings: SupporterSettings; onSaved: (next: SupporterSettings) => void }) {
  const [token, setToken] = useState('')
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const run = (body: { kofiOn?: boolean; verificationToken?: string }, done?: string) => {
    setBusy(true)
    setSaved(null)
    setProblem(null)

    supporterApi
      .setSettings(body)
      .then((next) => {
        onSaved(next)
        setToken('')
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
            disabled={busy || token.trim() === ''}
            onClick={() => run({ verificationToken: token.trim() }, 'Saved.')}
          >
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Switch checked={settings.kofiOn} disabled={busy || (!settings.kofiOn && !settings.tokenStored)} onChange={(on) => run({ kofiOn: on })}>
        Ko-fi
      </Switch>
      <Fact label="Verification token" value={settings.tokenStored ? 'Saved' : 'Not set'} />
      <PasswordField label="Verification token" mono value={token} onChange={setToken} />
      <Fact label="Webhook address" value={settings.webhookAddress ?? 'Public address not set'} />
    </SettingsCard>
  )
}

function ThanksCard({ settings, onSaved }: { settings: SupporterSettings; onSaved: (next: SupporterSettings) => void }) {
  const [text, setText] = useState(settings.thanksText)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setSaved(null)
    setProblem(null)

    supporterApi
      .setSettings({ thanksText: text })
      .then((next) => {
        onSaved(next)
        setText(next.thanksText)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Thank-you message"
      footer={
        <>
          <Button type="button" size="xs" disabled={busy || text === settings.thanksText} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
        <span className="text-muted-foreground">Message</span>
        <Textarea rows={5} value={text} maxLength={settings.mostThanksLength} onChange={(e) => setText(e.target.value)} />
      </label>
    </SettingsCard>
  )
}
