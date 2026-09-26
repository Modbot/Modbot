import { useCallback, useEffect, useRef, useState } from 'react'
import { ChevronRight } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Row } from '@/components/ui/fact-row'
import { dateTime } from '@/components/charts'
import { SourceBadge } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
import { EmptyRow } from '@/components/PanelGrid'
import { Panel } from '@/components/subject/shared'
import { api, type ProfileFields, type ProfileVersion } from '@/lib/api'
import { formatDay } from '@/lib/format'
import { fieldName } from '@/lib/profileFields'
import { useLoad } from '@/lib/useLoad'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'

/**
 * A person's profile over time: every version the facts can replay, newest first, with the one
 * chosen shown in full.
 *
 * Opened from the audit log at the version one fact recorded (`?version=`), or from the popup's
 * History tab at the newest. The versions come from the server, which walks the profile-changed
 * facts backwards from the row (see `VRChatUserHistory`); nothing here guesses.
 */
export function ProfileVersions({ id, openAt }: { id: string; openAt: number | null }) {
  const load = useCallback(() => api.userHistory(id), [id])
  const { data, error } = useLoad(load)
  const [chosen, setChosen] = useState<number | null>(openAt)

  if (error) return <Panel title="History" flush><EmptyRow tone="danger">{error}</EmptyRow></Panel>
  if (!data) return <Panel title="History" flush><EmptyRow>Loading…</EmptyRow></Panel>

  if (!data.known || data.versions.length === 0)
    return <Panel title="History" flush><EmptyRow>No profile recorded yet.</EmptyRow></Panel>

  const version = data.versions.find((v) => v.factId === chosen) ?? data.versions[0]

  return (
    <div className="grid min-h-0 flex-1 md:h-full md:grid-cols-[18rem_minmax(0,1fr)] md:overflow-hidden">
      <ol className="flex flex-col border-b border-b-(length:--hairline) md:overflow-auto md:border-r md:border-b-0 md:border-r-(length:--hairline)">
        {data.versions.map((v) => (
          <VersionRow key={v.factId} version={v} chosen={v.factId === version.factId} onClick={() => setChosen(v.factId)} />
        ))}
      </ol>

      <div className="min-h-0 overflow-auto p-(--panel-pad)">
        <VersionCard version={version} />
      </div>
    </div>
  )
}

function VersionRow({ version, chosen, onClick }: { version: ProfileVersion; chosen: boolean; onClick: () => void }) {
  const row = useRef<HTMLLIElement>(null)
  const brought = useRef(false)

  useEffect(() => {
    if (!chosen || brought.current) return
    brought.current = true
    row.current?.scrollIntoView({ block: 'nearest' })
  }, [chosen])

  return (
    <li ref={row}>
      <button
        type="button"
        onClick={onClick}
        aria-current={chosen}
        className={cn(
          'flex w-full flex-col gap-0.5 border-b-(length:--hairline) px-(--panel-pad) py-2 text-left hover:bg-muted',
          chosen && 'bg-accent text-accent-foreground',
        )}
        style={{ fontSize: 'var(--text-small)' }}
      >
        <span className="flex items-center gap-2">
          <span className="font-mono">{formatDay(version.at)}</span>
          {version.current && <Badge variant="secondary">Now</Badge>}
          {version.baseline && <Badge variant="outline">First seen</Badge>}
        </span>
        <span className="truncate text-muted-foreground">
          {version.changed.length > 0 ? version.changed.map(fieldName).join(', ') : version.baseline ? 'Recorded' : ''}
        </span>
      </button>
    </li>
  )
}

/** One version, laid out the way the profile card lays out the profile now, with the record itself behind a control. */
export function VersionCard({ version }: { version: ProfileVersion }) {
  const p = version.profile
  const [showJson, setShowJson] = useState(false)

  return (
    <div className="flex flex-col gap-3" style={{ fontSize: 'var(--text-small)' }}>
      <div className="flex flex-wrap items-center gap-2">
        <SourceBadge source={version.source} />
        {version.before ? (
          <span className="text-muted-foreground">
            Between <span className="font-mono">{dateTime(version.at)}</span> and{' '}
            <span className="font-mono">{dateTime(version.before)}</span>
          </span>
        ) : (
          <span className="font-mono text-muted-foreground">{dateTime(version.at)}</span>
        )}
        {version.current && <Badge variant="secondary">As stored now</Badge>}
        {version.baseline && <Badge variant="outline">First seen</Badge>}
      </div>

      {version.changed.length > 0 && (
        <div className="flex flex-wrap items-center gap-1">
          <span className="text-muted-foreground">Changed:</span>
          {version.changed.map((f) => (
            <Badge key={f} variant="outline">
              {fieldName(f)}
            </Badge>
          ))}
        </div>
      )}

      <Fields fields={p} highlight={version.changed} />

      <div className="flex flex-col gap-2">
        <button
          type="button"
          onClick={() => setShowJson((s) => !s)}
          aria-expanded={showJson}
          className="flex items-center gap-1 self-start text-muted-foreground hover:text-foreground hover:underline"
        >
          <ChevronRight className={cn('size-3.5 shrink-0 transition-transform', showJson && 'rotate-90')} />
          {showJson ? 'Hide JSON' : 'JSON'}
        </button>
        {showJson && <JsonView title="Profile" value={p} />}
      </div>
    </div>
  )
}

