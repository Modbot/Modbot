import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { api, ApiError, type GoogleCalendarCheck, type GoogleCalendarSettings } from '@/lib/api'
import { timeOfDay } from '@/lib/format'
import { ConfirmButton, Fact, Field, Outcome, Placeholder, Switch } from '../fields'
import { SettingsCard, SettingsSection } from '../SettingsCard'

/** The largest key file read. A real one is about 2.3 KB; the server refuses over 16 KB too. */
const MAX_KEY_FILE_BYTES = 16 * 1024

const PUBLIC: Record<GoogleCalendarCheck['public'], string> = {
  all: 'Yes',
  freeBusy: 'Free or busy only',
  no: 'No',
  unknown: 'Unknown',
}

/**
 * Settings → Google Calendar (Google Calendar design §3.1): the service account's key file, the
 * calendar, Check, Sending (step 2), and the calendar's own links once Check finds it public.
 *
 * No words explain the setup (CLAUDE.md): the how-to is the docs page
 * `moderation/google-calendar`. The key is never shown back; only that it is saved and the address
 * it belongs to.
 */
export function GoogleCalendarSection() {
  const [data, setData] = useState<GoogleCalendarSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .googleCalendarSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load Google Calendar settings.')),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="google" title="Google Calendar">
      {error || !data ? (
        <Placeholder tone={error ? 'danger' : 'loading'} onTryAgain={load}>
          {error}
        </Placeholder>
      ) : (
        <>
          <KeyCard settings={data} onSaved={setData} />
          <CalendarCard settings={data} onSaved={setData} />
          {(data.canSend || data.sending || data.removing) && <SendingCard settings={data} onSaved={setData} />}
          {data.links && <LinksCard links={data.links} />}
        </>
      )}
    </SettingsSection>
  )
}

