import { Heart, MessageCircle, Repeat2 } from 'lucide-react'
import type { BlueskyCounts } from '@/lib/posts'

/** The likes, reposts and replies under a sent Bluesky post: an icon and a number each. */
export function PostNumbers({ counts }: { counts: BlueskyCounts }) {
  const items = [
    { Icon: Heart, label: 'Likes', n: counts.likes },
    { Icon: Repeat2, label: 'Reposts', n: counts.reposts },
    { Icon: MessageCircle, label: 'Replies', n: counts.replies },
  ]

  return (
    <span className="inline-flex items-center gap-x-3 text-muted-foreground">
      {items.map(({ Icon, label, n }) => (
        <span key={label} className="inline-flex items-center gap-1" title={label}>
          <Icon className="size-3.5" aria-hidden />
          {n}
          <span className="sr-only">{label.toLowerCase()}</span>
        </span>
      ))}
    </span>
  )
}
