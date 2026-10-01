import { useCallback, useEffect, useState } from 'react'
import { WorldLink } from '@/components/facts'
import { HeadCount } from '@/components/HeadCount'
import { Panel } from '@/components/subject/shared'
import { Button } from '@/components/ui/button'
import { ApiError } from '@/lib/api'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { playersText, vrchatWorldUrl, worldPickApi, type NextGame as NextGameView } from '@/lib/worldLists'

/**
 * Next game during an event that picks from a world list (world lists design §6): the world picked
 * last, the people in the event's instance now, and the buttons that pick. Opens no instance.
 */
export function NextGame({ eventId, instanceId = null }: { eventId: string; instanceId?: string | null }) {
  const [view, setView] = useState<NextGameView | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    worldPickApi
      .nextGame(eventId)
      .then((answer) => {
        if (!cancelled) setView(answer)
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof ApiError ? e.message : 'Could not load the next game.')
      })
    return () => {
      cancelled = true
    }
  }, [eventId])

  if (error) return <div className="text-destructive">{error}</div>
  if (!view) return null

  return <NextGamePanel view={view} instanceId={instanceId ?? view.instanceId} onChange={setView} />
}

/** Next game in the instance popup, when the instance belongs to an open event that picks from a list. */
export function InstanceNextGame({ instanceId }: { instanceId: string }) {
  const [view, setView] = useState<NextGameView | null>(null)

  useEffect(() => {
    let cancelled = false
    worldPickApi
      .forInstance(instanceId)
      .then((answer) => {
        if (!cancelled) setView(answer ?? null)
      })
      .catch(() => {
        if (!cancelled) setView(null)
      })
    return () => {
      cancelled = true
    }
  }, [instanceId])

  if (!view) return null

  return (
    <Panel title="Next game">
      <NextGamePanel view={view} instanceId={instanceId} onChange={setView} inPanel />
    </Panel>
  )
}

function NextGamePanel({
  view,
  instanceId,
  onChange,
  inPanel = false,
}: {
  view: NextGameView
  instanceId: string | null
  onChange: (view: NextGameView) => void
  /** Inside a popup's section, which names it already. */
  inPanel?: boolean
}) {
  const [busy, setBusy] = useState(false)
  const [another, setAnother] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const pick = useCallback(
    (instead: string | null) => {
      setBusy(true)
      setError(null)
      setAnother(instead !== null)
      worldPickApi
        .pickNextGame(view.eventId, instanceId, instead)
        .then(onChange)
        .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not pick the next game.'))
        .finally(() => setBusy(false))
    },
    [view.eventId, instanceId, onChange],
  )

  const game = view.game
  const players = game ? playersText(game.minPlayers, game.maxPlayers) : null

  return (
    <div
      className={cn('flex flex-col gap-2', !inPanel && 'border-t border-t-(length:--hairline) pt-3')}
      style={{ fontSize: 'var(--text-small)' }}
    >
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        {!inPanel && <span className="font-label">Next game</span>}
        <span className="text-muted-foreground">{view.listName}</span>
        {view.people !== null && (
          <span>
            <span className="text-muted-foreground">People now </span>
            <span className="font-mono">
              <HeadCount count={view.people} unsure={view.peopleUnsure} />
            </span>
          </span>
        )}
      </div>

      {game && (
        <div className="flex items-start gap-3">
          {game.thumbnailUrl && (
            <img
              src={vrchatMedia(game.thumbnailUrl)}
              alt=""
              className="aspect-[4/3] w-24 shrink-0 rounded-sm object-cover"
              loading="lazy"
            />
          )}
          <div className="flex min-w-0 flex-col gap-0.5">
            <WorldLink id={game.worldId} name={game.name} unnamed="id" className="font-medium [overflow-wrap:anywhere]" />
            {players && (
              <span>
                <span className="text-muted-foreground">Players </span>
                <span className="font-mono">{players}</span>
              </span>
            )}
            <a className="underline" href={vrchatWorldUrl(game.worldId)} target="_blank" rel="noreferrer">
              vrchat.com
            </a>
          </div>
        </div>
      )}

      {view.noneFits && <div className="text-warn">{noneFitsText(view.people, another)}</div>}
      {error && <div className="text-destructive">{error}</div>}

      {view.canPick && (
        <div className="flex flex-wrap gap-2">
          <Button size="sm" disabled={busy} onClick={() => pick(null)}>
            Next game
          </Button>
          {game && (
            <Button size="sm" variant="outline" disabled={busy} onClick={() => pick(game.worldId)}>
              Pick another
            </Button>
          )}
        </div>
      )}
    </div>
  )
}

function noneFitsText(people: number | null, another: boolean): string {
  if (people === null) return another ? 'No other world in the list.' : 'The list is empty.'
  const who = `${people} ${people === 1 ? 'person' : 'people'}`
  return another ? `No other world fits ${who}.` : `No world fits ${who}.`
}
