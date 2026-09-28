import { useCallback, useState } from 'react'
import { ChevronRight } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Row } from '@/components/ui/fact-row'
import { dateTime } from '@/components/charts'
import { FactSentence } from '@/components/factSentence'
import { FactTime, PersonLink, InstanceLink, SourceBadge, WorldLink } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
import { EmptyRow } from '@/components/PanelGrid'
import { VersionCard } from '@/components/subject/ProfileVersions'
import { api, type AuditAround, type AuditEntry } from '@/lib/api'
import { needsYear } from '@/lib/format'
import { fieldName } from '@/lib/profileFields'
import { openPersonVersion } from '@/lib/subject'
import { useLoad } from '@/lib/useLoad'
import { cn } from '@/lib/utils'

/**
 * Everything one audit log entry holds, under its row.
 *
 * The sentence in the row says what happened; this says everything the fact recorded: every
 * column, the diff a change carried, the snapshot a profile fact stands for, and the payload and
 * the whole entry as JSON. Nothing here is reworded: the JSON is what the server answered.
 *
 * It leads with what happened around the entry (finding 20 of the 2026-09-25 UX review): the
 * question asked of a questionable ban is what else that moderator did then, and what else
 * happened to that person, and it used to take hand-built filters and a copied id to answer. The
 * raw type is for checking, not reading, so it sits with the JSON.
 *
 * `around` is off for a fact shown inside another entry's detail, whose own detail already has it.
 */
