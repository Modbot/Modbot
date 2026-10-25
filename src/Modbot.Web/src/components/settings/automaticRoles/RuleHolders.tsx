import { useState } from 'react'
import { ChevronRight } from 'lucide-react'
import { EmptyRow } from '@/components/PanelGrid'
import { ApiError } from '@/lib/api'
import { automaticRolesApi, type AutomaticRoleHolder } from '@/lib/automaticRoles'
import { givenLabel } from '@/lib/automaticRoleStanding'

/**
 * On a rule in Settings → Automatic roles: who holds the rule's role and who it applies to but
 * does not hold it. Read when the list is opened, not before, and again each time it is opened.
 */
export function RuleHolders({ ruleId }: { ruleId: string }) {
  const [holders, setHolders] = useState<AutomaticRoleHolder[] | null>(null)
  const [error, setError] = useState('')

  const load = () => {
    automaticRolesApi
      .holders(ruleId)
      .then((list) => {
        setHolders(list.holders)
        setError('')
      })
      .catch((e: unknown) =>
        setError(e instanceof ApiError && e.status === 403 ? 'You do not have permission to see this.' : 'Could not load the list.'),
      )
  }

  return (
    <details className="group" onToggle={(e) => e.currentTarget.open && load()}>
      <summary
        className="flex w-fit cursor-pointer list-none items-center gap-1 rounded-sm text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring [&::-webkit-details-marker]:hidden"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <ChevronRight
          className="size-3.5 shrink-0 transition-transform group-open:rotate-90 motion-reduce:transition-none"
          aria-hidden
        />
        Holders
      </summary>

      <div className="mt-2">
        {error ? (
          <EmptyRow tone="danger" onTryAgain={load}>
            {error}
          </EmptyRow>
        ) : !holders ? (
          <EmptyRow tone="loading" />
        ) : holders.length === 0 ? (
          <EmptyRow>No holders.</EmptyRow>
        ) : (
          <table className="w-full text-left" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="text-muted-foreground">
              <tr>
                <th className="py-1 pr-3 font-normal">Person</th>
                <th className="py-1 pr-3 font-normal">Given</th>
                <th className="py-1 font-normal">Rule</th>
              </tr>
            </thead>
            <tbody>
              {holders.map((holder) => (
                <tr key={`${holder.userId}/${holder.platform}`} className="border-t border-t-(length:--hairline) align-top">
                  <td className="min-w-0 py-1 pr-3 break-words">
                    <div className="font-medium">{holder.username}</div>
                    <div className="text-muted-foreground">{holder.roleName}</div>
                  </td>
                  <td className="py-1 pr-3 whitespace-nowrap">{givenLabel(holder.given)}</td>
                  <td className="py-1 break-words">{holder.ruleMet ? 'Met' : holder.failedReason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </details>
  )
}
