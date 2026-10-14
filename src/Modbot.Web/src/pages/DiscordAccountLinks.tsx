import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Outcome } from '@/components/settings/fields'
import { Empty } from '@/components/ListParts'
import { api, ApiError, type CurrentUser, type DiscordLinkShare } from '@/lib/api'
import { dateTime } from '@/lib/format'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { isFinal } from '@/lib/tryAgain'
import { ServerHeader } from '@/pages/analytics/ServerHeader'
import { Panel, Stat, StatStrip } from '@/pages/analytics/shared'
import { useServerProfile } from '@/pages/analytics/useServerProfile'

/** A share as a percentage with one decimal, or a dash when there is nobody to take it of. */
function share(part: number, whole: number): string {
  return whole > 0 ? `${((part / whole) * 100).toFixed(1)}%` : '—'
}

/**
 * The Discord page's Account links tab: what share of the VRChat group has a Discord account linked,
 * what share of the Discord server has a VRChat account linked, and the one-time reminder to link.
 *
 * Seeing it needs See analytics. Starting and stopping the reminder need Change settings, so the
 * buttons are only drawn for somebody who has it; the server checks anyway.
 */
export function DiscordAccountLinks({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const server = useServerProfile(me)

  return (
    <div className="flex flex-col gap-3">
      {server && <ServerHeader server={server} me={me} pathOf={pathOf} active="discord-account-links" />}
      <LinkShare canChange={can(me, 'ManageSettings')} />
    </div>
  )
}

function LinkShare({ canChange }: { canChange: boolean }) {
  const [data, setData] = useState<DiscordLinkShare | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [errorFinal, setErrorFinal] = useState(false)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false

    api
      .discordLinkShare()
      .then((next) => {
        if (cancelled) return
        setData(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setErrorFinal(isFinal(e))
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to view analytics.'
            : 'Could not load the account links.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [tries])

  // Reloaded now and then while the reminder runs, so the counts move.
  const running = data?.reminder.running ?? false
  useEffect(() => {
    if (!running) return

    const timer = window.setInterval(() => setTries((n) => n + 1), 30_000)
    return () => window.clearInterval(timer)
  }, [running])

  if (error || !data) {
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

  return (
    <>
      <StatStrip className="sm:grid-cols-2 xl:grid-cols-2">
        <Stat
          label="Group members with a linked Discord account"
          value={share(data.groupMembersLinked, data.groupMembers)}
          note={`${data.groupMembersLinked.toLocaleString()} of ${data.groupMembers.toLocaleString()}`}
          noteMono
        />
        <Stat
          label="Discord members with a linked VRChat account"
          value={share(data.discordMembersLinked, data.discordMembers)}
          note={`${data.discordMembersLinked.toLocaleString()} of ${data.discordMembers.toLocaleString()}`}
          noteMono
        />
      </StatStrip>

      <Reminder data={data} canChange={canChange} onChanged={setData} />
    </>
  )
}

function Reminder({
  data,
  canChange,
  onChanged,
}: {
  data: DiscordLinkShare
  canChange: boolean
  onChanged: (next: DiscordLinkShare) => void
}) {
  const reminder = data.reminder
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const change = (action: () => Promise<DiscordLinkShare>) => {
    setBusy(true)
    setProblem(null)

    action()
      .then(onChanged)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not change the reminder.'))
      .finally(() => setBusy(false))
  }

  return (
    <>
      {!data.linkingSetUp && <Notice tone="warn">Account linking is not set up.</Notice>}

      <Panel
        title="Link reminder"
        right={
          canChange && (
            <div className="flex items-center gap-2">
              <Outcome tone="problem">{problem}</Outcome>
              {reminder.running ? (
                <Button type="button" size="xs" disabled={busy} onClick={() => change(api.stopDiscordLinkReminder)}>
                  {busy ? 'Stopping…' : 'Stop reminder'}
                </Button>
              ) : (
                <Button
                  type="button"
                  size="xs"
                  disabled={busy || !data.linkingSetUp || reminder.waiting === 0}
                  onClick={() => change(api.startDiscordLinkReminder)}
                >
                  {busy ? 'Starting…' : 'Start reminder'}
                </Button>
              )}
            </div>
          )
        }
        flush
      >
        <Table
          pinFirst
          head={
            <>
              <Th>Reminder</Th>
              <Th className="text-right">{reminder.running ? 'Running' : 'Not running'}</Th>
            </>
          }
        >
          <Tr>
            <Td>{reminder.running ? 'Left to send' : 'Would be sent'}</Td>
            <Td className="text-right font-mono">{reminder.waiting.toLocaleString()}</Td>
          </Tr>
          <Tr>
            <Td>Sent, ever</Td>
            <Td className="text-right font-mono">{reminder.sent.toLocaleString()}</Td>
          </Tr>
          <Tr>
            <Td>Not reached, ever</Td>
            <Td className="text-right font-mono">{reminder.notReached.toLocaleString()}</Td>
          </Tr>
          <Tr>
            <Td>Last sent</Td>
            <Td className="text-right">{reminder.lastSentAt ? dateTime(reminder.lastSentAt) : '—'}</Td>
          </Tr>
        </Table>
      </Panel>
    </>
  )
}
