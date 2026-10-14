import { useEffect, useState } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { StaffChecklist } from '@/components/staffTrial/StaffChecklist'
import { api, type MyStaffTrials, type StaffTrial } from '@/lib/api'

/**
 * On the account page: the signed-in person's own new staff checklist, where they write their
 * answers, and the checklist of everyone they are buddy to, where they sign off. Draws nothing for
 * somebody with neither, and nothing while the read fails: the page is for the account, and a
 * checklist that cannot be read leaves the rest of it as it was.
 */
export function MyTrials() {
  const [mine, setMine] = useState<MyStaffTrials | null>(null)

  useEffect(() => {
    let live = true

    api
      .myStaffTrials()
      .then((next) => live && setMine(next))
      .catch(() => undefined)

    return () => {
      live = false
    }
  }, [])

  if (!mine) return null

  const replace = (next: StaffTrial) =>
    setMine((was) =>
      was && {
        own: was.own?.accountId === next.accountId ? next : was.own,
        asBuddy: was.asBuddy.map((t) => (t.accountId === next.accountId ? next : t)),
      },
    )

  const drop = (accountId: string) =>
    setMine((was) =>
      was && {
        own: was.own?.accountId === accountId ? null : was.own,
        asBuddy: was.asBuddy.filter((t) => t.accountId !== accountId),
      },
    )

  return (
    <>
      {mine.own && (
        <TrialCard title="Your new staff checklist" trial={mine.own} onChange={replace} onRemoved={() => drop(mine.own!.accountId)} />
      )}
      {mine.asBuddy.map((trial) => (
        <TrialCard
          key={trial.accountId}
          title={`${trial.username}'s new staff checklist`}
          trial={trial}
          onChange={replace}
          onRemoved={() => drop(trial.accountId)}
        />
      ))}
    </>
  )
}

function TrialCard({ title, trial, onChange, onRemoved }: {
  title: string
  trial: StaffTrial
  onChange: (next: StaffTrial) => void
  onRemoved: () => void
}) {
  return (
    <Card className="lg:col-span-2">
      <CardHeader>
        <CardTitle>{title}</CardTitle>
      </CardHeader>
      <CardContent>
        <StaffChecklist trial={trial} onChange={onChange} onRemoved={onRemoved} />
      </CardContent>
    </Card>
  )
}
