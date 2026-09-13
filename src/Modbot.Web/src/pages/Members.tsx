import { Card, CardContent } from '@/components/ui/card'

/**
 * Members — not built yet.
 *
 * This screen used to render a dozen hardcoded names, ids and "hours in world" figures, ported
 * straight from the design prototype. In a prototype that is exactly right; in a running
 * deployment it is a lie that cannot be detected from the inside. A moderator looking at
 * "ΛƧƬΛ · 18h 22m · 3 prior actions" has no way to know they are reading a mockup, and the
 * failure mode is somebody acting on it.
 *
 * The member list arrives with the sync that produces it (M1). Until the data is real the screen
 * says so, which is the only honest thing an empty feature can do.
 */
export function Members() {
  return (
    <Card>
      <CardContent className="py-10 text-center text-muted-foreground">
        <div className="font-medium text-foreground">Members</div>
        <p className="mx-auto mt-1 max-w-md" style={{ fontSize: 'var(--text-small)' }}>
          Not built yet. Modbot has not synced the group's member list — that lands with member
          sync, along with search, roles and time in world.
        </p>
      </CardContent>
    </Card>
  )
}
