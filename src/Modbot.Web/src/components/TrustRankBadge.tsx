import { cn } from '@/lib/utils'
import { trustRank, trustRankColour, trustRankLabel } from '@/lib/trustRank'

/**
 * A person's VRChat trust rank: a dot in VRChat's colour for the rank, and the rank's name.
 *
 * The same shape as the source badge on a fact: the colour is a second channel and the word
 * carries the identity, so a rank whose VRChat colour is faint on one theme still reads on both.
 * Renders nothing when the rank is unknown -- a person nobody has read yet is not a Visitor.
 */
export function TrustRankBadge({ rank, className }: { rank: unknown; className?: string }) {
  const known = trustRank(rank)
  if (!known) return null

  return (
    <span
      className={cn(
        'inline-flex shrink-0 items-center gap-1.5 rounded-full border px-1.5 py-0 font-medium whitespace-nowrap text-muted-foreground',
        className,
      )}
      style={{ fontSize: '0.6875rem', borderWidth: 'var(--hairline)' }}
    >
      <span className="size-1.5 shrink-0 rounded-full" style={{ background: trustRankColour(known) }} />
      {trustRankLabel(known)}
    </span>
  )
}
