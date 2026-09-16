import { useEffect, useState } from 'react'
import { buttonVariants } from '@/components/ui/button'
import { SiteFooter, SiteHeader } from '@/components/Site'
import { type Group, type Room, openFor, order, regionName } from '@/lib/rooms'
import { cn } from '@/lib/utils'

type State = { status: 'loading' } | { status: 'ready'; groups: Group[] } | { status: 'failed' }

/*
 * /rooms — every group whose Modbot reports its open rooms, and the rooms they have open.
 *
 * The list is read from this site's own /api/rooms, which reads Modbot Cloud with a key the browser
 * never sees. Nothing here counts people: the feed has no field for a head count, because the
 * Modbot that reported the room never sent one.
 */

export default function RoomsPage({ privacy = false }: { privacy?: boolean }) {
  const [state, setState] = useState<State>({ status: 'loading' })
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    let live = true

    const load = () =>
      fetch('/api/rooms', { headers: { accept: 'application/json' } })
        .then((r) => (r.ok ? r.json() : Promise.reject(new Error(String(r.status)))))
        .then((body: { groups?: Group[] }) => {
          if (live) setState({ status: 'ready', groups: order(body.groups ?? []) })
        })
        .catch(() => {
          if (live) setState((was) => (was.status === 'ready' ? was : { status: 'failed' }))
        })

    void load()

    // The same minute the server holds its own read for, so an open tab stays close to the truth
    // without asking more often than the answer can change.
    const timer = window.setInterval(() => {
      setNow(Date.now())
      void load()
    }, 60_000)

    return () => {
      live = false
      window.clearInterval(timer)
    }
  }, [])

  return (
    <>
      <SiteHeader />
      <main id="main" tabIndex={-1} className="outline-none">
        <div className="mx-auto max-w-6xl px-4 pt-10 pb-20 sm:px-6 sm:pt-14 md:pt-16">
          <h1 className="display text-[2.5rem] leading-[1.02] sm:text-[3.5rem]">Open rooms</h1>

          <div className="mt-10">
            {state.status === 'loading' ? (
              <p className="text-muted-foreground">Loading…</p>
            ) : state.status === 'failed' ? (
              <p className="text-muted-foreground">Could not load the rooms.</p>
            ) : state.groups.length === 0 ? (
              <p className="text-muted-foreground">No groups listed</p>
            ) : (
              <ul className="grid gap-5 lg:grid-cols-2">
                {state.groups.map((group) => (
                  <GroupCard key={group.groupId} group={group} now={now} />
                ))}
              </ul>
            )}
          </div>
        </div>
      </main>
      <SiteFooter privacy={privacy} />
    </>
  )
}

function GroupCard({ group, now }: { group: Group; now: number }) {
  const name = group.groupName ?? group.groupId

  return (
    <li className="flex min-w-0 flex-col overflow-hidden rounded-xl border bg-card">
      {group.groupBannerUrl && (
        <img src={group.groupBannerUrl} alt="" loading="lazy" className="h-24 w-full object-cover sm:h-28" />
      )}

      <div className="flex min-w-0 items-center gap-3 px-4 py-3.5">
        {group.groupIconUrl ? (
          <img
            src={group.groupIconUrl}
            alt=""
            loading="lazy"
            className="size-10 shrink-0 rounded-lg object-cover"
          />
        ) : (
          <div aria-hidden="true" className="size-10 shrink-0 rounded-lg bg-accent" />
        )}
        <h2 className="min-w-0 flex-1 truncate font-medium">{name}</h2>
        <a
          href={group.groupUrl}
          rel="noopener nofollow"
          className={cn(buttonVariants({ variant: 'outline', size: 'sm' }), 'shrink-0')}
        >
          Group
        </a>
      </div>

      {group.rooms.length === 0 ? (
        <p className="border-t px-4 py-3.5 text-sm text-muted-foreground">No rooms open</p>
      ) : (
        <ul className="border-t">
          {group.rooms.map((room) => (
            <RoomRow key={room.location} room={room} now={now} />
          ))}
        </ul>
      )}
    </li>
  )
}

function RoomRow({ room, now }: { room: Room; now: number }) {
  const region = regionName(room.region)
  const age = openFor(room.openedAt, now)

  return (
    <li className="flex min-w-0 items-center gap-3 px-4 py-3 [&:not(:last-child)]:border-b">
      {room.worldImageUrl ? (
        <img
          src={room.worldImageUrl}
          alt=""
          loading="lazy"
          className="h-10 w-16 shrink-0 rounded-md object-cover"
        />
      ) : (
        <div aria-hidden="true" className="h-10 w-16 shrink-0 rounded-md bg-accent" />
      )}

      <div className="min-w-0 flex-1">
        <p className="truncate text-[0.9375rem]">{room.worldName ?? room.worldId}</p>
        <p className="truncate text-sm text-muted-foreground">{[region, age].filter(Boolean).join(' · ')}</p>
      </div>

      {room.joinLink && (
        <a
          href={room.joinLink}
          rel="noopener nofollow"
          className={cn(buttonVariants({ size: 'sm' }), 'shrink-0')}
        >
          Join
        </a>
      )}
    </li>
  )
}
