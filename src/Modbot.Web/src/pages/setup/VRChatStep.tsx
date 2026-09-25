import { useState } from 'react'
import { Input } from '@/components/ui/input'
import { ApiError, api, type ConnectionDiagnosis } from '@/lib/api'
import { refreshGateHealth } from '@/lib/useGateHealth'
import { DiagnosisNote } from './DiagnosisNote'
import { ErrorText, Field, WizardBody, WizardHeader } from './WizardChrome'
import { Notice } from '@/components/ui/notice'
import { WIZARD_FORM_ID, type StepProps } from './types'

/**
 * Spec 7.1 step 2 — and the first place a credential is proved rather than accepted.
 *
 * A failure here is shown as a full diagnosis rather than a one-line error, because the most
 * common one on a VPS is a Cloudflare block, and "login failed" would send the operator to check
 * a password that was never wrong.
 */
export function VRChatStep({ eyebrow, status, run, refresh }: StepProps) {
  const [username, setUsername] = useState(status.vrChat.username ?? '')
  const [password, setPassword] = useState('')
  const [totpSecret, setTotpSecret] = useState('')
  const [diagnosis, setDiagnosis] = useState<ConnectionDiagnosis | null>(null)
  const [error, setError] = useState<string | null>(null)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()

    run(async () => {
      setError(null)
      setDiagnosis(null)

      try {
        await api.verifyVRChat({ username, password, totpSecret: totpSecret || null })
        await refresh()
        return true
      } catch (e) {
        if (e instanceof ApiError && e.diagnosis) {
          setDiagnosis(e.diagnosis)
          // The banner is what says how long; ask for it now rather than at the next poll.
          if (e.diagnosis.outcome === 'SignInWaiting') void refreshGateHealth()
          return false
        }

        setError(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.')
        return false
      }
    })
  }

  return (
    <form id={WIZARD_FORM_ID} onSubmit={submit}>
      <WizardHeader eyebrow={eyebrow} title="Connect a VRChat account" />
      <WizardBody>
        <Field label="Email or username" htmlFor="vrc-username">
          <Input
            id="vrc-username"
            autoComplete="off"
            autoFocus
            required
            value={username}
            onChange={(e) => setUsername(e.target.value)}
          />
        </Field>
        <Field label="Password" htmlFor="vrc-password">
          <Input
            id="vrc-password"
            type="password"
            autoComplete="off"
            required
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </Field>
        <Field label="Two-factor secret" htmlFor="vrc-totp">
          <Input
            id="vrc-totp"
            className="font-mono"
            autoComplete="off"
            spellCheck={false}
            placeholder="JBSWY3DPEHPK3PXP"
            value={totpSecret}
            onChange={(e) => setTotpSecret(e.target.value)}
          />
        </Field>

        <Notice tone="warn" title="Use a dedicated account, not your personal one." />

        {diagnosis && <DiagnosisNote diagnosis={diagnosis} />}
        <ErrorText>{error}</ErrorText>
      </WizardBody>
    </form>
  )
}
