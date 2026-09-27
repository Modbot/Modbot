import { useEffect, useState } from 'react'
import { RefreshCw, X } from 'lucide-react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { PersonLink } from '@/components/facts'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { VRChatPermissionMissing } from '@/components/VRChatPermissionMissing'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { api, ApiError, type CurrentUser, type GroupInviteList, type GroupInviteRow, type MissingGroupPermission } from '@/lib/api'
import { formatDay } from '@/lib/format'
import type { PageId } from '@/lib/nav'
import { missingPermissionOf } from '@/lib/vrchatPermissions'
import { GroupHeaderFor } from './GroupHeader'

/**
 * The VRChat page's Invites tab: the people the group has invited who have not answered yet, as
 * vrchat.com lists them, newest first, each with a Cancel that asks first.
 *
 * **Every request to VRChat here is one somebody asked for.** The list is read once when the tab
 * opens, once per page turned and once per Refresh; a Cancel is one request. VRChat sends no total,
 * so paging is Previous and Next.
 */
export function GroupInvites({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const [page, setPage] = useState(1)
  const [asked, setAsked] = useState(0)
  const wanted = `${page}:${asked}`
  const [read, setRead] = useState<{
    key: string
    list: GroupInviteList | null
    error: string | null
    missing: MissingGroupPermission | null
  } | null>(null)
  const loading = read?.key !== wanted
  const list = read?.list ?? null
  const error = read?.key === wanted ? read.error : null
  const missing = read?.key === wanted ? read.missing : null

  const [cancelling, setCancelling] = useState<GroupInviteRow | null>(null)

  useEffect(() => {
    let cancelled = false
    const key = `${page}:${asked}`

    api
      .groupInvites(page)
      .then((next) => {
        if (!cancelled) setRead({ key, list: next, error: null, missing: null })
      })
      .catch((e: unknown) => {
        if (!cancelled)
          setRead((current) => ({
            key,
            list: current?.list ?? null,
            error: e instanceof ApiError ? e.message : 'Could not read the invites.',
            missing: e instanceof ApiError ? missingPermissionOf(e.detail) : null,
          }))
      })

    return () => {
      cancelled = true
    }
  }, [page, asked])

  const removed = (row: GroupInviteRow) =>
    setRead((current) =>
      current?.list ? { ...current, list: { ...current.list, invites: current.list.invites.filter((i) => i.userId !== row.userId) } } : current,
    )

  return (
    <div className="flex flex-col gap-3">
      <GroupHeaderFor me={me} pathOf={pathOf} active="group-invites" />

      <PanelGrid className="grid-cols-1">
        <Card>
          <CardHeader>
            <CardTitle>Invites</CardTitle>
            <CardAction>
              <Button size="xs" variant="outline" onClick={() => setAsked((n) => n + 1)} disabled={loading} aria-label="Refresh invites">
                <RefreshCw className={loading ? 'animate-spin' : undefined} /> Refresh
              </Button>
            </CardAction>
          </CardHeader>

          {missing ? (
            <EmptyRow tone="danger">
              <VRChatPermissionMissing missing={missing} />
            </EmptyRow>
          ) : error ? (
            <EmptyRow tone="danger">{error}</EmptyRow>
          ) : !list ? (
            <EmptyRow>Loading…</EmptyRow>
          ) : list.invites.length === 0 ? (
            <EmptyRow>No invites</EmptyRow>
          ) : (
            <ul className="divide-y-(--hairline) divide-border">
              {list.invites.map((row) => (
                <li key={row.userId} className="flex items-center gap-2 px-(--panel-pad) py-2">
                  <div className="flex min-w-0 flex-1 flex-wrap items-center gap-x-3 gap-y-0.5">
                    <PersonLink platform="vrchat" id={row.userId} name={row.displayName} />
                    {row.invitedAt && (
                      <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                        {formatDay(row.invitedAt)}
                      </span>
                    )}
                  </div>
                  <Button size="xs" variant="outline" onClick={() => setCancelling(row)}>
                    <X /> Cancel invite
                  </Button>
                </li>
              ))}
            </ul>
          )}

          {list && (list.page > 1 || list.hasMore) && (
            <CardFooter className="flex-wrap gap-1" style={{ fontSize: 'var(--text-small)' }}>
              <Button size="xs" variant="outline" disabled={list.page <= 1 || loading} onClick={() => setPage((p) => p - 1)}>
                Previous
              </Button>
              <Button size="xs" variant="outline" disabled={!list.hasMore || loading} onClick={() => setPage((p) => p + 1)}>
                Next
              </Button>
            </CardFooter>
          )}
        </Card>
      </PanelGrid>

      <ConfirmDialog
        open={cancelling !== null}
        onOpenChange={(open) => !open && setCancelling(null)}
        title={cancelling?.displayName ? `Cancel the invite to ${cancelling.displayName}?` : 'Cancel this invite?'}
        action="Cancel invite"
        failed="Could not cancel the invite."
        onConfirm={() => api.cancelGroupInvite(cancelling!.userId, cancelling!.displayName)}
        onDone={() => cancelling && removed(cancelling)}
      />
    </div>
  )
}
