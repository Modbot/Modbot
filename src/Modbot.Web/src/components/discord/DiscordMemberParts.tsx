import { Badge } from '@/components/ui/badge'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'

/**
 * The small pieces the Discord member list and the Discord person popup share: a picture, and a
 * role drawn in the colour the server gave it.
 */

/** A Discord role as a badge, with a square in the role's colour. A role with no colour gets no square. */
export function RoleChip({ name, id, color }: { name: string | null; id: string; color: number }) {
  return (
    <Badge variant="secondary" className="max-w-[12rem]" title={id}>
      {color !== 0 && (
        <span
          aria-hidden
          className="size-2 shrink-0"
          style={{ background: `#${color.toString(16).padStart(6, '0')}` }}
        />
      )}
      <span className="truncate">{name ?? id}</span>
    </Badge>
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
