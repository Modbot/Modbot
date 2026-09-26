import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError, api, type LinkPageStatus } from '@/lib/api'
import { Brand, ErrorText, Field, WizardBody, WizardHeader } from './setup/WizardChrome'
import { Notice } from '@/components/ui/notice'

/** What `?error=` from the sign-in redirect means, in the words the page shows. */
const SIGN_IN_ERRORS: Record<string, string> = {
  cancelled: 'Discord sign-in was cancelled.',
  'sign-in-expired': 'Discord sign-in expired. Try again.',
  discord: 'Discord sign-in failed.',
  'not-set-up': 'Account linking is not set up.',
}

/** Read once, then taken out of the address so a refresh does not show it again. */
function takeSignInError(): string | null {
  const params = new URLSearchParams(window.location.search)
  const code = params.get('error')
  if (!code) return null

  window.history.replaceState(window.history.state, '', window.location.pathname)
  return SIGN_IN_ERRORS[code] ?? 'Discord sign-in failed.'
}

/**
 * The member link page (Discord account linking design §3): sign in with Discord, name a VRChat
 * account, put the code in its bio, check.
 *
 * Needs no Modbot account and sits outside the app shell. Either side can be done first -- a member
 * who came from VRChat can get their code before signing in with Discord -- but Check waits for
 * Discord sign-in, because it spends a VRChat request and the server counts those per Discord
 * account.
 */
export function LinkAccounts() {
  const [status, setStatus] = useState<LinkPageStatus | null>(null)
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(takeSignInError)
  const [message, setMessage] = useState<{ text: string; ok: boolean } | null>(null)
  const [changing, setChanging] = useState(false)
  const [copied, setCopied] = useState(false)

  const load = useCallback(() => api.linkPage().then(setStatus), [])

  useEffect(() => {
    load().catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not reach Modbot.'))
  }, [load])

  const run = (work: () => Promise<void>, fallback: string) => {
    setBusy(true)
    setError(null)
    setMessage(null)
    work()
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : fallback))
      .finally(() => setBusy(false))
  }

  const nameAccount = (event: React.FormEvent) => {
    event.preventDefault()
    run(async () => {
      setStatus(await api.linkVRChat(input))
      setInput('')
      setChanging(false)
    }, 'Could not get a code.')
  }

  const check = () =>
    run(async () => {
      const result = await api.linkCheck()
      setStatus(result.status)
      setMessage({ text: result.message, ok: result.linked })
    }, 'Could not check.')

  const unlink = () => run(async () => setStatus(await api.linkUnlink()), 'Could not unlink.')

  const signOut = () =>
    run(async () => {
      await api.linkSignOut()
      await load()
    }, 'Could not sign out.')

  const copy = (text: string) => {
    void navigator.clipboard?.writeText(text).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <div className="grid min-h-dvh place-items-center bg-background p-6">
      <div className="w-full min-w-0 max-w-[520px]">
        <Brand />
        <Card>
          <WizardHeader eyebrow={status?.serverName ?? 'Discord'} title="Link your VRChat account" />
          <WizardBody>
            {!status ? (
              error ? (
                <ErrorText>{error}</ErrorText>
              ) : (
                <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  Loading…
                </div>
              )
            ) : !status.available ? (
              <ErrorText>Account linking is not set up.</ErrorText>
            ) : (
              <Steps
                status={status}
                busy={busy}
                input={input}
                setInput={setInput}
                changing={changing}
                setChanging={setChanging}
                copied={copied}
                onCopy={copy}
                onName={nameAccount}
                onCheck={check}
                onUnlink={unlink}
                onSignOut={signOut}
                message={message}
                error={error}
              />
            )}
          </WizardBody>
        </Card>
      </div>
    </div>
  )
}