export function EntryDetail({ entry, around = true }: { entry: AuditEntry; around?: boolean }) {
  const changed = changedFields(entry)

  // Under a row of the audit log this sits in a cell as wide as the whole table, which a narrow
  // screen scrolls sideways. Below lg it is held to the width the screen shows (less the page's
  // inset and the panel's two edges), so each fact's value lands on screen beside its label.
  return (
    <div className="grid gap-3 border-t-(length:--hairline) bg-muted/20 p-(--panel-pad) grid-cols-1 max-lg:max-w-[calc(100vw-2rem-2*var(--hairline))] lg:grid-cols-2">
      {around && <Around entry={entry} />}

      <div className="flex flex-col gap-3">
        <div className="max-w-lg text-foreground">
          <Row label="Source" value={<SourceBadge source={entry.source} />} />
          {entry.occurredBefore ? (
            <Row
              label="When"
              value={
                <>
                  Between <span className="font-mono">{dateTime(entry.occurredAt, needsYear(entry.occurredAt, entry.occurredBefore))}</span> and{' '}
                  <span className="font-mono">{dateTime(entry.occurredBefore, needsYear(entry.occurredAt, entry.occurredBefore))}</span>
                </>
              }
            />
          ) : (
            <Row label="When" mono value={dateTime(entry.occurredAt)} />
          )}
          <Row label="Recorded" mono value={dateTime(entry.observedAt)} />
          <Row
            label="About"
            value={
              <>
                <span className="text-muted-foreground">{entry.subjectKind.toLowerCase()} · {entry.subjectPlatform} · </span>
                {entry.subjectKind === 'Person' ? (
                  <PersonLink platform={entry.subjectPlatform} id={entry.subjectId} name={entry.subjectName} />
                ) : (
                  <span className="font-mono break-all">{entry.subjectId}</span>
                )}
                {entry.subjectName && entry.subjectKind === 'Person' && (
                  <span className="ml-2 font-mono text-muted-foreground break-all">{entry.subjectId}</span>
                )}
              </>
            }
          />
          <Row
            label="Done by"
            value={
              entry.actorId ? (
                <>
                  <span className="text-muted-foreground">{entry.actorPlatform} · </span>
                  <PersonLink platform={entry.actorPlatform} id={entry.actorId} name={entry.actorName} />
                  <span className="ml-2 font-mono text-muted-foreground break-all">{entry.actorId}</span>
                </>
              ) : (
                <span className="text-muted-foreground">nobody named</span>
              )
            }
          />
          {entry.worldId && (
            <Row
              label="World"
              value={
                <>
                  <WorldLink id={entry.worldId} name={entry.worldName} unnamed="id" />
                  <span className="ml-2 font-mono text-muted-foreground break-all">{entry.worldId}</span>
                </>
              }
            />
          )}
          {entry.instanceId && (
            <Row
              label="Instance"
              value={
                <InstanceLink
                  modbotInstanceId={entry.modbotInstanceId}
                  worldId={entry.worldId}
                  worldName={entry.worldName}
                  number={entry.instanceId}
                  name={entry.instanceName}
                />
              }
            />
          )}
          {entry.reportedBy && entry.reportedBy.length > 0 && (
            <Row
              label="Reported by"
              value={
                <ul>
                  {entry.reportedBy.map((reporter) => (
                    <li key={reporter.accountId}>
                      {reporter.name ?? reporter.accountId}
                      <span className="ml-2 font-mono text-muted-foreground">{dateTime(reporter.at)}</span>
                    </li>
                  ))}
                </ul>
              }
            />
          )}
          {entry.description && <Row label="Description" value={entry.description} />}
          <Row label="Entry id" mono value={entry.id} />
        </div>

        {changed.length > 0 && (
          <Card>
            <CardHeader>
              <CardTitle>Changed</CardTitle>
            </CardHeader>
            <Table head={<><Th>Field</Th><Th>Before</Th><Th>After</Th></>}>
              {changed.map(([key, pair]) => (
                <Tr key={key}>
                  <Td>{fieldName(key)}</Td>
                  <Td className="min-w-[10rem] break-all whitespace-normal text-muted-foreground">{shown(pair.old)}</Td>
                  <Td className="min-w-[10rem] break-all whitespace-normal">{shown(pair.new)}</Td>
                </Tr>
              ))}
            </Table>
          </Card>
        )}

        <Snapshot entry={entry} />
        <SameDecision entry={entry} />
      </div>

      {/*
        One record, not two. The payload is a field of the entry, so showing both printed the
        payload twice and left a moderator comparing them to find out whether they differed.

        Shut to begin with: everything above this is the same record said in words, and that is
        what the page is for. The JSON is here to be checked when the words are not enough.
      */}
      <div className="flex flex-col gap-3">
        <div className="max-w-lg text-foreground">
          <Row
            label="Type"
            mono
            value={
              <>
                {entry.type}
                {entry.typeRaw && (
                  <span className="ml-2 text-muted-foreground" title="The source's own word">
                    {entry.typeRaw}
                  </span>
                )}
              </>
            }
          />
          <Row label="Log" value={entry.category} />
        </div>
        <JsonView title="Raw JSON" value={entry} closed />
      </div>
    </div>
  )
}

/**
 * What happened around this entry: the same moderator's actions, and the same person's facts.
 *
 * Each list comes from the server newest first with the entry in its place, marked here. Every
 * line opens into its own detail the way "Same decision" does, rather than moving the log, so the
 * reader keeps the row they started from.
 */
function Around({ entry }: { entry: AuditEntry }) {
  // An entry nobody did and that is about no person has nothing to put here.
  const wanted = Boolean(entry.actorId) || entry.subjectKind === 'Person'

  const load = useCallback(() => api.auditAround(entry.id), [entry.id])
  const { data, error } = useLoad<AuditAround>(wanted ? load : null)

  if (!wanted) return null

  return (
    <Card className="lg:col-span-2">
      <CardHeader>
        <CardTitle>Around this</CardTitle>
      </CardHeader>
      {error && <EmptyRow tone="danger">{error}</EmptyRow>}
      {!error && !data && <EmptyRow>Loading…</EmptyRow>}
      {data && (
        <div className="grid grid-cols-1 divide-y-(length:--hairline) divide-border lg:grid-cols-2 lg:divide-x-(length:--hairline) lg:divide-y-0">
          {data.byActor && (
            <AroundList
              title={`By ${entry.actorName ?? entry.actorId ?? 'the same account'}`}
              entries={data.byActor}
              current={entry.id}
            />
          )}
          {data.aboutSubject && (
            <AroundList
              title={`About ${entry.subjectName ?? entry.subjectId}`}
              entries={data.aboutSubject}
              current={entry.id}
            />
          )}
        </div>
      )}
    </Card>
  )
}

