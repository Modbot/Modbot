import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { ApiError, api, type McpSignInView } from '@/lib/api'
import { Brand, ErrorText, WizardBody, WizardFooter, WizardHeader } from './setup/WizardChrome'

/**
 * An AI app asking to act as the signed-in person on the MCP server (`/connect`).
 *
 * The app sent the browser to `/mcp/authorize`, which checked what it could and sent it here with
 * the same query. This page shows who is asking and which tools they would get, and one press
 * answers: the server makes the code and says where the browser goes next, which is back to the
 * app.
 *
 * Outside the app shell, like sign-in and pairing: the app is waiting, and the sidebar would be a
 * way to wander off.
 */
export function Connect() {
  const search = window.location.search
  const query = new URLSearchParams(search)

  const [view, setView] = useState<McpSignInView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api
      .mcpSignIn(search)
      .then(setView)
      .catch((e: unknown) =>
        setError(
          e instanceof ApiError && e.status === 403
            ? 'Your account cannot use AI chat.'
            : e instanceof ApiError
              ? e.message
              : 'Could not reach the Modbot server.',
        ),
      )
  }, [search])

  const answer = (approve: boolean) => {
    setBusy(true)
    setError(null)

    api
      .answerMcpSignIn({
        clientId: query.get('client_id') ?? '',
        redirectUri: query.get('redirect_uri'),
        state: query.get('state'),
        codeChallenge: query.get('code_challenge'),
        codeChallengeMethod: query.get('code_challenge_method'),
        scope: query.get('scope'),
        resource: query.get('resource'),
        approve,
      })
      .then(({ redirectTo }) => window.location.assign(redirectTo))
      .catch((e: unknown) => {
        setError(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.')
        setBusy(false)
      })
  }

  return (
    <div className="grid min-h-dvh place-items-center bg-background p-6">
      <div className="w-full max-w-[460px]">
        <Brand subtitle="connect" />
        <div className="overflow-hidden rounded-xl border bg-card shadow-lg">
          <WizardHeader eyebrow="Connect" title={view?.clientName ?? 'AI app'}>
            {view && `Sends you back to ${view.redirectHost}`}
          </WizardHeader>

          <WizardBody>
            <ErrorText>{error}</ErrorText>

            {view && (
              <div className="space-y-2">
                <div className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
                  Tools
                </div>
                {view.tools.length === 0 ? (
                  <p className="m-0 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                    None
                  </p>
                ) : (
                  <ul className="m-0 grid list-none gap-x-4 gap-y-1 p-0 sm:grid-cols-2" style={{ fontSize: 'var(--text-small)' }}>
                    {view.tools.map((t) => (
                      <li key={t.name}>{t.label}</li>
                    ))}
                  </ul>
                )}
              </div>
            )}

            {!view && !error && (
              <p className="m-0 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                Loading…
              </p>
            )}
          </WizardBody>

          <WizardFooter>
            <Button variant="secondary" disabled={busy || !view} onClick={() => answer(false)}>
              Cancel
            </Button>
            <Button className="ml-auto" disabled={busy || !view} onClick={() => answer(true)}>
              {busy ? 'Connecting…' : 'Allow'}
            </Button>
          </WizardFooter>
        </div>
      </div>
    </div>
  )
}
