import { useCallback } from 'react'
import { Badge } from '@/components/ui/badge'
import { JsonView } from '@/components/JsonView'
import { FactList, Field, Note } from '@/components/subject/shared'
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
    <div
      className="rounded-md border px-3 py-2"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <div className="font-medium">Modbot account</div>

      <div className="mt-1 flex flex-col gap-1.5">
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
          <Field label="Made">{formatDay(account.createdAt)}</Field>
          <Field label="Last signed in">
            {account.lastLoginAt ? dateTime(account.lastLoginAt) : '—'}
          </Field>
        </div>
      </div>
    </div>
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
    <div className="flex min-h-0 flex-col gap-3 overflow-auto p-4">
      <div className="font-medium">Signed in, changed and did</div>

      {error && <Note className="text-destructive">{error}</Note>}
      {!error && !data && <Note>Loading…</Note>}
      {data && <FactList entries={data.entries} empty="Nothing recorded yet." />}
    </div>
  )
}

/** The account as the API answers it, for the JSON tab. */
export function AccountRecord({ account }: { account: PersonAccount }) {
  return <JsonView title="Modbot account" value={account} />
}