function AroundList({ title, entries, current }: { title: string; entries: AuditEntry[]; current: number }) {
  return (
    <div className="min-w-0">
      <div className="truncate px-(--panel-pad) py-2 font-medium" style={{ fontSize: 'var(--text-small)' }}>
        {title}
      </div>
      {entries.length <= 1 && <EmptyRow>Nothing else near it.</EmptyRow>}
      {entries.length > 1 && (
        <>
          <AroundWide entries={entries} current={current} />
          <AroundNarrow entries={entries} current={current} />
        </>
      )}
    </div>
  )
}

/**
 * The list where there is room: when, then the sentence beside it.
 *
 * Not a `Table`, so it marks itself as the wide form by hand (see `data-layout` in index.css). On a
 * phone the time's column left the sentence a strip under 100px wide, and a role badge wider than
 * that ran under "This one".
 */
function AroundWide({ entries, current }: { entries: AuditEntry[]; current: number }) {
  return (
    <div data-layout="wide">
      {entries.map((fact) =>
        fact.id === current ? (
          <div
            key={fact.id}
            aria-current="true"
            className="flex flex-wrap items-baseline gap-2 border-t border-t-(length:--hairline) bg-accent px-(--panel-pad) py-2 pl-[calc(var(--panel-pad)+1.25rem)]"
            style={{ fontSize: 'var(--text-small)' }}
          >
            <span className="font-mono text-muted-foreground">{dateTime(fact.occurredAt)}</span>
            <span className="min-w-0 flex-1">
              <FactSentence entry={fact} />
            </span>
            <span className="text-muted-foreground">This one</span>
          </div>
        ) : (
          <details key={fact.id} className="group border-t border-t-(length:--hairline)">
            <summary
              className={cn(
                'flex cursor-pointer list-none items-baseline gap-2 px-(--panel-pad) py-2 hover:bg-muted/40',
                'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring [&::-webkit-details-marker]:hidden',
              )}
              style={{ fontSize: 'var(--text-small)' }}
            >
              <ChevronRight
                className="size-3.5 shrink-0 self-center text-muted-foreground transition-transform group-open:rotate-90 motion-reduce:transition-none"
                aria-hidden
              />
              <span className="shrink-0 font-mono text-muted-foreground">{dateTime(fact.occurredAt)}</span>
              <span className="min-w-0 flex-1">
                <SourceBadge source={fact.source} /> <FactSentence entry={fact} />
              </span>
            </summary>
            <EntryDetail entry={fact} around={false} />
          </details>
        ),
      )}
    </div>
  )
}

/**
 * The list on a phone, as the audit log's own rows are there: the sentence across the whole row,
 * then where it came from and when. A row opens its detail below it, as the wide form's do.
 */
function AroundNarrow({ entries, current }: { entries: AuditEntry[]; current: number }) {
  const [open, setOpen] = useState<ReadonlySet<number>>(new Set())

  const toggle = (id: number) =>
    setOpen((was) => {
      const now = new Set(was)
      if (!now.delete(id)) now.add(id)
      return now
    })

  return (
    <NarrowRows>
      {entries.map((fact) =>
        fact.id === current ? (
          <NarrowRow
            key={fact.id}
            main={<FactSentence entry={fact} />}
            facts={[
              <SourceBadge key="source" source={fact.source} />,
              <FactTime key="when" entry={fact} withDay />,
              'This one',
            ]}
            current
            className="bg-accent"
          />
        ) : (
          <NarrowRow
            key={fact.id}
            main={<FactSentence entry={fact} />}
            facts={[<SourceBadge key="source" source={fact.source} />, <FactTime key="when" entry={fact} withDay />]}
            onOpen={() => toggle(fact.id)}
            open={open.has(fact.id)}
            hasLinks
          >
            {open.has(fact.id) && <EntryDetail entry={fact} around={false} />}
          </NarrowRow>
        ),
      )}
    </NarrowRows>
  )
}

