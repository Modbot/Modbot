import { useEffect, useState } from 'react'
import { Card, CardHeader } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import { EmptyRow } from '@/components/PanelGrid'
import { NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { RoleChip } from '@/components/discord/DiscordMemberParts'
import { Unread } from '@/components/Freshness'
import { Empty } from '@/components/ListParts'
import { api, ApiError, type CurrentUser, type RoleReport, type RoleReportRow } from '@/lib/api'
import { ROLE_FLAG_LABELS } from '@/lib/discordTidyUp'
import type { PageId } from '@/lib/nav'
import { isFinal } from '@/lib/tryAgain'
import { cn } from '@/lib/utils'
import { ServerHeader } from '@/pages/analytics/ServerHeader'
import { useServerProfile } from '@/pages/analytics/useServerProfile'

/**
 * The Discord page's Roles tab: every role in the server with how many hold it, and what is worth a
 * look about it (Discord tidy-up design). Read only: nothing here changes a role; tidying up is done
 * in Discord.
 *
 * The server marks the roles and puts them in order, marked ones first; the page only draws them.
 * A role marked as having the same permissions and colour as others names those others on hover,
 * since the pair is the point of that mark.
 */
export function DiscordRoles({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const server = useServerProfile(me)

  return (
    <div className="flex flex-col gap-3">
      {server && <ServerHeader server={server} me={me} pathOf={pathOf} active="discord-roles" />}
      <RoleList />
    </div>
  )
}

function RoleList() {
  const [report, setReport] = useState<RoleReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [errorFinal, setErrorFinal] = useState(false)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false

    api
      .discordRoleReport()
      .then((next) => {
        if (cancelled) return
        setReport(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setErrorFinal(isFinal(e))
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to view analytics.'
            : 'Could not load the Discord roles.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [tries])

  if (error || !report) {
    return (
      <Empty
        tone={error ? 'danger' : 'loading'}
        onTryAgain={
          errorFinal
            ? null
            : () => {
                setError(null)
                setTries((n) => n + 1)
              }
        }
      >
        {error}
      </Empty>
    )
  }

  const marked = report.roles.filter((r) => r.flags.length > 0).length
  const unread = report.guildId === null || report.membersListedAt === null

  return (
    <Card>
      <CardHeader className={cn(unread && 'bg-warn/10')}>
        {report.guildId === null ? (
          <Unread>No Discord server set.</Unread>
        ) : report.membersListedAt === null ? (
          <Unread>The Discord member list has not been read yet.</Unread>
        ) : null}
        <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <span className="font-mono">{report.roles.length.toLocaleString()}</span>{' '}
          {report.roles.length === 1 ? 'role' : 'roles'} · <span className="font-mono">{marked.toLocaleString()}</span>{' '}
          marked
        </span>
      </CardHeader>

      {report.roles.length === 0 ? (
        <EmptyRow>No roles</EmptyRow>
      ) : (
        <Table
          pinFirst
          narrow={
            <NarrowRows>
              {report.roles.map((role) => (
                <NarrowRow
                  key={role.id}
                  main={<RoleChip id={role.id} name={role.name} color={role.color} className="max-w-full" />}
                  side={<Count members={role.members} />}
                  facts={role.flags.map((flag) => (
                    <span key={flag} title={flagTitle(role, flag)}>
                      {ROLE_FLAG_LABELS[flag]}
                    </span>
                  ))}
                />
              ))}
            </NarrowRows>
          }
          head={
            <>
              <Th>Role</Th>
              <Th className="text-right">Members</Th>
              <Th>Marked as</Th>
            </>
          }
        >
          {report.roles.map((role) => (
            <Tr key={role.id}>
              <Td>
                <RoleChip id={role.id} name={role.name} color={role.color} />
              </Td>
              <Td className="text-right">
                <Count members={role.members} />
              </Td>
              <Td>
                {role.flags.length === 0 ? (
                  <span className="text-muted-foreground">—</span>
                ) : (
                  <div className="flex flex-wrap items-center gap-1.5">
                    {role.flags.map((flag) => (
                      <Badge key={flag} variant={flag === 'bot-role' ? 'outline' : 'warn'} title={flagTitle(role, flag)}>
                        {ROLE_FLAG_LABELS[flag]}
                      </Badge>
                    ))}
                  </div>
                )}
              </Td>
            </Tr>
          ))}
        </Table>
      )}
    </Card>
  )
}

function Count({ members }: { members: number | null }) {
  return members === null ? (
    <span className="text-muted-foreground">—</span>
  ) : (
    <span className="font-mono">{members.toLocaleString()}</span>
  )
}

/** The other roles behind "Same permissions and colour", on hover; nothing for any other mark. */
function flagTitle(role: RoleReportRow, flag: string): string | undefined {
  return flag === 'same-permissions-and-colour' && role.samePermissionsAndColourAs.length > 0
    ? role.samePermissionsAndColourAs.join(', ')
    : undefined
}
