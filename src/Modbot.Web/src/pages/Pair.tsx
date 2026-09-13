import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError, api, type IssuedPairingCode } from '@/lib/api'
import { encodePairingToken, pairingLink } from '@/lib/pairingToken'
import { Brand, ErrorText, Note, WizardBody, WizardFooter, WizardHeader } from './setup/WizardChrome'

type Issued = IssuedPairingCode & { token: string; link: string }

function secondsUntil(iso: string): number {
  return Math.max(0, Math.floor((new Date(iso).getTime() - Date.now()) / 1000))
}

/**
 * Pairing the desktop client (`/pair`).
 *
 * One button. Pressing it opens a `modbot-client://` link that Windows hands to the client, which
 * decodes the address and the one-time code inside it and pairs. Nothing has to be typed, which is
 * the point: the old flow asked a moderator -- often already in a headset -- to copy a server
 * address and an eight-character code from one screen into another.
 *
 * The copy button is the same token for the case where the link does not open: a browser that
 * asks and is refused, a client installed but not yet run once, a machine where the link goes to
 * the wrong program. Both paths carry the same thing and end in the same place in the client.
 *
 * Outside the app shell, like sign-in, because this is a landing page: `my.modbot.co` will send a
 * moderator here, and the sidebar of a deployment they may never otherwise open would be noise.
 */
export function Pair() {
  const [issued, setIssued] = useState<Issued | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(true)
  const [copied, setCopied] = useState<'yes' | 'no' | null>(null)
  const [secondsLeft, setSecondsLeft] = useState<number | null>(null)

  const issue = useCallback(
    () =>
      api
        .issuePairingCode()
        .then((next) => {
          const token = encodePairingToken(window.location.origin, next.code)
          setIssued({ ...next, token, link: pairingLink(token) })
          setSecondsLeft(secondsUntil(next.expiresAt))
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.'),
        )
        .finally(() => setBusy(false)),
    [],
  )

  const reissue = () => {
    setBusy(true)
    setError(null)
    setCopied(null)
    void issue()
  }

  // A code is issued on arrival rather than on a press, so the button is a plain link the browser
  // can hand straight to Windows. A link minted inside a click handler after a round trip is the
  // version pop-up blockers and protocol-handler prompts refuse.
  useEffect(() => {
    void issue()
  }, [issue])

  // The countdown is cosmetic and the server's clock is the one that counts; this only tells the
  // moderator when to expect "expired" rather than letting them find out from the client.
  useEffect(() => {
    if (!issued) return

    const timer = window.setInterval(() => setSecondsLeft(secondsUntil(issued.expiresAt)), 1000)
    return () => window.clearInterval(timer)
  }, [issued])

  const expired = secondsLeft === 0

  const copy = () => {
    if (!issued) return

    navigator.clipboard
      .writeText(issued.token)
      .then(() => setCopied('yes'))
      .catch(() => setCopied('no'))
  }

  return (
    <div className="grid min-h-screen place-items-center bg-background p-6">
      <div className="w-full max-w-[460px]">
        <Brand subtitle="desktop client" />
        <div className="overflow-hidden rounded-xl border bg-card shadow-lg">
          <WizardHeader eyebrow="Pair" title="Pair this computer">
            The Modbot desktop client reports which of this group's instances you are in. Pairing
            gives your copy of it a token for this server, and nothing else.
          </WizardHeader>

          <WizardBody>
            {error && <ErrorText>{error}</ErrorText>}

            {issued && !expired && (
              <>
                <Button asChild className="w-full" style={{ height: 'var(--control-h)' }}>
                  <a href={issued.link}>Open in Modbot</a>
                </Button>

                <p className="m-0 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  Your browser will ask to open Modbot. Say yes, and the client window shows
                  "Paired". Nothing happened? Copy the pairing token below and paste it into the
                  client's Servers page instead.
                </p>

                <div className="flex gap-2">
                  <Input readOnly value={issued.token} aria-label="Pairing token" className="font-mono" />
                  <Button variant="secondary" onClick={copy} style={{ height: 'var(--control-h)' }}>
                    {copied === 'yes' ? 'Copied' : 'Copy pairing token'}
                  </Button>
                </div>

                {copied === 'no' && (
                  <Note tone="warn">
                    Your browser would not copy it. Select the token above and copy it yourself.
                  </Note>
                )}
              </>
            )}

            {issued && expired && (
              <Note tone="warn" title="This link has expired.">
                A pairing link works for five minutes and once. Get a new one and try again.
              </Note>
            )}

            {!issued && !error && (
              <p className="m-0 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                Preparing a pairing link…
              </p>
            )}
          </WizardBody>

          <WizardFooter>
            <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {issued && !expired && secondsLeft !== null
                ? `Works once, for the next ${Math.floor(secondsLeft / 60)}:${String(secondsLeft % 60).padStart(2, '0')}`
                : ' '}
            </span>
            <div className="flex-1" />
            <Button
              variant={issued && expired ? 'default' : 'ghost'}
              size="sm"
              disabled={busy}
              onClick={reissue}
            >
              {busy ? 'Preparing…' : 'Get a new link'}
            </Button>
          </WizardFooter>
        </div>

        <p className="mt-4 text-center text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Do not have the client yet? Install it first, run it once, then come back to this page.
        </p>
      </div>
    </div>
  )
}