/**
 * The other facts one decision left behind.
 *
 * A ban on somebody standing in one of the group's instances arrives from VRChat as a ban and an
 * instance kick, seconds apart; a ban pressed in Modbot leaves Modbot's record of who decided it
 * beside VRChat's record that it happened. The timeline shows the decision once (see
 * `AuditQuery`), and every fact behind it is here, whole, each opening into its own detail.
 */
function SameDecision({ entry }: { entry: AuditEntry }) {
  const linked = entry.linked ?? []
  if (linked.length === 0) return null

  return (
    <Card>
      <CardHeader>
        <CardTitle>Same decision</CardTitle>
      </CardHeader>
      {linked.map((fact) => (
        <details key={fact.id} className="group border-b border-b-(length:--hairline) last:border-0">
          <summary
            className="flex cursor-pointer list-none flex-wrap items-center gap-2 px-(--panel-pad) py-2 focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring [&::-webkit-details-marker]:hidden"
            style={{ fontSize: 'var(--text-small)' }}
          >
            <ChevronRight
              className="size-3.5 shrink-0 text-muted-foreground transition-transform group-open:rotate-90 motion-reduce:transition-none"
              aria-hidden
            />
            <SourceBadge source={fact.source} />
            <FactSentence entry={fact} />
            <span className="font-mono text-muted-foreground">{dateTime(fact.occurredAt)}</span>
          </summary>
          <EntryDetail entry={fact} around={false} />
        </details>
      ))}
    </Card>
  )
}

/**
 * What a snapshot fact stands for.
 *
 * A profile fact is one version of the person, so the version is shown here as the popup's
 * Profile changes tab would show it, with a way to open it there. A list snapshot is a headcount with a
 * date on it, which is all the fact recorded.
 */
function Snapshot({ entry }: { entry: AuditEntry }) {
  if (entry.type === 'vrchat.user.profile.changed' || entry.type === 'vrchat.user.profile.first-seen')
    return <ProfileAt entry={entry} />

  const count = entry.data?.['memberCount'] ?? entry.data?.['banCount'] ?? entry.data?.['count']
  if (
    (entry.type === 'vrchat.group.members.snapshot' ||
      entry.type === 'vrchat.group.bans.snapshot' ||
      entry.type === 'discord.members.snapshot') &&
    typeof count === 'number'
  ) {
    return (
      <div className="flex flex-wrap items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
        <span className="font-medium">Snapshot</span>
        <Badge variant="secondary">
          <span className="font-mono">{count.toLocaleString()}</span> listed
        </Badge>
        <span className="text-muted-foreground">
          on <span className="font-mono">{dateTime(entry.occurredAt)}</span>
        </span>
      </div>
    )
  }

  return null
}

function ProfileAt({ entry }: { entry: AuditEntry }) {
  const load = useCallback(() => api.userHistory(entry.subjectId), [entry.subjectId])
  const { data, error } = useLoad(load)

  const version = data?.versions.find((v) => v.factId === entry.id)

  return (
    <Card>
      <CardHeader>
        <CardTitle>Profile after this change</CardTitle>
        <CardAction>
          <Button size="xs" variant="outline" onClick={() => openPersonVersion(entry.subjectId, entry.id)}>
            Open in the person popup
          </Button>
        </CardAction>
      </CardHeader>
      {error && <EmptyRow tone="danger">{error}</EmptyRow>}
      {!error && !data && <EmptyRow>Loading…</EmptyRow>}
      {data && !version && <EmptyRow>This change is older than the versions still on record.</EmptyRow>}
      {version && (
        <CardContent>
          <VersionCard version={version} />
        </CardContent>
      )}
    </Card>
  )
}

function changedFields(entry: AuditEntry): [string, { old?: unknown; new?: unknown }][] {
  const changed = entry.data?.['changed']
  if (!changed || typeof changed !== 'object') return []
  return Object.entries(changed as Record<string, { old?: unknown; new?: unknown }>)
}

function shown(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—'
  if (typeof value === 'boolean') return value ? 'yes' : 'no'
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}
