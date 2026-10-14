import { useCallback, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Empty, Panel } from '@/components/subject/shared'
import { Outcome } from '@/components/settings/fields'
import { BuddyPicker, StaffChecklist } from '@/components/staffTrial/StaffChecklist'
import { ApiError, api, type StaffTrial, type StaffTrialResponse } from '@/lib/api'
import { useLoad } from '@/lib/useLoad'

/**
 * A staff member's new staff checklist, on the Account tab of their profile: the tasks they
 * answered, what their buddy signed off, and the buddy's note. Somebody who manages users can start
 * one here when the account has none.
 */
export function AccountTrial({ accountId }: { accountId: string }) {
  const load = useCallback(() => api.staffTrial(accountId), [accountId])
  const { data, error, reload } = useLoad<StaffTrialResponse>(load)

  // What the last save answered with, shown in place of the first read.
  const [changed, setChanged] = useState<StaffTrialResponse | null>(null)

  if (error) {
    return (
      <Panel title="New staff checklist" flush>
        <Empty tone="danger" onTryAgain={reload}>{error}</Empty>
      </Panel>
    )
  }

  const shown = changed ?? data
  if (!shown) {
    return (
      <Panel title="New staff checklist" flush>
        <Empty tone="loading" />
      </Panel>
    )
  }

  const set = (trial: StaffTrial | null) => setChanged({ trial, canStart: shown.canStart })

  return (
    <Panel title="New staff checklist">
      {shown.trial ? (
        <StaffChecklist trial={shown.trial} onChange={set} onRemoved={() => set(null)} />
      ) : shown.canStart ? (
        <StartTrial accountId={accountId} onStarted={set} />
      ) : (
        <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          No checklist.
        </p>
      )}
    </Panel>
  )
}

function StartTrial({ accountId, onStarted }: { accountId: string; onStarted: (trial: StaffTrial) => void }) {
  const [buddy, setBuddy] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const start = () => {
    setBusy(true)
    setProblem(null)

    api
      .startStaffTrial(accountId, buddy)
      .then(onStarted)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not start the checklist.'))
      .finally(() => setBusy(false))
  }

  return (
    <div className="flex flex-wrap items-center gap-2">
      <div className="min-w-40">
        <BuddyPicker accountId={accountId} value={buddy} onChange={setBuddy} />
      </div>
      <Button type="button" size="xs" onClick={start} disabled={busy || buddy === ''}>
        {busy ? 'Starting…' : 'Start checklist'}
      </Button>
      <Outcome tone="problem">{problem}</Outcome>
    </div>
  )
}
