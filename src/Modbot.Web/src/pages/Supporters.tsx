import { useCallback, useEffect, useState } from 'react'
import { Picker } from '@/components/discord/Picker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { ConfirmButton, Outcome } from '@/components/settings/fields'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { api, ApiError, type DiscordMember } from '@/lib/api'
import { dateTime } from '@/lib/format'
import { supporterApi, supporterMatches, type Supporter, type SupporterList, type SupporterTier } from '@/lib/supporters'

/**
 * Who pays through Ko-fi, which Discord role and VRChat group role each tier gives, and who
 * is still waiting to be matched to a Discord account. Needs Manage settings.
 */
export function Supporters() {
  const [list, setList] = useState<SupporterList | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      supporterApi
        .list()
        .then((next) => {
          setList(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError && e.status === 403 ? 'You do not have permission to see this.' : 'Could not load the supporters.'),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error)
    return (
      <Card>
        <EmptyRow tone="danger" onTryAgain={load}>
          {error}
        </EmptyRow>
      </Card>
    )

  if (!list)
    return (
      <Card>
        <EmptyRow tone="loading" />
      </Card>
    )

  return (
    <div className="flex flex-col gap-3">
      {list.kofiOn && !list.botCanManageRoles && (
        <Card>
          <CardContent>
            <Outcome tone="problem">Bot needs Manage Roles</Outcome>
          </CardContent>
        </Card>
      )}
      <TiersCard list={list} onChanged={setList} />
      <SupportersCard list={list} onChanged={setList} />
    </div>
  )
}

/** The VRChat group's roles as a picker's groups. */
function groupRoleGroups(list: SupporterList) {
  return [{ heading: null, options: list.groupRoles.map((r) => ({ id: r.id, label: r.name, marks: [] })) }]
}

function GroupRolePicker({
  list,
  value,
  onChange,
}: {
  list: SupporterList
  value: string
  onChange: (id: string) => void
}) {
  const current = value ? { label: list.groupRoles.find((r) => r.id === value)?.name ?? value, marks: [] } : null

  return <Picker label="VRChat group role" value={value} onChange={onChange} groups={groupRoleGroups(list)} current={current} allowNone error={null} />
}

function TiersCard({ list, onChanged }: { list: SupporterList; onChanged: (next: SupporterList) => void }) {
  const [name, setName] = useState('')
  const [discordRoleId, setDiscordRoleId] = useState('')
  const [vrChatRoleId, setVrChatRoleId] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const add = () => {
    setBusy(true)
    setProblem(null)

    supporterApi
      .addTier({ name: name.trim(), discordRoleId: discordRoleId || null, vrChatRoleId: vrChatRoleId || null })
      .then((next) => {
        onChanged(next)
        setName('')
        setDiscordRoleId('')
        setVrChatRoleId('')
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not add the tier.'))
      .finally(() => setBusy(false))
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Tiers</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {list.tiers.length === 0 && <EmptyRow>No tiers yet.</EmptyRow>}

        {list.tiers.map((tier) => (
          <TierRow key={tier.id} tier={tier} list={list} onChanged={onChanged} />
        ))}

        <div className="flex max-w-lg flex-col gap-2 border-t border-t-(length:--hairline) pt-3">
          <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            <span className="text-muted-foreground">Tier name</span>
            <Input value={name} maxLength={list.mostTierNameLength} onChange={(e) => setName(e.target.value)} />
          </label>
          <RolePicker label="Discord role" value={discordRoleId} onChange={setDiscordRoleId} needsAssign />
          <GroupRolePicker list={list} value={vrChatRoleId} onChange={setVrChatRoleId} />
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" size="sm" disabled={busy || name.trim().length === 0} onClick={add}>
              {busy ? 'Adding…' : 'Add tier'}
            </Button>
            <Outcome tone="problem">{problem}</Outcome>
          </div>
        </div>
      </CardContent>
    </Card>
  )
}

function TierRow({
  tier,
  list,
  onChanged,
}: {
  tier: SupporterTier
  list: SupporterList
  onChanged: (next: SupporterList) => void
}) {
  const [discordRoleId, setDiscordRoleId] = useState(tier.discordRoleId ?? '')
  const [vrChatRoleId, setVrChatRoleId] = useState(tier.vrChatRoleId ?? '')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const changed = discordRoleId !== (tier.discordRoleId ?? '') || vrChatRoleId !== (tier.vrChatRoleId ?? '')

  const run = (call: () => Promise<SupporterList>) => {
    setBusy(true)
    setProblem(null)

    call()
      .then(onChanged)
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <div className="flex max-w-lg flex-col gap-2 border-b border-b-(length:--hairline) pb-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{tier.name}</span>
        <Badge variant="outline">{tier.supporters}</Badge>
      </div>
      <RolePicker label="Discord role" value={discordRoleId} onChange={setDiscordRoleId} needsAssign />
      <GroupRolePicker list={list} value={vrChatRoleId} onChange={setVrChatRoleId} />
      <div className="flex flex-wrap items-center gap-2">
        <Button
          type="button"
          size="xs"
          disabled={busy || !changed}
          onClick={() => run(() => supporterApi.setTierRoles(tier.id, { discordRoleId: discordRoleId || null, vrChatRoleId: vrChatRoleId || null }))}
        >
          {busy ? 'Saving…' : 'Save'}
        </Button>
        <ConfirmButton variant="outline" disabled={busy} onConfirm={() => run(() => supporterApi.deleteTier(tier.id))}>
          Delete
        </ConfirmButton>
        <Outcome tone="problem">{problem}</Outcome>
      </div>
    </div>
  )
}

function SupportersCard({ list, onChanged }: { list: SupporterList; onChanged: (next: SupporterList) => void }) {
  const [typed, setTyped] = useState('')
  const shown = list.supporters.filter((s) => supporterMatches(s, typed))

  return (
    <Card>
      <CardHeader>
        <CardTitle>Supporters</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-2">
        <label className="flex max-w-lg flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Search by name or code</span>
          <Input value={typed} onChange={(e) => setTyped(e.target.value)} />
        </label>

        {list.supporters.length === 0 && <EmptyRow>No supporters yet.</EmptyRow>}
        {list.supporters.length > 0 && shown.length === 0 && <EmptyRow>No supporter matches.</EmptyRow>}

        {shown.map((supporter) => (
          <SupporterRow key={supporter.id} supporter={supporter} onChanged={onChanged} />
        ))}
      </CardContent>
    </Card>
  )
}

function SupporterRow({ supporter, onChanged }: { supporter: Supporter; onChanged: (next: SupporterList) => void }) {
  const [matching, setMatching] = useState(false)
  const active = supporter.state === 'active'

  return (
    <div className="flex flex-col gap-2 border-b border-b-(length:--hairline) pb-2">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{supporter.discordName ?? supporter.name}</span>
        {supporter.tierName && <Badge variant="secondary">{supporter.tierName}</Badge>}
        <Badge variant={active ? 'ok' : 'outline'}>{active ? 'Active' : 'Ended'}</Badge>
        {!supporter.discordUserId && <Badge variant="warn">Not matched</Badge>}
        {supporter.discordUserId && !supporter.vrChatLinked && <Badge variant="outline">No linked VRChat</Badge>}
        {supporter.code && <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>{supporter.code}</span>}
      </div>

      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        <span>Paid {dateTime(supporter.lastPaidAt)}</span>
        {active && <span>Roles end {dateTime(supporter.rolesEndAt)}</span>}
        {supporter.discordRoleGiven && <span>Discord role given</span>}
        {supporter.vrChatRoleGiven && <span>VRChat role given</span>}
      </div>

      <Outcome tone="problem">{supporter.problem}</Outcome>

      {!supporter.discordUserId && !matching && (
        <div className="flex">
          <Button type="button" size="xs" variant="outline" onClick={() => setMatching(true)}>
            Match
          </Button>
        </div>
      )}

      {matching && <MatchBox supporter={supporter} onDone={() => setMatching(false)} onChanged={onChanged} />}
    </div>
  )
}

/** Finds a member of the Discord server by name and matches the supporter to them. */
function MatchBox({
  supporter,
  onDone,
  onChanged,
}: {
  supporter: Supporter
  onDone: () => void
  onChanged: (next: SupporterList) => void
}) {
  const [search, setSearch] = useState(supporter.name)
  const [members, setMembers] = useState<DiscordMember[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const find = () => {
    setBusy(true)
    setProblem(null)

    api
      .discordMembers({ search: search.trim(), pageSize: 8 })
      .then((found) => setMembers(found.members))
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not search.'))
      .finally(() => setBusy(false))
  }

  const match = (member: DiscordMember) => {
    setBusy(true)
    setProblem(null)

    supporterApi
      .link(supporter.id, member.userId)
      .then((next) => {
        onChanged(next)
        onDone()
      })
      .catch((e: unknown) => {
        setProblem(e instanceof ApiError ? e.message : 'Could not match.')
        setBusy(false)
      })
  }

  return (
    <div className="flex max-w-lg flex-col gap-2">
      <div className="flex flex-wrap gap-2">
        <Input
          className="min-w-0 flex-1"
          aria-label="Discord member"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && search.trim()) find()
          }}
        />
        <Button type="button" size="xs" disabled={busy || !search.trim()} onClick={find}>
          Search
        </Button>
        <Button type="button" size="xs" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
      </div>

      {members?.length === 0 && <EmptyRow>No members found.</EmptyRow>}

      {members?.map((member) => (
        <div key={member.userId} className="flex items-center justify-between gap-2">
          <span className="min-w-0 truncate">
            {member.displayName} <span className="text-muted-foreground">{member.username}</span>
          </span>
          <Button type="button" size="xs" disabled={busy} onClick={() => match(member)}>
            Match
          </Button>
        </div>
      ))}

      <Outcome tone="problem">{problem}</Outcome>
    </div>
  )
}
