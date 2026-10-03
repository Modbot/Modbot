import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type BlueskySettings } from '@/lib/api'
import { timeOfDay } from '@/lib/format'
import { ConfirmButton, Fact, Field, Outcome, PasswordField, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

/**
 * Settings → Bluesky (Bluesky design §3.1, posts design §4.2c): the handle, an app password, Check,
 * Remove, and the Posting switch once Check has passed.
 *
 * No words explain the setup (CLAUDE.md): the how-to is the docs page `moderation/bluesky`, which
 * says how to make an app password and mark the account as automated. The app password is never
 * shown back; only that one is saved.
 */
export function BlueskySection() {
  const [data, setData] = useState<BlueskySettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .blueskySettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load Bluesky settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="bluesky" title="Bluesky">
      {error || !data ? (
        <Placeholder tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </Placeholder>
      ) : (
        <>
          <AccountCard settings={data} onSaved={setData} />
          {(data.canPost || data.posting) && <PostingCard settings={data} onSaved={setData} />}
        </>
      )}
    </SettingsSection>
  )
}

function AccountCard({ settings, onSaved }: { settings: BlueskySettings; onSaved: (next: BlueskySettings) => void }) {
  // Null until somebody types, so the field follows the stored handle after a save and after Remove.
  const [typedHandle, setTypedHandle] = useState<string | null>(null)
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState<'saving' | 'checking' | 'removing' | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const limitedUntil = useStillAhead(settings.limitedUntil)
  const signInAfter = useStillAhead(settings.signInAfter)

  const handle = typedHandle ?? settings.handle ?? ''
  const changed = handle.trim().replace(/^@/, '').toLowerCase() !== (settings.handle ?? '') || password.trim() !== ''

  const run = (kind: 'saving' | 'checking' | 'removing', call: () => Promise<BlueskySettings>, done?: string) => {
    setBusy(kind)
    setSaved(null)
    setProblem(null)

    call()
      .then((next) => {
        onSaved(next)
        if (kind !== 'checking') {
          setTypedHandle(null)
          setPassword('')
        }
        if (done) setSaved(done)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(null))
  }

  const save = () =>
    run(
      'saving',
      () =>
        api.setBlueskySettings({
          ...(handle.trim() ? { handle: handle.trim() } : {}),
          ...(password.trim() ? { appPassword: password.trim() } : {}),
        }),
      'Saved.',
    )

  const check = () => run('checking', () => api.checkBluesky())
  const remove = () => run('removing', () => api.removeBlueskySettings())

  const result = settings.check

  return (
    <SettingsCard
      title="Account"
      footer={
        <>
          <Button type="button" size="xs" disabled={busy !== null || !changed} onClick={save}>
            {busy === 'saving' ? 'Saving…' : 'Save'}
          </Button>
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={busy !== null || changed || !settings.handle || !settings.appPasswordStored || limitedUntil !== null}
            onClick={check}
          >
            {busy === 'checking' ? 'Checking…' : 'Check'}
          </Button>
          {(settings.handle || settings.appPasswordStored) && (
            <ConfirmButton variant="outline" disabled={busy !== null} onConfirm={remove}>
              Remove
            </ConfirmButton>
          )}
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Fact label="App password" value={settings.appPasswordStored ? 'Saved' : 'Not set'} />
      <Field label="Handle" mono value={handle} maxLength={253} onChange={setTypedHandle} />
      <PasswordField label="App password" mono value={password} onChange={setPassword} />

      {result && !result.problem && (
        <div className="grid gap-3 sm:grid-cols-2">
          <Fact
            label="Signed in as"
            value={`@${result.handle ?? settings.handle ?? ''}${result.displayName ? ` (${result.displayName})` : ''}`}
          />
          <Fact label="Automated" value={result.automated ? 'Yes' : 'No'} />
        </div>
      )}

      {limitedUntil ? (
        <Outcome tone="problem">Bluesky is limiting Modbot. Try again after {timeOfDay(limitedUntil)}.</Outcome>
      ) : signInAfter ? (
        <Outcome tone="problem">Too many sign-ins. Try again after {timeOfDay(signInAfter)}.</Outcome>
      ) : (
        <Outcome tone="problem">{result?.problem}</Outcome>
      )}
    </SettingsCard>
  )
}

/** Posting to Bluesky: the one switch that stops all of it, also shown in Settings → Posts. */
function PostingCard({ settings, onSaved }: { settings: BlueskySettings; onSaved: (next: BlueskySettings) => void }) {
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const setPosting = (posting: boolean) => {
    setBusy(true)
    setProblem(null)

    api
      .setBlueskySettings({ posting })
      .then(onSaved)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard title="Posting" footer={<Outcome tone="problem">{problem}</Outcome>}>
      <Switch checked={settings.posting} disabled={busy || (!settings.posting && !settings.canPost)} onChange={setPosting}>
        Posting
      </Switch>
    </SettingsCard>
  )
}

/** A time Bluesky or the guard holds Modbot until, while it is still ahead; null once it has passed. */
function useStillAhead(until: string | null): string | null {
  const [passed, setPassed] = useState<string | null>(null)

  useEffect(() => {
    if (!until) return
    const timer = window.setTimeout(() => setPassed(until), Math.max(0, new Date(until).getTime() - Date.now()))
    return () => window.clearTimeout(timer)
  }, [until])

  return until && passed !== until ? until : null
}
