import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError, api, type ConnectionDiagnosis } from '@/lib/api'
import { DiagnosisNote } from './DiagnosisNote'
import { ErrorText, Field, WizardBody, WizardHeader } from './WizardChrome'
import { WIZARD_FORM_ID, type StepProps } from './types'

/**
 * Spec 7.1.1 — the step worth getting right.
 *
 * The shape of this screen follows the shape of the problem. When the check passes, the proxy
 * fields are collapsed behind a disclosure, because most operators will never need them and a
 * visible empty proxy form invites somebody to fill it in and break a working install. When it
 * fails with a Cloudflare block, they open automatically and the retest lives beside them, so the
 * operator never leaves the step. When it fails any other way the fields stay shut, because
 * offering a proxy there is worse than offering nothing -- it is confident, specific, and wrong.
 */
export function ConnectionStep({ eyebrow, status, run, refresh, busy }: StepProps) {
  const [diagnosis, setDiagnosis] = useState<ConnectionDiagnosis | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [testing, setTesting] = useState(false)

  const [useProxy, setUseProxy] = useState(status.connection.proxyUrl !== null)
  const [proxyUrl, setProxyUrl] = useState(status.connection.proxyUrl ?? '')
  const [proxyUsername, setProxyUsername] = useState(status.connection.proxyUsername ?? '')
  const [proxyPassword, setProxyPassword] = useState('')

  const test = async () => {
    setError(null)
    setTesting(true)

    try {
      const result = await api.testConnection(
        useProxy
          ? {
              useProxy: true,
              proxyUrl,
              proxyUsername,
              // Omitted rather than sent empty, so re-testing after fixing a typo in the URL
              // keeps the stored password instead of silently clearing it.
              ...(proxyPassword ? { proxyPassword } : {}),
            }
          : { useProxy: false },
      )

      setDiagnosis(result)

      // A block opens the proxy fields by itself. Making the operator find a disclosure triangle
      // at the exact moment they have been told they need a proxy is a small cruelty.
      if (result.proxyWouldHelp) setUseProxy(true)

      await refresh()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.')
    } finally {
      setTesting(false)
    }
  }

  const submit = (event: React.FormEvent) => {
    event.preventDefault()

    run(async () => {
      // Spec 7.1.1: on a WAF block "a proxy is required to continue". The same is true of every
      // other failure here for a less dramatic reason -- nothing downstream works until this
      // does, so advancing only moves the confusion one screen along.
      //
      // Refused on submit rather than by disabling Continue: a disabled button explains nothing,
      // and "why can't I click this?" is the question this whole step exists to answer.
      if (!diagnosis || diagnosis.outcome !== 'Ok') {
        setError(
          !diagnosis
            ? 'Test the connection first.'
            : diagnosis.proxyWouldHelp
              ? 'Modbot cannot reach VRChat from this host. Add a proxy and test again.'
              : 'Modbot cannot reach VRChat. Test again once it is fixed.',
        )
        return false
      }

      return true
    })
  }

  return (
    <form id={WIZARD_FORM_ID} onSubmit={submit}>
      <WizardHeader eyebrow={eyebrow} title="Check the connection" />
      <WizardBody>
        {diagnosis && <DiagnosisNote diagnosis={diagnosis} />}
        <ErrorText>{error}</ErrorText>

        {/*
          The disclosure stays available whatever the outcome, collapsed, exactly as the design
          prototype has it -- an operator who already knows their host needs a proxy should not
          have to fix an unrelated failure first to reach the field. What changes with the
          diagnosis is whether Modbot *suggests* one: that lives in the diagnosis text and in
          proxyWouldHelp, never in whether the form exists.
        */}
        {!useProxy ? (
          <details className="group" open={false}>
            <summary
              className="cursor-pointer text-muted-foreground marker:text-muted-foreground/50"
              style={{ fontSize: 'var(--text-small)' }}
            >
              Use a proxy
            </summary>
            <div className="pt-3">
              <ProxyFields
                proxyUrl={proxyUrl}
                setProxyUrl={setProxyUrl}
                proxyUsername={proxyUsername}
                setProxyUsername={setProxyUsername}
                proxyPassword={proxyPassword}
                setProxyPassword={setProxyPassword}
                passwordStored={status.connection.proxyPasswordStored}
                onUse={() => setUseProxy(true)}
              />
            </div>
          </details>
        ) : null}

        {useProxy && (
          <ProxyFields
            proxyUrl={proxyUrl}
            setProxyUrl={setProxyUrl}
            proxyUsername={proxyUsername}
            setProxyUsername={setProxyUsername}
            proxyPassword={proxyPassword}
            setProxyPassword={setProxyPassword}
            passwordStored={status.connection.proxyPasswordStored}
          />
        )}

        <div className="flex items-center gap-2">
          {/* Behind a press rather than fired on arrival, because the request costs rate-limit
              budget and this step is re-runnable from settings -- an operator opening it to change
              a proxy should not spend a login just by looking. */}
          <Button
            type="button"
            variant="outline"
            onClick={() => void test()}
            disabled={testing || busy}
          >
            {testing ? 'Testing…' : diagnosis ? 'Test again' : 'Test connection'}
          </Button>

          {useProxy && (
            <Button
              type="button"
              variant="ghost"
              onClick={() => {
                setUseProxy(false)
                setProxyPassword('')
              }}
              disabled={testing || busy}
            >
              Test without the proxy
            </Button>
          )}
        </div>
      </WizardBody>
    </form>
  )
}

function ProxyFields({
  proxyUrl,
  setProxyUrl,
  proxyUsername,
  setProxyUsername,
  proxyPassword,
  setProxyPassword,
  passwordStored,
  onUse,
}: {
  proxyUrl: string
  setProxyUrl: (v: string) => void
  proxyUsername: string
  setProxyUsername: (v: string) => void
  proxyPassword: string
  setProxyPassword: (v: string) => void
  passwordStored: boolean
  onUse?: () => void
}) {
  return (
    <div className="space-y-4" onFocus={onUse}>
      <Field label="Proxy URL" htmlFor="proxy-url">
        <Input
          id="proxy-url"
          className="font-mono"
          spellCheck={false}
          placeholder="http://proxy.example.com:11202"
          value={proxyUrl}
          onChange={(e) => setProxyUrl(e.target.value)}
        />
      </Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Username" htmlFor="proxy-username">
          <Input
            id="proxy-username"
            autoComplete="off"
            value={proxyUsername}
            onChange={(e) => setProxyUsername(e.target.value)}
          />
        </Field>
        <Field
          label="Password"
          hint={passwordStored ? 'stored' : undefined}
          htmlFor="proxy-password"
        >
          <Input
            id="proxy-password"
            type="password"
            autoComplete="off"
            value={proxyPassword}
            onChange={(e) => setProxyPassword(e.target.value)}
          />
        </Field>
      </div>
    </div>
  )
}
