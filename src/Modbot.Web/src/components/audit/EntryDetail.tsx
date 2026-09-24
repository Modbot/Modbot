import { useCallback } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { dateTime } from '@/components/charts'
import { FactSentence } from '@/components/factSentence'
import { PersonLink, InstanceLink, SourceBadge, WorldLink } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
import { EmptyRow } from '@/components/PanelGrid'
import { VersionCard } from '@/components/subject/ProfileVersions'
import { api, type AuditEntry } from '@/lib/api'
import { fieldName } from '@/lib/profileFields'
import { openPersonVersion } from '@/lib/subject'
import { useLoad } from '@/lib/useLoad'

/**
 * Everything one audit log entry holds, under its row.
 *
 * The sentence in the row says what happened; this says everything the fact recorded: every
 * column, the diff a change carried, the snapshot a profile fact stands for, and the payload and
 * the whole entry as JSON. Nothing here reworded — the JSON is what the server answered.
 */
export function EntryDetail({ entry }: { entry: AuditEntry }) {
  const changed = changedFields(entry)

  return (
    <div className="grid gap-3 border-t bg-muted/20 p-(--panel-pad) lg:grid-cols-2" style={{ borderTopWidth: 'var(--hairline)' }}>
      <div className="flex flex-col gap-3">
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
          <Item label="Type">
            <span className="font-mono">{entry.type}</span>
            {entry.typeRaw && (
              <span className="ml-2 font-mono text-muted-foreground" title="The source's own word">
                {entry.typeRaw}
              </span>
            )}
          </Item>
          <Item label="Log">{entry.category}</Item>
          <Item label="Source">
            <SourceBadge source={entry.source} />
          </Item>
          <Item label="When">
            {entry.occurredBefore ? (
              <>
                Between <span className="font-mono">{dateTime(entry.occurredAt)}</span> and{' '}
                <span className="font-mono">{dateTime(entry.occurredBefore)}</span>
              </>
            ) : (
              <span className="font-mono">{dateTime(entry.occurredAt)}</span>
            )}
          </Item>
          <Item label="Recorded">
            <span className="font-mono">{dateTime(entry.observedAt)}</span>
          </Item>
          <Item label="About">
            <span className="text-muted-foreground">{entry.subjectKind.toLowerCase()} · {entry.subjectPlatform} · </span>
            {entry.subjectKind === 'Person' ? (
              <PersonLink platform={entry.subjectPlatform} id={entry.subjectId} name={entry.subjectName} />
            ) : (
              <span className="font-mono break-all">{entry.subjectId}</span>
            )}
            {entry.subjectName && entry.subjectKind === 'Person' && (
              <span className="ml-2 font-mono text-muted-foreground break-all">{entry.subjectId}</span>
            )}
          </Item>
          <Item label="Done by">
            {entry.actorId ? (
              <>
                <span className="text-muted-foreground">{entry.actorPlatform} · </span>
                <PersonLink platform={entry.actorPlatform} id={entry.actorId} name={entry.actorName} />
                <span className="ml-2 font-mono text-muted-foreground break-all">{entry.actorId}</span>
              </>
            ) : (
              <span className="text-muted-foreground">nobody named</span>
            )}
          </Item>
          {entry.worldId && (
            <Item label="World">
              <WorldLink id={entry.worldId} name={entry.worldName} unnamed="id" />
              <span className="ml-2 font-mono text-muted-foreground break-all">{entry.worldId}</span>
            </Item>
          )}
          {entry.instanceId && (
            <Item label="Instance">
              <InstanceLink
                modbotInstanceId={entry.modbotInstanceId}
                worldId={entry.worldId}
                worldName={entry.worldName}
                number={entry.instanceId}
              />
            </Item>
          )}
          {entry.reportedBy && entry.reportedBy.length > 0 && (
            <Item label="Reported by">
              <ul>
                {entry.reportedBy.map((reporter) => (
                  <li key={reporter.accountId}>
                    {reporter.name ?? reporter.accountId}
                    <span className="ml-2 font-mono text-muted-foreground">{dateTime(reporter.at)}</span>
                  </li>
                ))}
              </ul>
            </Item>
          )}
          {entry.description && <Item label="Description">{entry.description}</Item>}
          <Item label="Entry id">
            <span className="font-mono">{entry.id}</span>
          </Item>
        </dl>

        {changed.length > 0 && (
          <Card>
            <CardHeader>
              <CardTitle>Changed</CardTitle>
            </CardHeader>
            <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
              <thead className="bg-strip text-left text-muted-foreground">
                <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                  <th className="px-3 py-1 font-normal">Field</th>
                  <th className="px-3 py-1 font-normal">Before</th>
                  <th className="px-3 py-1 font-normal">After</th>
                </tr>
              </thead>
              <tbody>
                {changed.map(([key, pair]) => (
                  <tr key={key} className="border-b border-b-(length:--hairline) align-top last:border-0">
                    <td className="px-3 py-1 whitespace-nowrap">{fieldName(key)}</td>
                    <td className="px-3 py-1 break-all text-muted-foreground">{shown(pair.old)}</td>
                    <td className="px-3 py-1 break-all">{shown(pair.new)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
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
        <JsonView title="The whole record" value={entry} closed />
      </div>
    </div>
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
        <details
          key={fact.id}
          className="border-b border-b-(length:--hairline) last:border-0"
        >
          <summary
            className="flex cursor-pointer flex-wrap items-center gap-2 px-(--panel-pad) py-2"
            style={{ fontSize: 'var(--text-small)' }}
          >
            <SourceBadge source={fact.source} />
            <FactSentence entry={fact} />
            <span className="font-mono text-muted-foreground">{dateTime(fact.occurredAt)}</span>
          </summary>
          <EntryDetail entry={fact} />
        </details>
      ))}
    </Card>
  )
}

function Item({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="min-w-0 break-words">{children}</dd>
    </>
  )
}

/**
 * What a snapshot fact stands for.
 *
 * A profile fact is one version of the person, so the version is shown here as the popup's
 * History tab would show it, with a way to open it there. A list snapshot is a headcount with a
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
      {error && <p className="px-(--panel-pad) py-2 text-destructive" style={{ fontSize: 'var(--text-small)' }}>{error}</p>}
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
