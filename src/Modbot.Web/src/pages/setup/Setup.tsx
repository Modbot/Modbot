import { useCallback, useEffect, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { SignInWaitBanner } from '@/components/SignInWaitBanner'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'
import { ApiError, api, type OnboardingStatus, type OnboardingStep } from '@/lib/api'
import { openRegisterOnce } from '@/lib/myModbot'
import { AdministratorStep } from './AdministratorStep'
import { ConnectionStep } from './ConnectionStep'
import { GroupStep } from './GroupStep'
import { LinkVRChatStep } from './LinkVRChatStep'
import { OptionalStep } from './OptionalStep'
import { VRChatStep } from './VRChatStep'
import { Brand, StepIndicator, WizardBody, WizardFooter, WizardHeader } from './WizardChrome'
import { Notice } from '@/components/ui/notice'
import { WIZARD_FORM_ID, type StepProps } from './types'

/**
 * Spec 7.1's five steps, plus the administrator's own VRChat link (accounts and access design
 * §4.3), in order.
 *
 * Order, not sequence: each step is independently re-runnable later from settings, so this array
 * is what the wizard suggests rather than a state machine anybody is trapped in. The link step
 * sits after the connection check because the proof goes through the gate.
 */
const STEPS: { key: OnboardingStep; component: (props: StepProps) => React.ReactElement }[] = [
  { key: 'Administrator', component: AdministratorStep },
  { key: 'VRChat', component: VRChatStep },
  { key: 'Connection', component: ConnectionStep },
  { key: 'LinkVRChat', component: LinkVRChatStep },
  { key: 'Group', component: GroupStep },
  { key: 'Optional', component: OptionalStep },
]

export function Setup({ onFinished }: { onFinished: () => void }) {
  const [status, setStatus] = useState<OnboardingStatus | null>(null)
  const [index, setIndex] = useState(0)
  const [busy, setBusy] = useState(false)
  const [fatal, setFatal] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    const next = await api.onboardingStatus()
    setStatus(next)
    return next
  }, [])

  useEffect(() => {
    refresh()
      .then((first) => {
        // Resume where the server says setup got to, rather than at step one. Closing the browser
        // halfway through must not mean creating a second administrator account.
        const resumeAt = STEPS.findIndex((step) => step.key === first.nextStep)
        setIndex(resumeAt < 0 ? STEPS.length - 1 : resumeAt)
      })
      .catch((e: unknown) =>
        setFatal(e instanceof ApiError ? e.message : 'Could not reach the Modbot server.'),
      )
  }, [refresh])

  const run = useCallback(
    (action: () => Promise<boolean>) => {
      setBusy(true)

      void action()
        .then((advance) => {
          if (!advance) return

          if (index >= STEPS.length - 1) onFinished()
          else setIndex(index + 1)
        })
        .finally(() => setBusy(false))
    },
    [index, onFinished],
  )

  if (fatal) {
    return (
      <Shell>
        <div className="p-(--panel-pad)">
          <Notice tone="danger" title="Modbot is not answering.">
            {fatal}
          </Notice>
        </div>
      </Shell>
    )
  }

  if (!status) {
    return (
      <Shell>
        <EmptyRow>Loading…</EmptyRow>
      </Shell>
    )
  }

  // Once an account exists the wizard is an ordinary authenticated page (spec 7.1). Landing here
  // without a session means signing in first -- otherwise the wizard would stay an open door onto
  // re-pointing a running deployment.
  if (status.hasAdministrator && !status.authenticated) {
    return (
      <Shell>
        <WizardHeader eyebrow="Setup" title="Sign in to change setup" />
        <WizardBody>
          <Button type="button" onClick={onFinished}>
            Go to sign in
          </Button>
        </WizardBody>
      </Shell>
    )
  }

  const step = STEPS[index]
  const Step = step.component
  const last = index === STEPS.length - 1

  return (
    <Shell steps={{ total: STEPS.length, current: index + 1 }} groupName={status.group?.name}>
      <Step
        key={step.key}
        eyebrow={`Step ${index + 1} of ${STEPS.length}`}
        status={status}
        run={run}
        refresh={refresh}
        busy={busy}
      />

      <WizardFooter>
        {/*
          The skip affordance, and the honest version of it. The prototype puts "Skip setup" on
          every screen because it is a demo that needs a way into the app; here it appears only
          where skipping actually exists -- the optional step, which spec 7.1 marks skippable, and
          any step being re-run on a deployment that is already set up. A button that cannot do
          what it says is worse than no button.
        */}
        {last ? (
          <Button
            type="button"
            variant="ghost"
            disabled={busy}
            onClick={() => {
              if (!status.onboardingComplete) openRegisterOnce()
              setBusy(true)
              api
                .completeOnboarding()
                .then(onFinished)
                .finally(() => setBusy(false))
            }}
          >
            Skip this step
          </Button>
        ) : status.onboardingComplete ? (
          <Button
            type="button"
            variant="ghost"
            onClick={onFinished}
          >
            Back to Modbot
          </Button>
        ) : null}

        <div className="flex-1" />

        {index > 0 && (
          <Button
            type="button"
            variant="outline"
            disabled={busy}
            onClick={() => setIndex(index - 1)}
          >
            Back
          </Button>
        )}

        <Button type="submit" form={WIZARD_FORM_ID} disabled={busy}>
          {busy ? 'Working…' : last ? 'Finish setup' : 'Continue'}
        </Button>
      </WizardFooter>
    </Shell>
  )
}

function Shell({
  children,
  steps,
  groupName,
}: {
  children: React.ReactNode
  steps?: { total: number; current: number }
  groupName?: string
}) {
  return (
    <div className="flex min-h-dvh flex-col bg-background">
      {/* The same banner as the app shell (foundation spec 4.1.2). It stays empty until somebody is
          signed in, because the health it reads needs a session. */}
      <SignInWaitBanner />
      <div className="grid flex-1 place-items-center p-6">
        <div className="w-full min-w-0 max-w-[520px]">
          <Brand subtitle={groupName} />
          <Card>
            {steps && <StepIndicator total={steps.total} current={steps.current} />}
            {children}
          </Card>
        </div>
      </div>
    </div>
  )
}
