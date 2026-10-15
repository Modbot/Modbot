import { useCallback, useEffect, useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { api, ApiError, type BlueskySettings } from '@/lib/api'
import { timeOfDay } from '@/lib/format'
import { useStillAhead } from '@/lib/useStillAhead'
import { ConfirmButton, Fact, Field, Outcome, PasswordField, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

/**
 * Settings → Bluesky (Bluesky design §3.1, posts design §4.2c): the handle, an app password, Check,
 * Remove, and the Posting switch once Check has passed. With a public https address, also Sign in with
 * Bluesky (step 3b): the server answers Bluesky's sign-in page, and Bluesky sends the browser back here
 * with `?bluesky=` saying how it went.
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
          {(data.canPost || data.posting) && <PinCard settings={data} onSaved={setData} />}
          {(data.canPost || data.posting) && <LineupCard settings={data} onSaved={setData} />}
          <TagsCard settings={data} onSaved={setData} />
        </>
      )}
    </SettingsSection>
  )
}

/** What the page says after Bluesky sends the browser back, by the server's `?bluesky=` word. */
const SIGN_IN_RESULTS: Record<string, { ok: boolean; text: string }> = {
  'signed-in': { ok: true, text: 'Signed in.' },
  cancelled: { ok: false, text: 'Bluesky sign-in was cancelled.' },
  expired: { ok: false, text: 'The Bluesky sign-in took too long. Try again.' },
  'signed-out': { ok: false, text: 'You were signed out of Modbot before Bluesky sent you back.' },
  used: { ok: false, text: 'This Bluesky sign-in was already used.' },
  'wrong-account': { ok: false, text: 'Bluesky signed in another account than this handle.' },
  refused: { ok: false, text: 'Bluesky did not finish the sign-in.' },
  unreachable: { ok: false, text: 'Could not reach Bluesky.' },
  limited: { ok: false, text: 'Bluesky is limiting Modbot.' },
}

/** The `?bluesky=` word Bluesky's sign-in came back with, read once and taken off the address. */
function useSignInResult() {
  const [result] = useState(() => {
    const word = new URLSearchParams(window.location.search).get('bluesky')
    return word ? (SIGN_IN_RESULTS[word] ?? null) : null
  })

  useEffect(() => {
    if (!new URLSearchParams(window.location.search).has('bluesky')) return
    window.history.replaceState(window.history.state, '', window.location.pathname + window.location.hash)
  }, [])

  return result
}

/** The handle as the server keeps it: no @, a pasted bsky.app/profile link cut to its handle, `.bsky.social` added to a handle with no dot, lower case. */
function shortHandle(typed: string): string {
  let handle = typed.trim().replace(/^@/, '').replace(/^https?:\/\//i, '')
  if (/^bsky\.app\/profile\//i.test(handle)) handle = handle.slice('bsky.app/profile/'.length).split(/[/?#]/)[0]
  if (handle !== '' && !handle.includes('.')) handle += '.bsky.social'
  return handle.toLowerCase()
}

function AccountCard({ settings, onSaved }: { settings: BlueskySettings; onSaved: (next: BlueskySettings) => void }) {
  // Null until somebody types, so the field follows the stored handle after a save and after Remove.
  const [typedHandle, setTypedHandle] = useState<string | null>(null)
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState<'saving' | 'checking' | 'removing' | 'signing-in' | null>(null)
  const signInResult = useSignInResult()
  const [saved, setSaved] = useState<string | null>(signInResult?.ok ? signInResult.text : null)
  const [problem, setProblem] = useState<string | null>(signInResult && !signInResult.ok ? signInResult.text : null)
  const limitedUntil = useStillAhead(settings.limitedUntil)
  const signInAfter = useStillAhead(settings.signInAfter)

  const handle = typedHandle ?? settings.handle ?? ''
  const changed = shortHandle(handle) !== (settings.handle ?? '') || password.trim() !== ''

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

  // Leaves the page for Bluesky's; only a refusal before that comes back here.
  const signIn = () => {
    setBusy('signing-in')
    setSaved(null)
    setProblem(null)

    api
      .startBlueskySignIn(handle.trim() || undefined)
      .then(({ url }) => window.location.assign(url))
      .catch((e: unknown) => {
        setProblem(e instanceof ApiError ? e.message : 'Could not reach Bluesky.')
        setBusy(null)
      })
  }

  const signedIn = settings.appPasswordStored || settings.signedInWithBluesky
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
            disabled={busy !== null || changed || !settings.handle || !signedIn || limitedUntil !== null}
            onClick={check}
          >
            {busy === 'checking' ? 'Checking…' : 'Check'}
          </Button>
          {settings.signInWithBluesky && (
            <Button
              type="button"
              size="xs"
              variant="outline"
              disabled={busy !== null || !handle.trim() || password.trim() !== '' || limitedUntil !== null}
              onClick={signIn}
            >
              {busy === 'signing-in' ? 'Signing in…' : 'Sign in with Bluesky'}
            </Button>
          )}
          {(settings.handle || signedIn) && (
            <ConfirmButton variant="outline" disabled={busy !== null} onConfirm={remove}>
              Remove
            </ConfirmButton>
          )}
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      {settings.signedInWithBluesky ? (
        <Fact label="Sign-in" value="Bluesky" />
      ) : (
        <Fact label="App password" value={settings.appPasswordStored ? 'Saved' : 'Not set'} />
      )}
      <Field
        label="Handle"
        mono
        value={handle}
        maxLength={253}
        placeholder="yourname.bsky.social"
        onChange={setTypedHandle}
      />
      <PasswordField
        label="App password"
        mono
        value={password}
        placeholder="xxxx-xxxx-xxxx-xxxx"
        onChange={setPassword}
      />

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

/** Pin the next event: Modbot keeps the next calendar date's post pinned on the account's profile. */
function PinCard({ settings, onSaved }: { settings: BlueskySettings; onSaved: (next: BlueskySettings) => void }) {
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const setPin = (pinNextEvent: boolean) => {
    setBusy(true)
    setProblem(null)

    api
      .setBlueskySettings({ pinNextEvent })
      .then(onSaved)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard title="Profile" footer={<Outcome tone="problem">{problem}</Outcome>}>
      <Switch checked={settings.pinNextEvent} disabled={busy} onChange={setPin}>
        Pin the next event
      </Switch>
    </SettingsCard>
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

/** Weekly line-up: once a week the next seven days' public dates go out as one post. */
function LineupCard({ settings, onSaved }: { settings: BlueskySettings; onSaved: (next: BlueskySettings) => void }) {
  const [on, setOn] = useState(settings.lineupOn)
  const [day, setDay] = useState(settings.lineupDay)
  const [hour, setHour] = useState(settings.lineupHour)
  const [timeZone, setTimeZone] = useState(settings.lineupTimeZone ?? '')
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const zoneList = useMemo(() => zones(), [])

  const changed =
    on !== settings.lineupOn ||
    day !== settings.lineupDay ||
    hour !== settings.lineupHour ||
    timeZone !== (settings.lineupTimeZone ?? '')

  const save = () => {
    setBusy(true)
    setSaved(null)
    setProblem(null)

    api
      .setBlueskySettings({ lineupOn: on, lineupDay: day, lineupHour: hour, lineupTimeZone: timeZone })
      .then((next) => {
        onSaved(next)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Weekly line-up"
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
          Weekly line-up
        </Switch>

        <div className="grid gap-3 sm:grid-cols-3">
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
        </div>
      </div>
    </SettingsCard>
  )
}

/** "Tags on every post": the tags every calendar post ends with, after its category's tags. */
function TagsCard({ settings, onSaved }: { settings: BlueskySettings; onSaved: (next: BlueskySettings) => void }) {
  // Null until somebody types, so the field follows the stored tags after a save.
  const [typed, setTyped] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const tags = typed ?? settings.postTags

  const save = () => {
    setBusy(true)
    setSaved(null)
    setProblem(null)

    api
      .setBlueskySettings({ postTags: tags })
      .then((next) => {
        onSaved(next)
        setTyped(null)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Tags"
      footer={
        <>
          <Button type="button" size="xs" disabled={busy || typed === null || typed === settings.postTags} onClick={save}>
            {busy ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Field label="Tags on every post" mono value={tags} maxLength={400} placeholder="#VRChat" onChange={setTyped} />
    </SettingsCard>
  )
}
