import { useCallback, useEffect, useState } from 'react'
import { Card, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { EmptyRow } from '@/components/PanelGrid'
import { ConfirmButton } from '@/components/settings/fields'
import { dateTime } from '@/components/charts'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { DiscordPersonLink } from '@/components/facts'
import { api, ApiError, type CurrentUser, type DiscordGate, type DiscordGateRow } from '@/lib/api'
import { can } from '@/lib/permissions'
import { timeAgo } from '@/lib/serverOverview'

/**
 * Discord → Members, above the list: who is waiting at the join gate (join gate design §7), and
 * the hold on new joiners (§8).
 *
 * Not a filter on the member list: Watch only rows are about what would have happened, and a row
 * here outlives nothing the member list knows about. Drawn only while the gate is not off.
 * Let in, Remove, Hold, Lift hold and Pause invites need Manage the join gate; Watch only does
 * nothing in Discord, so it offers none of them.
 */
export function AtTheGateCard({ me }: { me: CurrentUser }) {
  const [gate, setGate] = useState<DiscordGate | null>(null)
  const [now, setNow] = useState(() => new Date().toISOString())
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .discordGate()
        .then((next) => {
          setGate(next)
          setNow(new Date().toISOString())
        })
        .catch(() => setGate(null)),
    [],
  )

  useEffect(() => {
    void load()
    const timer = setInterval(() => void load(), 60_000)
    return () => clearInterval(timer)
  }, [load])

  if (!gate || gate.mode === 'off') return null

  const acts = gate.mode === 'on' && can(me, 'ManageJoinGate')

  const run = (action: () => Promise<unknown>) => {
    setBusy(true)
    setProblem(null)
    action()
      .then(() => load())
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'That did not work.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardHeader className="flex flex-wrap items-center gap-2">
        <CardTitle>At the gate</CardTitle>
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {gate.mode === 'watch' ? 'Watch only' : gate.heldSince ? `New joiners held since ${dateTime(gate.heldSince)}` : null}
        </span>
        <span className="ml-auto flex flex-wrap items-center gap-2">
          {problem && (
            <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
              {problem}
            </span>
          )}
          {acts &&
            (gate.heldSince ? (
              <Button type="button" size="xs" disabled={busy} onClick={() => run(api.liftDiscordGateHold)}>
                Lift hold
              </Button>
            ) : (
              <Button type="button" size="xs" variant="outline" disabled={busy} onClick={() => run(api.holdDiscordGate)}>
                Hold new joiners
              </Button>
            ))}
          {acts && gate.pauseInvites && (
            <ConfirmButton variant="outline" disabled={busy} onConfirm={() => run(api.pauseDiscordInvites)}>
              Pause invites
            </ConfirmButton>
          )}
        </span>
      </CardHeader>

      {gate.waiting.length === 0 ? (
        <EmptyRow>Nobody waiting</EmptyRow>
      ) : (
        <Table
          pinFirst
          narrow={
            <NarrowRows>
              {gate.waiting.map((r) => (
                <NarrowRow
                  key={r.discordUserId}
                  picture={<Avatar url={r.avatarUrl} className="size-8" />}
                  main={<span className="truncate font-medium">{r.displayName ?? r.username}</span>}
                  facts={[
                    <span key="joined">joined {timeAgo(r.joinedAt, now) ?? dateTime(r.joinedAt)}</span>,
                    steps(gate, r),
                    removal(gate, r),
                    r.problem && (
                      <span key="problem" className="text-destructive">
                        {r.problem}
                      </span>
                    ),
                  ]}
                >
                  {acts && <Actions row={r} busy={busy} run={run} />}
                </NarrowRow>
              ))}
            </NarrowRows>
          }
          head={
            <>
              <Th>Person</Th>
              <Th>Joined</Th>
              <Th>Steps</Th>
              <Th>{gate.mode === 'watch' ? 'Would remove' : 'Earliest removal'}</Th>
              {acts && (
                <Th>
                  <span className="sr-only">Actions</span>
                </Th>
              )}
            </>
          }
        >
          {gate.waiting.map((r) => (
            <Tr key={r.discordUserId}>
              <Td>
                <div className="flex items-center gap-2">
                  <Avatar url={r.avatarUrl} />
                  <div className="min-w-0">
                    <DiscordPersonLink id={r.discordUserId} name={r.displayName ?? r.username} />
                    {r.problem && (
                      <div className="truncate text-destructive" style={{ fontSize: 'var(--text-small)' }}>
                        {r.problem}
                      </div>
                    )}
                  </div>
                </div>
              </Td>
              <Td className="font-mono">
                <span title={dateTime(r.joinedAt)}>{timeAgo(r.joinedAt, now) ?? dateTime(r.joinedAt)}</span>
              </Td>
              <Td>{steps(gate, r)}</Td>
              <Td className="font-mono">{removal(gate, r)}</Td>
              {acts && (
                <Td>
                  <Actions row={r} busy={busy} run={run} />
                </Td>
              )}
            </Tr>
          ))}
        </Table>
      )}
    </Card>
  )
}

function Actions({
  row,
  busy,
  run,
}: {
  row: DiscordGateRow
  busy: boolean
  run: (action: () => Promise<unknown>) => void
}) {
  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <Button type="button" size="xs" variant="outline" disabled={busy} onClick={() => run(() => api.letInAtDiscordGate(row.discordUserId))}>
        Let in
      </Button>
      <ConfirmButton disabled={busy} onConfirm={() => run(() => api.removeAtDiscordGate(row.discordUserId))}>
        Remove
      </ConfirmButton>
    </span>
  )
}

/** The steps the server asks for, each done or not: "Agreed · Not linked". */
function steps(gate: DiscordGate, r: DiscordGateRow): string {
  const parts = [r.agreed ? 'Agreed' : 'Not agreed']

  if (gate.needsLink && r.linked !== null) parts.push(r.linked ? 'Linked' : 'Not linked')
  if (gate.needsEighteenPlus && r.eighteenPlus !== null) parts.push(r.eighteenPlus ? '18+' : 'Not 18+')

  return parts.join(' · ')
}

/** When they will be (or, in Watch only, would have been) removed; null when never. */
function removal(gate: DiscordGate, r: DiscordGateRow): string | null {
  if (gate.mode === 'watch') return r.wouldRemoveAt ? dateTime(r.wouldRemoveAt) : null
  return r.removedAt ? dateTime(r.removedAt) : null
}
