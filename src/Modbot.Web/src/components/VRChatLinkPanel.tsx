import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError, api, type VRChatLinkStatus } from '@/lib/api'
import { ErrorText, Field, Note } from '@/pages/setup/WizardChrome'

/**
 * Link your VRChat account (accounts and access design §4.3), in three moves: paste your user id
 * or profile address, put the code Modbot gives you in your bio, press Check.
 *
 * One component for the wizard step, the page an unlinked person lands on, and the account page's
 * "link a different account" — so the words and the order are the same everywhere someone meets
 * this.
 */
export function VRChatLinkPanel({
  onLinked,
  compact = false,
}: {
  /** Called once the link is confirmed, with the fresh status. */
  onLinked?: (status: VRChatLinkStatus) => void
  /** On the account page the "already linked" summary is shown by the page, not here. */
  compact?: boolean
}) {
  const [status, setStatus] = useState<VRChatLinkStatus | null>(null)
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  const load = useCallback(() => api.vrchatLink().then(setStatus), [])

  useEffect(() => {
    load().catch((e: unknown) =>
      setError(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.'),
    )
  }, [load])

  const start = (event: React.FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    setMessage(null)

    api
      .startVRChatLink(input)
      .then((next) => {
        setStatus(next)
        setInput('')
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not start the link.'))
      .finally(() => setBusy(false))
  }

  const check = () => {
    setBusy(true)
    setError(null)
    setMessage(null)

    api
      .checkVRChatLink()
      .then((result) => {
        setStatus(result.status)
        setMessage(result.message)
        if (result.linked) onLinked?.(result.status)
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not check.'))
      .finally(() => setBusy(false))
  }

  const startOver = () => {
    setMessage(null)
    setError(null)
    setStatus((s) => (s ? { ...s, pending: null } : s))
  }

  const copy = (text: string) => {
    void navigator.clipboard?.writeText(text).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  if (!status) {
    return (
      <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {error ?? 'Loading…'}
      </div>
    )
  }

  const pending = status.pending

  if (!pending) {
    return (
      <form onSubmit={start} className="space-y-4">
        {status.linked && !compact && (
          <Note tone="ok" title="Linked.">
            This account is {status.vrChatDisplayName ?? status.vrChatUserId}. You can link a
            different one below.
          </Note>
        )}

        <ol className="list-decimal space-y-2 pl-5 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <li>
            Open your VRChat profile in a new tab and copy your user id, or the address of the page.
          </li>
          <li>Paste it here. Modbot will give you a short code.</li>
          <li>Put the code in your VRChat bio, save, and press Check.</li>
        </ol>

        <div>
          <Button asChild type="button" variant="outline" size="sm">
            <a href={status.profileUrl} target="_blank" rel="noopener noreferrer">
              Open my VRChat profile
            </a>
          </Button>
        </div>

        <Field label="Your VRChat user id or profile address" htmlFor="vrchat-link-input">
          <Input
            id="vrchat-link-input"
            className="font-mono"
            autoComplete="off"
            spellCheck={false}
            placeholder="usr_… or https://vrchat.com/home/user/…"
            required
            value={input}
            onChange={(e) => setInput(e.target.value)}
          />
        </Field>

        <ErrorText>{error}</ErrorText>

        <Button type="submit" size="sm" disabled={busy || !input.trim()}>
          {busy ? 'Working…' : 'Continue'}
        </Button>
      </form>
    )
  }

  return (
    <div className="space-y-4">
      <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        Put this code anywhere in the bio of{' '}
        <span className="font-mono text-foreground">{pending.vrChatUserId}</span>, save your
        profile, then press Check. You can take it out again once you are linked.
      </div>

      <div className="flex items-center gap-2">
        <code
          className="rounded-md border bg-secondary px-3 py-2 font-mono text-[15px] font-semibold tracking-wide select-all"
          style={{ borderWidth: 'var(--hairline)' }}
        >
          {pending.code}
        </code>
        <Button type="button" variant="outline" size="sm" onClick={() => copy(pending.code)}>
          {copied ? 'Copied' : 'Copy'}
        </Button>
        <Button asChild type="button" variant="ghost" size="sm">
          <a href={status.profileUrl} target="_blank" rel="noopener noreferrer">
            Open my profile
          </a>
        </Button>
      </div>

      {message && <Note tone={status.linked ? 'ok' : 'info'}>{message}</Note>}
      <ErrorText>{error}</ErrorText>

      <div className="flex items-center gap-2">
        <Button type="button" size="sm" onClick={check} disabled={busy || status.linked}>
          {busy ? 'Checking…' : 'Check'}
        </Button>
        <Button type="button" variant="ghost" size="sm" onClick={startOver} disabled={busy}>
          Start over
        </Button>
        <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {pending.checksLeft} {pending.checksLeft === 1 ? 'check' : 'checks'} left on this code
        </span>
      </div>
    </div>
  )
}
