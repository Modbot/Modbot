import { useState } from 'react'
import { Input } from '@/components/ui/input'
import { ApiError, api } from '@/lib/api'
import { ErrorText, Field, WizardBody, WizardHeader } from './WizardChrome'
import { WIZARD_FORM_ID, type StepProps } from './types'

/** Spec 7.1 step 1. */
export function AdministratorStep({ eyebrow, run, refresh }: StepProps) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()

    run(async () => {
      setError(null)

      // Also checked on the server, which is the check that matters. This one exists so the
      // answer arrives before a round trip rather than after it.
      if (password !== confirm) {
        setError('The passwords do not match.')
        return false
      }

      try {
        await api.createAdministrator({ username, password, confirmPassword: confirm })
        await refresh()
        return true
      } catch (e) {
        setError(e instanceof ApiError ? e.message : 'Could not create the account.')
        return false
      }
    })
  }

  return (
    <form id={WIZARD_FORM_ID} onSubmit={submit}>
      <WizardHeader eyebrow={eyebrow} title="Create your administrator account">
        This is how you'll sign in to Modbot. It's separate from your VRChat account.
      </WizardHeader>
      <WizardBody>
        <Field label="Username" htmlFor="admin-username">
          <Input
            id="admin-username"
            autoComplete="username"
            autoFocus
            required
            value={username}
            onChange={(e) => setUsername(e.target.value)}
          />
        </Field>
        <Field label="Password" hint="at least 12 characters" htmlFor="admin-password">
          <Input
            id="admin-password"
            type="password"
            autoComplete="new-password"
            required
            minLength={12}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </Field>
        <Field label="Confirm password" htmlFor="admin-confirm">
          <Input
            id="admin-confirm"
            type="password"
            autoComplete="new-password"
            required
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
          />
        </Field>
        <ErrorText>{error}</ErrorText>
      </WizardBody>
    </form>
  )
}
