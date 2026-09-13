import type { OnboardingStatus } from '@/lib/api'

/**
 * The id the footer's Continue button submits.
 *
 * Each step renders its own `<form>` and the footer button targets it with `form=`. That is what
 * makes Enter submit the step -- which matters, because this is five screens of typing and
 * reaching for the mouse after every field is how a setup wizard earns its reputation.
 */
export const WIZARD_FORM_ID = 'wizard-step'

export type StepProps = {
  /** "Step 2 of 5", supplied by the orchestrator so no step has to know where it sits. */
  eyebrow: string

  /** What the server currently believes is configured. */
  status: OnboardingStatus

  /**
   * Runs a step's action with the footer's busy state around it, and advances when it returns
   * true. Returning false leaves the operator on the step with whatever it put on screen — which
   * is the normal outcome for the connection check, not an error path.
   */
  run: (action: () => Promise<boolean>) => void

  /** Re-reads status from the server. */
  refresh: () => Promise<OnboardingStatus>

  busy: boolean
}
