import { useCallback } from 'react'
import { Badge } from '@/components/ui/badge'
import { EmptyRow } from '@/components/PanelGrid'
import { FactList, Field, Panel } from '@/components/subject/shared'
import { api, type PersonAccount } from '@/lib/api'
import { dateTime } from '@/components/charts'
import { formatDay } from '@/lib/format'
import { useLoad } from '@/lib/useLoad'

/**
 * The Modbot half of a person: the account they sign in with, and everything it has done.
 *
 * This part had nowhere to live before. An account's sign-ins, role changes and settings changes
 * are recorded against the account as the subject, and its kicks and bans against the account as
 * the actor, so neither the person's popup nor the audit log's subject filter could show the whole
 * of it (one view per person design §5). `?account=` on the audit log answers both halves at once.
 */

/** The account, on the left of the popup. */
export function AccountCard({ account }: { account: PersonAccount }) {
  return (
    <Panel title="Modbot account">
      <div className="flex flex-col gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
        <div className="flex flex-wrap items-center gap-1">
          <span className="font-medium">{account.username}</span>
          {account.isDisabled && <Badge variant="outline">Disabled</Badge>}
        </div>

        {account.roles.length > 0 && (
          <div className="flex flex-wrap items-center gap-1">
            <span className="text-muted-foreground">Roles:</span>
            {account.roles.map((role) => (
              <Badge key={role} variant="secondary">
                {role}
              </Badge>
            ))}
          </div>
        )}

        <div className="flex flex-wrap gap-x-6 gap-y-1">
          <Field label="Made">
            <span className="font-mono">{formatDay(account.createdAt)}</span>
          </Field>
          <Field label="Last signed in">
            <span className="font-mono">{account.lastLoginAt ? dateTime(account.lastLoginAt) : '—'}</span>
          </Field>
        </div>
      </div>
    </Panel>
  )
}

const ACCOUNT_LIMIT = 100

/**
 * Everything the account did and everything done to it, newest first.
 *
 * One read, not two: the server merges the subject and actor halves so the newest hundred are
 * the newest hundred of both rather than of either.
 */
export function AccountHistory({ accountId }: { accountId: string }) {
  const load = useCallback(() => api.audit({ account: accountId, limit: ACCOUNT_LIMIT }), [accountId])
  const { data, error } = useLoad(load)

  return (
    <Panel title="Signed in, changed and did" flush>
      {error && <EmptyRow tone="danger">{error}</EmptyRow>}
      {!error && !data && <EmptyRow>Loading…</EmptyRow>}
      {data && <FactList entries={data.entries} empty="Nothing recorded yet." />}
    </Panel>
  )
}

/** The account as the API answers it, for the JSON tab. */
