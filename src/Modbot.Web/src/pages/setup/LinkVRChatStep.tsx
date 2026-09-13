import { useState } from 'react'
import { VRChatLinkPanel } from '@/components/VRChatLinkPanel'
import { ErrorText, WizardBody, WizardHeader } from './WizardChrome'
import { WIZARD_FORM_ID, type StepProps } from './types'

/**
 * Accounts and access design §4.3: the administrator links their own VRChat account. After the
 * connection check, because the proof goes through the gate.
 *
 * The panel does the work with its own buttons; the footer's Continue only moves on once the
 * server says the link is there.
 */
export function LinkVRChatStep({ eyebrow, status, run, refresh }: StepProps) {
  const [error, setError] = useState<string | null>(null)

  const submit = (event: React.FormEvent) => {
    event.preventDefault()

    run(async () => {
      setError(null)
      const fresh = await refresh()
      if (fresh.vrChatLinked) return true

      setError('Link your VRChat account first — paste your user id, put the code in your bio, and press Check.')
      return false
    })
  }

  return (
    <form id={WIZARD_FORM_ID} onSubmit={submit}>
      <WizardHeader eyebrow={eyebrow} title="Link your VRChat account">
        {status.vrChatLinked
          ? 'Done. Press Continue.'
          : 'Everything Modbot records is attributed to a person. Show which VRChat account is yours.'}
      </WizardHeader>
      <WizardBody>
        <VRChatLinkPanel onLinked={() => void refresh()} />
        <ErrorText>{error}</ErrorText>
      </WizardBody>
    </form>
  )
}
