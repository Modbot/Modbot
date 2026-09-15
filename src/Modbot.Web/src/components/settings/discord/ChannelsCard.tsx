import { useCallback, useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { api, ApiError, type DiscordRoute, type DiscordRoutes } from '@/lib/api'
import { EVENT_POST_NEEDS, missingLabel, useDiscordChannels } from '@/lib/discordLists'
import { cn } from '@/lib/utils'
import { Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'
import { RouteEditor } from './RouteEditor'

/** Whether a route has any filter beyond its event types. */
function filtered(route: DiscordRoute): boolean {
  return (
    route.subjectIds.length > 0 ||
    route.actorIds.length > 0 ||
    route.actorAutomatic ||
    route.subjectVRChatRoleIds.length > 0 ||
    route.actorVRChatRoleIds.length > 0 ||
    route.actorModbotRoleIds.length > 0
  )
}

/**
 * The channels events are sent to (Discord event routes design §7): one row per route, with
 * Add channel, Edit, on/off and Delete. Each change is saved as it is made.
 */
export function ChannelsCard() {
  const [data, setData] = useState<DiscordRoutes | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<DiscordRoute | 'new' | null>(null)
  const [confirming, setConfirming] = useState<string | null>(null)
  const { data: channels } = useDiscordChannels()

  const load = useCallback(
    () =>
      api
        .discordRoutes()
        .then((next) => {
          setData(next)
          setLoadError(null)
        })
        .catch((e: unknown) =>
          setLoadError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to change Discord settings.'
              : 'Could not load the channels.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  const toggle = (route: DiscordRoute, enabled: boolean) => {
    setError(null)
    api
      .updateDiscordRoute(route.id, { enabled })
      .then(() => load())
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
  }

  const remove = (route: DiscordRoute) => {
    setError(null)
    setConfirming(null)
    api
      .deleteDiscordRoute(route.id)
      .then(() => load())
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not delete.'))
  }

  const channelLabel = (id: string) => {
    const channel = channels?.channels.find((c) => c.id === id)
    if (!channel) return { name: channels ? 'Unknown channel' : id, mark: channels ? id : null, problem: false }
    if (channel.removed) return { name: `#${channel.name}`, mark: 'Removed', problem: true }
    const missing = missingLabel(channel, EVENT_POST_NEEDS)
    return { name: `#${channel.name}`, mark: missing, problem: missing !== null }
  }

  return (
    <SettingsCard
      title="Channels"
      span={12}
      action={
        data && (
          <Button type="button" size="sm" variant="outline" onClick={() => setEditing('new')}>
            <Plus />
            Add channel
          </Button>
        )
      }
      footer={error ? <Outcome tone="problem">{error}</Outcome> : undefined}
    >
      {loadError ? (
        <Outcome tone="problem">{loadError}</Outcome>
      ) : !data ? (
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Loading…
        </span>
      ) : data.routes.length === 0 ? (
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          No channels
        </span>
      ) : (
        <ul className="flex flex-col divide-y" style={{ fontSize: 'var(--text-small)' }}>
          {data.routes.map((route) => {
            const channel = channelLabel(route.channelId)
            return (
              <li key={route.id} className="flex flex-wrap items-center gap-x-4 gap-y-2 py-2">
                <div className="flex min-w-[12rem] flex-1 flex-wrap items-baseline gap-x-2">
                  <span className={cn('font-medium', !route.enabled && 'text-muted-foreground')}>{channel.name}</span>
                  {route.name && <span className="text-muted-foreground">{route.name}</span>}
                  {channel.mark && (
                    <span className={cn('text-xs', channel.problem ? 'text-warn' : 'text-muted-foreground')}>
                      {channel.mark}
                    </span>
                  )}
                </div>
                <span className="text-muted-foreground tabular-nums">
                  {route.eventTypes.length === 1 ? '1 event' : `${route.eventTypes.length} events`}
                  {filtered(route) && ' · Filtered'}
                </span>
                <Switch checked={route.enabled} onChange={(enabled) => toggle(route, enabled)}>
                  {route.enabled ? 'On' : 'Off'}
                </Switch>
                <div className="flex gap-2">
                  <Button type="button" size="xs" variant="outline" onClick={() => setEditing(route)}>
                    Edit
                  </Button>
                  {confirming === route.id ? (
                    <>
                      <Button type="button" size="xs" variant="destructive" onClick={() => remove(route)}>
                        Delete
                      </Button>
                      <Button type="button" size="xs" variant="ghost" onClick={() => setConfirming(null)}>
                        Cancel
                      </Button>
                    </>
                  ) : (
                    <Button type="button" size="xs" variant="ghost" onClick={() => setConfirming(route.id)}>
                      Delete
                    </Button>
                  )}
                </div>
              </li>
            )
          })}
        </ul>
      )}

      <Dialog open={editing !== null} onOpenChange={(open) => !open && setEditing(null)}>
        {data && editing && (
          <DialogContent
            title={editing === 'new' ? 'Add channel' : 'Edit channel'}
            className="max-h-[90vh] max-w-[760px]"
            bodyClassName="overflow-y-auto"
          >
            <RouteEditor
              key={editing === 'new' ? 'new' : editing.id}
              route={editing === 'new' ? null : editing}
              options={data}
              onCancel={() => setEditing(null)}
              onSaved={() => {
                setEditing(null)
                void load()
              }}
            />
          </DialogContent>
        )}
      </Dialog>
    </SettingsCard>
  )
}