/** The profile's fields, with the ones this change touched marked. */
export function Fields({ fields: p, highlight }: { fields: ProfileFields; highlight: string[] }) {
  const picture = vrchatMedia(p.profilePictureUrl)
  const banner = vrchatMedia(p.bannerUrl)
  const icon = vrchatMedia(p.iconUrl)
  const groupIcon = vrchatMedia(p.representedGroup?.iconUrl)
  const marked = (field: string) => (highlight.includes(field) ? 'ring-2 ring-ring/50 rounded-sm' : '')

  return (
    <div className="flex gap-3">
      {picture ? (
        <img src={picture} alt="" className={cn('size-20 shrink-0 rounded-full bg-muted object-cover', marked('profilePicOverride') || marked('currentAvatarThumbnailImageUrl'))} referrerPolicy="no-referrer" />
      ) : (
        <div className="size-20 shrink-0 rounded-full bg-muted" />
      )}

      <div className="flex min-w-0 max-w-lg flex-1 flex-col">
        <div className={cn('px-1', marked('displayName'))}>
          <Row label="Name" value={p.displayName ?? '—'} />
        </div>
        <div className={cn('px-1', marked('bannerUrl'))}>
          <Row
            label="Banner"
            value={
              banner ? (
                <img src={banner} alt="" className="aspect-[3/1] w-full max-w-xs bg-muted object-cover" referrerPolicy="no-referrer" />
              ) : '—'
            }
          />
        </div>
        <div className={cn('px-1', marked('iconUrl'))}>
          <Row
            label="Icon"
            value={
              icon ? (
                <img src={icon} alt="" className="size-8 rounded-full bg-muted object-cover" referrerPolicy="no-referrer" />
              ) : '—'
            }
          />
        </div>
        <div className={cn('px-1', marked('representedGroup'))}>
          <Row
            label="Represented group"
            title={p.representedGroup?.groupId}
            value={
              p.representedGroup ? (
                <span className="inline-flex items-center gap-1.5">
                  {groupIcon ? (
                    <img src={groupIcon} alt="" className="size-5 shrink-0 rounded-full bg-muted object-cover" referrerPolicy="no-referrer" />
                  ) : (
                    <span className="size-5 shrink-0 rounded-full bg-muted" />
                  )}
                  {p.representedGroup.name}
                </span>
              ) : '—'
            }
          />
        </div>
        <div className={cn('px-1', marked('pronouns'))}>
          <Row label="Pronouns" value={p.pronouns ?? '—'} />
        </div>
        <div className={cn('px-1', marked('statusDescription'))}>
          <Row label="Status line" value={p.statusDescription ?? '—'} />
        </div>
        <div className={cn('px-1', marked('bio'))}>
          <Row label="Bio" value={<span className="whitespace-pre-wrap break-words">{p.bio ?? '—'}</span>} />
        </div>
        <div className={cn('px-1', marked('ageVerificationStatus') || marked('ageVerified'))}>
          <Row
            label="Age"
            value={p.ageVerificationStatus ?? (p.ageVerified === null ? '—' : p.ageVerified ? 'verified' : 'not verified')}
          />
        </div>
        <div className={cn('px-1', marked('dateJoined'))}>
          <Row label="Joined VRChat" value={p.dateJoined ?? '—'} mono />
        </div>
        <div className={cn('px-1', marked('tags'))}>
          <Row
            label="Tags"
            value={
              p.tags.length === 0 ? '—' : (
                <span className="flex flex-wrap justify-end gap-1">
                  {p.tags.map((t) => (
                    <Badge key={t} variant="outline" className="font-mono">{t}</Badge>
                  ))}
                </span>
              )
            }
          />
        </div>
        <div className={cn('px-1', marked('currentAvatarImageUrl'))}>
          <Row
            label="Avatar picture"
            mono
            value={
              p.avatarImageUrl ? (
                <a href={p.avatarImageUrl} target="_blank" rel="noreferrer noopener" className="break-all underline underline-offset-2">
                  {p.avatarImageUrl}
                </a>
              ) : '—'
            }
          />
        </div>
      </div>
    </div>
  )
}
