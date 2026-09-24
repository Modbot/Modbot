import { Badge } from '@/components/ui/badge'
import { cn } from '@/lib/utils'
import { trustRank, trustRankColour, trustRankLabel } from '@/lib/trustRank'

/**
 * A person's VRChat trust rank: a square in VRChat's colour for the rank, and the rank's name.
 *
 * The same shape as the source badge on a fact: the colour is a second channel and the word
 * carries the identity, so a rank whose VRChat colour is faint on one theme still reads on both.
 * Renders nothing when the rank is unknown -- a person nobody has read yet is not a Visitor.
 */
export function TrustRankBadge({ rank, className }: { rank: unknown; className?: string }) {
  const known = trustRank(rank)
  if (!known) return null

  return (
    <Badge variant="outline" className={cn('gap-1.5', className)}>
      <span aria-hidden className="size-1.5 shrink-0" style={{ background: trustRankColour(known) }} />
      {trustRankLabel(known)}
    </Badge>
  )
}
