import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'

/**
 * The small pieces the Discord member list and the Discord person popup share: a picture, and a
 * role drawn in the colour the server gave it.
 */

/** A Discord role as a chip, with a dot in the role's colour. A role with no colour gets no dot. */
export function RoleChip({ name, id, color }: { name: string | null; id: string; color: number }) {
  return (
    <span
      className="inline-flex max-w-[12rem] items-center gap-1 rounded-full border px-2 py-0.5"
      style={{ borderWidth: 'var(--hairline)', fontSize: '0.6875rem' }}
      title={id}
    >
      {color !== 0 && (
        <span
          className="size-2 shrink-0 rounded-full"
          style={{ background: `#${color.toString(16).padStart(6, '0')}` }}
        />
      )}
      <span className="truncate">{name ?? id}</span>
    </span>
  )
}

/** A round picture, or an empty circle of the same size when there is none. */
export function Avatar({ url, className }: { url: string | null | undefined; className?: string }) {
  return url ? (
    <img
      src={vrchatMedia(url)}
      alt=""
      className={cn('size-7 shrink-0 rounded-full bg-muted object-cover', className)}
      referrerPolicy="no-referrer"
    />
  ) : (
    <div className={cn('size-7 shrink-0 rounded-full bg-muted', className)} />
  )
}
