import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Panel } from '@/components/subject/shared'
import { api, ApiError, type CurrentUser, type DiscordLinkView, type PersonSide } from '@/lib/api'
import { formatDay } from '@/lib/format'
import { can } from '@/lib/permissions'

/**
 * The person's Discord account, beside their VRChat one, so a moderator reads one person rather
 * than two records (M5 §5.3).
 *
 * The card draws whatever is known. Where the two accounts proved a link, it also carries when
 * they linked, the roles Modbot gave and — for whoever holds Manage Discord links — **Unlink**,
 * which ends the link and lets the role job take those roles back. History stays either way.
 *
 * Where the Discord account was found some other way — typed onto a Modbot account, or the
 * account the link itself named — none of that is drawn, because none of it exists. The card must
 * never make an unproved id look like a proved link (one view per person design §3).
 */
export function DiscordLinkCard({
  side,
  vrchatUserId,
  me,
}: {
  side: PersonSide
  /** The VRChat account to look the link up by, when there is one. */
  vrchatUserId: string | null
  me: CurrentUser
}) {
  const [link, setLink] = useState<DiscordLinkView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const linked = side.foundBy === 'link' && vrchatUserId !== null

  const load = useCallback(() => {
    if (!linked || !vrchatUserId) return Promise.resolve()

    return api
      .discordLinkFor(vrchatUserId)
      .then((r) => {
        setLink(r.link)
        setError(null)
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the Discord link.'))
  }, [linked, vrchatUserId])

  useEffect(() => {
    void load()
  }, [load])

  const unlink = () => {
    if (!link) return
    setBusy(true)
    api
      .unlinkDiscord(link.id)
      .then(load)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not unlink.'))
      .finally(() => setBusy(false))
  }

  return (
    <Panel title="Discord">
      <div className="flex flex-col gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
        {error && <p className="text-destructive">{error}</p>}

        <p>
          {side.name ?? link?.discordUsername ?? (
            <span className="font-mono" title={side.id}>
              {side.id}
            </span>
          )}
        </p>
        {(side.name ?? link?.discordUsername) && (
          <p className="font-mono text-muted-foreground" title={side.id}>
            {side.id}
          </p>
        )}

        {link && (
          <>
            <p className="text-muted-foreground">Linked <span className="font-mono">{formatDay(link.linkedAt)}</span></p>

            {link.roles.length > 0 && (
              <div className="flex flex-wrap items-center gap-1">
                <span className="text-muted-foreground">Roles:</span>
                {link.roles.map((role) => (
                  <Badge key={role.id} variant="secondary" title={role.id}>
                    {role.name ?? role.id}
                  </Badge>
                ))}
              </div>
            )}

            {link.notInServer && <p className="text-muted-foreground">Not in the server</p>}
            {link.roleError && <p className="text-destructive">{link.roleError}</p>}

            {can(me, 'ManageDiscordLinks') && (
              <div>
                <Button type="button" variant="outline" size="xs" onClick={unlink} disabled={busy}>
                  Unlink
                </Button>
              </div>
            )}
          </>
        )}
      </div>
    </Panel>
  )
}
