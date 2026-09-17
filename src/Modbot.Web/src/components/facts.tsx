import { cn } from '@/lib/utils'
import { sourceLabel } from '@/lib/format'
import { instanceName, instanceNumber } from '@/lib/instanceName'
import { openDiscordPerson, openInstance, openPerson, openWorld } from '@/lib/subject'
import type { AuditEntry } from '@/lib/api'

/**
 * The pieces every screen that shows a fact reuses.
 *
 * One place, because the two things most easily got wrong — how a source is attributed, and how an
 * imprecise time is displayed — have to be got right identically on the audit log, the ban list
 * and the subject pane.
 */

/** Sources get distinct, stable colours so the merged timeline is readable without reading. */
const SOURCE_SERIES: Record<string, number> = {
  AuditLog: 1,
  SyncDiff: 3,
  Client: 4,
  Discord: 5,
  Manual: 2,
  Modbot: 2,
  Import: 2,
}

/** Which system said so. The colour is a second channel; the label carries the identity. */
export function SourceBadge({ source, className }: { source: string; className?: string }) {
  const series = SOURCE_SERIES[source] ?? 1

  return (
    <span
      className={cn('inline-flex shrink-0 items-center gap-1.5 rounded-full border px-2 py-0.5', className)}
      style={{ fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)' }}
    >
      <span className="size-1.5 shrink-0 rounded-full" style={{ background: `var(--series-${series})` }} />
      {sourceLabel(source)}
    </span>
  )
}

const time = (iso: string) =>
  new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })

const dateTime = (iso: string) => new Date(iso).toLocaleString()

/**
 * When a fact happened — as an instant when that is known, and as a range when it is not.
 *
 * Spec 5.3: a sync diff knows only that something happened between two polls. Collapsing that to
 * the lower bound invents precision Modbot does not have, and the invention is invisible — the
 * timestamp looks exactly like one VRChat stated. So a windowed fact is rendered as a window, with
 * the tilde carrying the claim even when the row is scanned rather than read.
 */
export function FactTime({ entry }: { entry: Pick<AuditEntry, 'occurredAt' | 'occurredBefore'> }) {
  if (!entry.occurredBefore) {
    return (
      <span className="tabular-nums text-muted-foreground" title={dateTime(entry.occurredAt)}>
        {time(entry.occurredAt)}
      </span>
    )
  }

  return (
    <span
      className="tabular-nums text-muted-foreground"
      title={`Between ${dateTime(entry.occurredAt)} and ${dateTime(entry.occurredBefore)}`}
    >
      ~{time(entry.occurredAt)}–{time(entry.occurredBefore)}
    </span>
  )
}

/**
 * A person, as a launcher for their pane.
 *
 * Spec 10.2: every list that renders a person is a launcher, so there is one component, one fetch
 * shape, and one place to add anything new about a person.
 */
export function SubjectLink({
  id,
  name,
  onOpen,
  className,
}: {
  id: string
  name?: string | null
  /** Defaults to opening this person's popup, which is what every caller wants. */
  onOpen?: (id: string) => void
  className?: string
}) {
  return (
    <button
      type="button"
      onClick={() => (onOpen ?? openPerson)(id)}
      title={id}
      className={cn(
        'max-w-[18rem] truncate rounded-md text-left hover:underline focus-visible:outline-2 focus-visible:outline-ring',
        name ? 'font-medium' : 'font-mono',
        className,
      )}
      style={{ display: 'inline' }}
    >
      {name ?? id}
    </button>
  )
}

/** A Discord account, as a launcher for the Discord person popup. */
export function DiscordPersonLink({
  id,
  name,
  className,
}: {
  id: string
  name?: string | null
  className?: string
}) {
  return <SubjectLink id={id} name={name} onOpen={openDiscordPerson} className={className} />
}

/**
 * A person on whichever platform they are on: Discord opens the Discord popup, anything else the
 * VRChat one. Platform names arrive as `Discord` from facts and `discord` from analytics.
 */
export function PersonLink({
  platform,
  id,
  name,
  className,
}: {
  platform: string | null | undefined
  id: string
  name?: string | null
  className?: string
}) {
  return platform?.toLowerCase() === 'discord' ? (
    <DiscordPersonLink id={id} name={name} className={className} />
  ) : (
    <SubjectLink id={id} name={name} className={className} />
  )
}

/** Shared look for every id that opens something. Inline, so it sits inside a sentence. */
const linkClass =
  'rounded-md text-left font-medium hover:underline focus-visible:outline-2 focus-visible:outline-ring'

/**
 * A world, as a launcher for its popup.
 *
 * A world Modbot has not read the page of yet has no name, which is ordinary rather than an
 * error: it says so and still opens, because the popup can show the instances and the time even when
 * the name is unknown.
 */
export function WorldLink({
  id,
  name,
  unnamed = 'not read yet',
  className,
}: {
  id: string
  name?: string | null
  /**
   * What to show with no name: `not read yet` where the caller looked the name up and there was
   * none, `id` where nobody looked — saying "not read yet" there would be a claim nobody checked.
   */
  unnamed?: 'not read yet' | 'id'
  className?: string
}) {
  return (
    <button
      type="button"
      onClick={() => openWorld(id)}
      title={id}
      className={cn(linkClass, !name && 'text-muted-foreground', !name && unnamed === 'id' && 'font-mono', className)}
      style={{ display: 'inline' }}
    >
      {name ?? (unnamed === 'id' ? id : 'a world Modbot has not read yet')}
    </button>
  )
}

/**
 * An instance, as a launcher for its popup: "The Black Cat #19453", the world's name and VRChat's
 * number as one link, the way VRChat shows an instance in game.
 *
 * `modbotInstanceId` is Modbot's own id and is what makes the instance one instance; it is what
 * the popup opens on. With no id matched — the fact happened outside every instance Modbot has a
 * row for — the world stays a link and the number is plain text, rather than a link to somebody
 * else's evening. A world Modbot has not read yet is named by its id.
 */
export function InstanceLink({
  modbotInstanceId,
  worldId,
  worldName,
  number,
  className,
}: {
  modbotInstanceId?: string | null
  worldId?: string | null
  worldName?: string | null
  number?: string | null
  className?: string
}) {
  if (modbotInstanceId) {
    return (
      <button
        type="button"
        onClick={() => openInstance(modbotInstanceId)}
        title={worldId ?? undefined}
        className={cn(linkClass, className)}
        style={{ display: 'inline' }}
      >
        {instanceName(worldName, worldId, number)}
      </button>
    )
  }

  if (worldId) {
    return (
      <span className={className}>
        <WorldLink id={worldId} name={worldName} unnamed="id" />
        {number ? <span className="text-muted-foreground"> {instanceNumber(number)}</span> : null}
      </span>
    )
  }

  return <span className={cn('text-muted-foreground', className)}>{instanceName(null, null, number)}</span>
}