function Steps({
  status,
  busy,
  input,
  setInput,
  changing,
  setChanging,
  copied,
  onCopy,
  onName,
  onCheck,
  onUnlink,
  onSignOut,
  message,
  error,
}: {
  status: LinkPageStatus
  busy: boolean
  input: string
  setInput: (value: string) => void
  changing: boolean
  setChanging: (value: boolean) => void
  copied: boolean
  onCopy: (text: string) => void
  onName: (event: React.FormEvent) => void
  onCheck: () => void
  onUnlink: () => void
  onSignOut: () => void
  message: { text: string; ok: boolean } | null
  error: string | null
}) {
  const { discord, pending, link } = status

  if (discord && link) {
    return (
      <div className="space-y-4">
        <Notice tone="ok" title="Linked">
          {discord.username} · {link.vrChatDisplayName ?? link.vrChatUserId}
        </Notice>
        <ErrorText>{error}</ErrorText>
        <div className="flex items-center gap-2">
          <Button type="button" variant="outline" size="sm" onClick={onUnlink} disabled={busy}>
            Unlink
          </Button>
          <Button type="button" variant="ghost" size="sm" onClick={onSignOut} disabled={busy}>
            Sign out
          </Button>
        </div>
      </div>
    )
  }

  const showCode = pending && !changing

  return (
    <div className="space-y-5">
      <section className="space-y-2">
        <StepTitle done={!!discord}>Discord</StepTitle>
        {discord ? (
          <div className="flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
            <span className="font-medium">{discord.username}</span>
            <Button type="button" variant="ghost" size="sm" onClick={onSignOut} disabled={busy}>
              Sign out
            </Button>
          </div>
        ) : (
          <Button asChild size="sm">
            <a href={api.linkSignInUrl}>Sign in with Discord</a>
          </Button>
        )}
      </section>

      <section className="space-y-2">
        <StepTitle done={false}>VRChat</StepTitle>
        {showCode ? (
          <div className="space-y-3">
            <div className="flex flex-wrap items-center gap-2">
              <code
                className="rounded-sm border border-(length:--hairline) bg-strip px-3 py-2 font-mono font-semibold select-all"
                style={{ fontSize: 'var(--text-base)' }}
              >
                {pending.code}
              </code>
              <Button type="button" variant="outline" size="sm" onClick={() => onCopy(pending.code)}>
                {copied ? 'Copied' : 'Copy'}
              </Button>
              <Button asChild type="button" variant="ghost" size="sm">
                <a href={status.profileUrl} target="_blank" rel="noopener noreferrer">
                  Open my VRChat profile
                </a>
              </Button>
            </div>
            <div className="flex items-center gap-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              <span className="font-mono">{pending.vrChatUserId}</span>
              <Button type="button" variant="ghost" size="sm" onClick={() => setChanging(true)} disabled={busy}>
                Change
              </Button>
            </div>
          </div>
        ) : (
          <form onSubmit={onName} className="space-y-3">
            <Field label="VRChat profile link or user id" htmlFor="link-vrchat-input">
              <Input
                id="link-vrchat-input"
                className="font-mono"
                autoComplete="off"
                spellCheck={false}
                placeholder="https://vrchat.com/home/user/usr_…"
                required
                value={input}
                onChange={(e) => setInput(e.target.value)}
              />
            </Field>
            <div className="flex items-center gap-2">
              <Button type="submit" size="sm" disabled={busy || !input.trim()}>
                {busy ? 'Working…' : 'Get code'}
              </Button>
              <Button asChild type="button" variant="ghost" size="sm">
                <a href={status.profileUrl} target="_blank" rel="noopener noreferrer">
                  Open my VRChat profile
                </a>
              </Button>
              {pending && (
                <Button type="button" variant="ghost" size="sm" onClick={() => setChanging(false)}>
                  Cancel
                </Button>
              )}
            </div>
          </form>
        )}
      </section>

      {message && <Notice tone={message.ok ? 'ok' : 'neutral'}>{message.text}</Notice>}
      <ErrorText>{error}</ErrorText>

      {showCode && (
        <div className="flex items-center gap-2">
          <Button type="button" size="sm" onClick={onCheck} disabled={busy || !discord}>
            {busy ? 'Checking…' : 'Check'}
          </Button>
          {pending.checksLeft !== null && (
            <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              <span className="font-mono">{pending.checksLeft}</span> {pending.checksLeft === 1 ? 'check' : 'checks'} left
            </span>
          )}
        </div>
      )}
    </div>
  )
}

function StepTitle({ done, children }: { done: boolean; children: React.ReactNode }) {
  return (
    <div className="flex items-center gap-2 font-label">
      <span
        className={done ? 'size-2 shrink-0 bg-primary' : 'size-2 shrink-0 border border-muted-foreground'}
        aria-hidden="true"
      />
      {children}
    </div>
  )
}
