import { useCallback } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { dateTime } from '@/components/charts'
import { PersonLink, InstanceLink, SourceBadge, WorldLink } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
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
    <div className="grid gap-4 border-t bg-muted/20 px-4 py-3 lg:grid-cols-2" style={{ borderTopWidth: 'var(--hairline)' }}>
      <div className="flex flex-col gap-4">
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
            {entry.occurredBefore
              ? `Between ${dateTime(entry.occurredAt)} and ${dateTime(entry.occurredBefore)}`
              : dateTime(entry.occurredAt)}
          </Item>
          <Item label="Recorded">{dateTime(entry.observedAt)}</Item>
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
          {entry.description && <Item label="Description">{entry.description}</Item>}
          <Item label="Entry id">
            <span className="font-mono">{entry.id}</span>
          </Item>
        </dl>

        {changed.length > 0 && (
          <div>
            <div className="mb-1 font-medium" style={{ fontSize: 'var(--text-small)' }}>Changed</div>
            <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
              <thead className="text-left text-muted-foreground">
                <tr>
                  <th className="py-1 pr-3 font-normal">Field</th>
                  <th className="py-1 pr-3 font-normal">Before</th>
                  <th className="py-1 font-normal">After</th>
                </tr>
              </thead>
              <tbody>
                {changed.map(([key, pair]) => (
                  <tr key={key} className="border-t align-top" style={{ borderTopWidth: 'var(--hairline)' }}>
                    <td className="py-1 pr-3 whitespace-nowrap">{fieldName(key)}</td>
                    <td className="py-1 pr-3 break-all text-muted-foreground">{shown(pair.old)}</td>
                    <td className="py-1 break-all">{shown(pair.new)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <Snapshot entry={entry} />
      </div>

      <div className="flex flex-col gap-3">
        <JsonView title="Payload" value={entry.data} />
        <JsonView title="Entry" value={entry} />
      </div>
    </div>
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
        <Badge variant="secondary">{count.toLocaleString()} listed</Badge>
        <span className="text-muted-foreground">on {dateTime(entry.occurredAt)}</span>
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
    <div className="flex flex-col gap-2 rounded-md border p-3" style={{ borderWidth: 'var(--hairline)' }}>
      <div className="flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
        <span className="font-medium">Profile after this change</span>
        <span className="flex-1" />
        <Button size="xs" variant="outline" onClick={() => openPersonVersion(entry.subjectId, entry.id)}>
          Open in the person popup
        </Button>
      </div>
      {error && <p className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>{error}</p>}
      {!error && !data && <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>Loading…</p>}
      {data && !version && (
        <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          This change is older than the versions still on record.
        </p>
      )}
      {version && <VersionCard version={version} />}
    </div>
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
