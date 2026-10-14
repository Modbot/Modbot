import { useEffect, useState } from 'react'
import { DailyBars, nextSlot, type DaySeries } from '@/components/charts'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Notice } from '@/components/ui/notice'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Textarea } from '@/components/ui/textarea'
import { Outcome } from '@/components/settings/fields'
import { Empty } from '@/components/ListParts'
import {
  api,
  ApiError,
  type CurrentUser,
  type DiscordInvite,
  type DiscordInvitesReport,
} from '@/lib/api'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { isFinal } from '@/lib/tryAgain'
import { ServerHeader } from '@/pages/analytics/ServerHeader'
import { Panel, Toggle } from '@/pages/analytics/shared'
import { useServerProfile } from '@/pages/analytics/useServerProfile'

type Span = '7' | '30' | '90'

/**
 * The Discord page's Invites tab: where new members joined from. Modbot compares each invite's use
 * count when somebody joins; staff name the invites (several invites with one name are one source)
 * and can have Modbot make an invite for each name on a list.
 *
 * Seeing the page needs See analytics. Naming and making invites need Change settings, so the
 * name fields and the form are only drawn for somebody who has it; the server checks both anyway.
 */
export function DiscordInvites({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const server = useServerProfile(me)

  return (
    <div className="flex flex-col gap-3">
      {server && <ServerHeader server={server} me={me} pathOf={pathOf} active="discord-invites" />}
      <InviteReport canChange={can(me, 'ManageSettings')} />
    </div>
  )
}

function InviteReport({ canChange }: { canChange: boolean }) {
  const [span, setSpan] = useState<Span>('30')
  const [report, setReport] = useState<DiscordInvitesReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [errorFinal, setErrorFinal] = useState(false)
  const [tries, setTries] = useState(0)

  useEffect(() => {
    let cancelled = false

    api
      .discordInvites(Number(span))
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
            : 'Could not load the Discord invites.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [span, tries])

  const reload = () => setTries((n) => n + 1)

  if (error || !report) {
    return (
      <Empty
        tone={error ? 'danger' : 'loading'}
        onTryAgain={
          errorFinal
            ? null
            : () => {
                setError(null)
                reload()
              }
        }
      >
        {error}
      </Empty>
    )
  }

  const series: DaySeries[] = report.series.map((s, i) => ({
    key: `source${i}`,
    label: s.name,
    points: s.days,
    slot: nextSlot(i),
  }))

  return (
    <>
      {report.missingPermission && (
        <Notice tone="warn">The bot needs Manage Server to read the server&rsquo;s invites.</Notice>
      )}
      {!report.missingPermission && report.readError && <Notice tone="warn">{report.readError}</Notice>}

      <Panel
        title="Joins per source"
        right={
          <Toggle
            value={span}
            onChange={setSpan}
            options={[
              { value: '7', label: '7 days' },
              { value: '30', label: '30 days' },
              { value: '90', label: '90 days' },
            ]}
          />
        }
      >
        {series.length === 0 ? (
          <EmptyRow>No joins in this range.</EmptyRow>
        ) : (
          <DailyBars
            from={report.from}
            to={report.to}
            stacked
            legend={series.map((s) => ({ label: s.label, slot: s.slot }))}
            series={series}
          />
        )}
      </Panel>

      <Panel title="Sources" flush>
        {report.sources.length === 0 ? (
          <EmptyRow>No invites yet.</EmptyRow>
        ) : (
          <Table
            pinFirst
            head={
              <>
                <Th>Source</Th>
                <Th className="text-right">Invites</Th>
                <Th className="text-right">Joins in range</Th>
                <Th className="text-right">Joins ever</Th>
              </>
            }
          >
            {report.sources.map((s) => (
              <Tr key={s.name}>
                <Td className={s.unknown ? 'text-muted-foreground' : undefined}>{s.name}</Td>
                <Td className="text-right font-mono">{s.unknown ? '—' : s.invites.toLocaleString()}</Td>
                <Td className="text-right font-mono">{s.joinsInRange.toLocaleString()}</Td>
                <Td className="text-right font-mono">{s.joinsEver.toLocaleString()}</Td>
              </Tr>
            ))}
          </Table>
        )}
      </Panel>

      <Panel title="Invites" flush>
        {report.invites.length === 0 ? (
          <EmptyRow>No invites</EmptyRow>
        ) : (
          <Table
            pinFirst
            head={
              <>
                <Th>Invite</Th>
                <Th>Name</Th>
                <Th className="text-right">Joins</Th>
                <Th>Status</Th>
              </>
            }
          >
            {report.invites.map((invite) => (
              <InviteRow
                // Drawn again when the saved name changes under it, so the field shows the new one.
                key={`${invite.id}:${invite.name ?? ''}`}
                invite={invite}
                canChange={canChange}
                longest={report.mostNameLength}
                onNamed={reload}
              />
            ))}
          </Table>
        )}
      </Panel>

      {canChange && <MakeInvites most={report.mostCreatedAtOnce} longest={report.mostNameLength} onMade={reload} />}
    </>
  )
}

function InviteRow({
  invite,
  canChange,
  longest,
  onNamed,
}: {
  invite: DiscordInvite
  canChange: boolean
  longest: number
  onNamed: () => void
}) {
  const saved = invite.name ?? ''
  const [name, setName] = useState(saved)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const save = () => {
    setBusy(true)
    setProblem(null)

    api
      .nameDiscordInvite(invite.id, name.trim() === '' ? null : name.trim())
      .then(onNamed)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <Tr>
      <Td className="font-mono">{invite.code}</Td>
      <Td>
        {canChange ? (
          <form
            className="flex items-center gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              if (name !== saved) save()
            }}
          >
            <Input
              value={name}
              maxLength={longest}
              onChange={(e) => setName(e.target.value)}
              aria-label={`Name for ${invite.code}`}
              className="max-w-64"
            />
            {name.trim() !== saved && (
              <Button type="submit" size="xs" disabled={busy}>
                {busy ? 'Saving…' : 'Save'}
              </Button>
            )}
            <Outcome tone="problem">{problem}</Outcome>
          </form>
        ) : invite.name ? (
          invite.name
        ) : (
          <span className="text-muted-foreground">—</span>
        )}
      </Td>
      <Td className="text-right font-mono">{invite.joins.toLocaleString()}</Td>
      <Td>
        <div className="flex flex-wrap items-center gap-1.5">
          {invite.isVanity && <Badge variant="outline">Vanity link</Badge>}
          {invite.madeByModbot && <Badge variant="outline">Made by Modbot</Badge>}
          {invite.gone && <Badge variant="outline">Gone</Badge>}
        </div>
      </Td>
    </Tr>
  )
}

/** One name per line; an invite is made for each, to the picked channel. */
function MakeInvites({ most, longest, onMade }: { most: number; longest: number; onMade: () => void }) {
  const [channelId, setChannelId] = useState('')
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [made, setMade] = useState<string | null>(null)

  const names = text
    .split('\n')
    .map((n) => n.trim())
    .filter((n) => n.length > 0)

  const make = () => {
    setBusy(true)
    setProblem(null)
    setMade(null)

    api
      .createDiscordInvites(channelId, names)
      .then((result) => {
        const refused = result.made.filter((m) => m.code === null)
        const count = result.made.length - refused.length

        if (count > 0) onMade()

        // Keep the names that were refused, so they can be tried again.
        setText(refused.map((m) => m.name).join('\n'))
        setMade(count > 0 ? `Made ${count.toLocaleString()} ${count === 1 ? 'invite' : 'invites'}.` : null)

        if (result.missingPermission) {
          setProblem('The bot needs Create Invite in that channel.')
        } else if (result.error) {
          setProblem(result.error)
        } else if (refused.length > 0) {
          setProblem(refused.map((m) => `${m.name}: ${m.error ?? 'refused'}`).join(' · '))
        }
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not make the invites.'))
      .finally(() => setBusy(false))
  }

  return (
    <Panel title="Make invites">
      <div className="flex max-w-lg flex-col gap-3">
        <ChannelPicker label="Channel" value={channelId} onChange={setChannelId} allowNone={false} needs={['viewChannel']} />

        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Names, one per line</span>
          <Textarea
            value={text}
            rows={5}
            onChange={(e) => setText(e.target.value)}
            aria-label="Invite names"
          />
        </label>

        <div className="flex flex-wrap items-center gap-3">
          <Button
            type="button"
            size="xs"
            onClick={make}
            disabled={busy || channelId === '' || names.length === 0 || names.length > most || names.some((n) => n.length > longest)}
          >
            {busy ? 'Making…' : 'Make invites'}
          </Button>
          <Outcome tone="ok">{made}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </div>
      </div>
    </Panel>
  )
}