function KeyCard({
  settings,
  onSaved,
}: {
  settings: GoogleCalendarSettings
  onSaved: (next: GoogleCalendarSettings) => void
}) {
  const chooser = useRef<HTMLInputElement>(null)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)

  const upload = (file: File) => {
    setSaved(null)
    setProblem(null)

    if (file.size > MAX_KEY_FILE_BYTES) {
      setProblem('The key file is too big.')
      return
    }

    setBusy(true)
    file
      .text()
      .then((keyFile) => api.setGoogleCalendarSettings({ keyFile }))
      .then((next) => {
        onSaved(next)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  const forget = () => {
    setBusy(true)
    setSaved(null)
    setProblem(null)

    api
      .forgetGoogleCalendarSettings()
      .then(onSaved)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not forget.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Key"
      footer={
        <>
          <Button type="button" size="xs" disabled={busy} onClick={() => chooser.current?.click()}>
            {busy ? 'Saving…' : 'Choose key file'}
          </Button>
          {(settings.keyStored || settings.calendarId) && (
            <ConfirmButton variant="outline" disabled={busy} onConfirm={forget}>
              Forget
            </ConfirmButton>
          )}
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Fact label="Key file" value={settings.keyStored ? 'Saved' : 'Not set'} />
      <CopyValue label="Modbot's Google address" value={settings.address} />
      <input
        ref={chooser}
        type="file"
        accept=".json,application/json"
        className="hidden"
        aria-label="Key file"
        onChange={(e) => {
          const file = e.target.files?.[0]
          if (file) upload(file)
          // The same file can be chosen again after a refusal.
          e.target.value = ''
        }}
      />
    </SettingsCard>
  )
}

function CalendarCard({
  settings,
  onSaved,
}: {
  settings: GoogleCalendarSettings
  onSaved: (next: GoogleCalendarSettings) => void
}) {
  // Null until somebody types, so the field follows the stored id: after a save, which may have
  // read the id out of a pasted link, and after Forget.
  const [typed, setTyped] = useState<string | null>(null)
  const [busy, setBusy] = useState<'saving' | 'checking' | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const limitedUntil = useStillAhead(settings.limitedUntil)

  const calendarId = typed ?? settings.calendarId ?? ''
  const changed = calendarId.trim() !== (settings.calendarId ?? '')

  const save = () => {
    setBusy('saving')
    setSaved(null)
    setProblem(null)

    api
      .setGoogleCalendarSettings({ calendarId: calendarId.trim() })
      .then((next) => {
        onSaved(next)
        setTyped(null)
        setSaved('Saved.')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(null))
  }

  const check = () => {
    setBusy('checking')
    setSaved(null)
    setProblem(null)

    api
      .checkGoogleCalendar()
      .then(onSaved)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not check.'))
      .finally(() => setBusy(null))
  }

  const result = settings.check

  return (
    <SettingsCard
      title="Calendar"
      footer={
        <>
          <Button type="button" size="xs" disabled={busy !== null || !changed} onClick={save}>
            {busy === 'saving' ? 'Saving…' : 'Save'}
          </Button>
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={busy !== null || changed || !settings.keyStored || !settings.calendarId || limitedUntil !== null}
            onClick={check}
          >
            {busy === 'checking' ? 'Checking…' : 'Check'}
          </Button>
          <Outcome tone="ok">{saved}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Field label="Calendar ID" mono value={calendarId} maxLength={1024} onChange={setTyped} />

      {result && (
        <div className="grid gap-3 sm:grid-cols-2">
          <Fact label="Calendar" value={result.calendarName ?? '—'} />
          <Fact label="Time zone" value={result.timeZone ?? '—'} />
          <Fact label="Can change events" value={result.canChangeEvents ? 'Yes' : 'No'} />
          <Fact label="Public" value={PUBLIC[result.public] ?? PUBLIC.unknown} />
        </div>
      )}

      {limitedUntil ? (
        <Outcome tone="problem">Google is limiting Modbot. Try again after {timeOfDay(limitedUntil)}.</Outcome>
      ) : (
        <Outcome tone="problem">{result?.problem}</Outcome>
      )}
    </SettingsCard>
  )
}

/**
 * Sending events to the calendar (Google Calendar design §3.1, step 2): the switch, Add all for the
 * events planned before Google was set up, and Remove Modbot's events, which asks first. Off leaves
 * what is on Google as it is (decision 7).
 */
function SendingCard({
  settings,
  onSaved,
}: {
  settings: GoogleCalendarSettings
  onSaved: (next: GoogleCalendarSettings) => void
}) {
  const [busy, setBusy] = useState<'sending' | 'adding' | 'removing' | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const canSend = !!settings.canSend

  const run = <T,>(kind: 'sending' | 'adding' | 'removing', call: () => Promise<T>, after: (result: T) => void) => {
    setBusy(kind)
    setDone(null)
    setProblem(null)

    call()
      .then(after)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(null))
  }

  const setSending = (on: boolean) =>
    run('sending', () => api.setGoogleCalendarSettings({ sending: on }), onSaved)

  const addAll = () =>
    run('adding', () => api.addAllToGoogleCalendar(), (result) => {
      onSaved(result.settings)
      setDone(`${result.added} added.`)
    })

  const remove = () => run('removing', () => api.removeGoogleCalendarEvents(), onSaved)

  return (
    <SettingsCard
      title="Sending"
      footer={
        <>
          <Button type="button" size="xs" variant="outline" disabled={busy !== null || !canSend} onClick={addAll}>
            {busy === 'adding' ? 'Adding…' : 'Add all'}
          </Button>
          <ConfirmButton variant="outline" disabled={busy !== null || !canSend || !!settings.removing} onConfirm={remove}>
            {settings.removing ? 'Removing…' : "Remove Modbot's events"}
          </ConfirmButton>
          <Outcome tone="ok">{done}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <Switch
        checked={!!settings.sending}
        disabled={busy !== null || (!settings.sending && !canSend)}
        onChange={setSending}
      >
        Sending
      </Switch>
    </SettingsCard>
  )
}

/**
 * The time Google limits Modbot until, while it is still ahead; null once it has passed, so Check
 * comes back by itself. The server sends it only while it is ahead.
 */
function useStillAhead(until: string | null): string | null {
  const [passed, setPassed] = useState<string | null>(null)

  useEffect(() => {
    if (!until) return
    const timer = window.setTimeout(() => setPassed(until), Math.max(0, new Date(until).getTime() - Date.now()))
    return () => window.clearTimeout(timer)
  }, [until])

  return until && passed !== until ? until : null
}

function LinksCard({ links }: { links: NonNullable<GoogleCalendarSettings['links']> }) {
  return (
    <SettingsCard title="Links" span={12}>
      <div className="flex flex-col gap-3">
        <CopyValue label="Subscribe" value={links.subscribe} />
        <CopyValue label="Public page" value={links.publicPage} />
        <CopyValue label="iCal address" value={links.iCal} />
      </div>
    </SettingsCard>
  )
}

/** A value to paste somewhere else, with a Copy button. Absent values are not shown. */
function CopyValue({ label, value }: { label: string; value: string | null }) {
  const [copied, setCopied] = useState(false)

  if (!value) return null

  const copy = () => {
    void navigator.clipboard?.writeText(value).then(() => {
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      <div className="flex items-center gap-2">
        <code
          className="min-w-0 flex-1 truncate rounded-sm border border-(length:--hairline) border-input bg-strip px-2.5 font-mono leading-(--control-h) select-all"
          style={{ height: 'var(--control-h)' }}
          title={value}
        >
          {value}
        </code>
        <Button type="button" variant="outline" size="sm" onClick={copy}>
          {copied ? 'Copied' : 'Copy'}
        </Button>
      </div>
    </div>
  )
}
