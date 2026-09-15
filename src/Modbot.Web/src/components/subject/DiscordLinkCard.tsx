import { useCallback, useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { api, ApiError, type CurrentUser, type DiscordLinkView } from '@/lib/api'
import { formatDay } from '@/lib/format'
import { can } from '@/lib/permissions'
import { DiscordPersonLink } from '@/components/facts'

/**
 * The Discord account linked to this VRChat person (M5 §5.3), beside their VRChat profile, so a
 * moderator reads one person rather than two records.
 *
 * Draws nothing while there is no link. Unlink needs Manage Discord links; it ends the link and
 * the role job takes back the roles Modbot gave. History stays either way.
 */
export function DiscordLinkCard({ subjectId, me }: { subjectId: string; me: CurrentUser }) {
  const [link, setLink] = useState<DiscordLinkView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(
    () =>
      api
        .discordLinkFor(subjectId)
        .then((r) => {
          setLink(r.link)
          setError(null)
        })
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the Discord link.')),
    [subjectId],
  )

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

  if (!link && !error) return null

  return (
    <div
      className="rounded-md border px-3 py-2"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <div className="font-medium">Discord</div>

      {error && <p className="mt-1 text-destructive">{error}</p>}

      {link && (
        <div className="mt-1 flex flex-col gap-1.5">
          <p>
            <DiscordPersonLink id={link.discordUserId} name={link.discordUsername} />{' '}
            <span className="font-mono text-muted-foreground" title={link.discordUserId}>
              {link.discordUserId}
            </span>
          </p>
          <p className="text-muted-foreground">Linked {formatDay(link.linkedAt)}</p>

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
              <Button type="button" variant="outline" size="sm" onClick={unlink} disabled={busy}>
                Unlink
              </Button>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
