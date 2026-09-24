import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { api, type Showcase, type ShowcasePerson } from '@/lib/api'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'

/**
 * The people the project thanks: the repository's contributors, the sponsors and the early
 * adopters, read from Modbot Cloud.
 *
 * Absent rather than broken when Cloud is off or unreachable: none of this changes what Modbot
 * does, and a page of error boxes about a list of names would be worse than a page without it.
 */
export function People() {
  const [showcase, setShowcase] = useState<Showcase | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .creditsShowcase()
      .then((next) => {
        if (!cancelled) setShowcase(next)
      })
      .catch(() => {
        if (!cancelled) setShowcase({ available: false, contributors: [], sponsors: [], earlyAdopters: [] })
      })

    return () => {
      cancelled = true
    }
  }, [])

  if (!showcase) {
    return (
      <Card>
        <EmptyRow>Loading…</EmptyRow>
      </Card>
    )
  }

  const empty =
    showcase.contributors.length === 0 &&
    showcase.sponsors.length === 0 &&
    showcase.earlyAdopters.length === 0

  if (empty) {
    return (
      <Card>
        <EmptyRow>Nothing to show.</EmptyRow>
      </Card>
    )
  }

  return (
    <PanelGrid className="grid-cols-1">
      {showcase.sponsors.length > 0 && <PeopleCard title="Sponsors" people={showcase.sponsors} />}
      {showcase.earlyAdopters.length > 0 && (
        <PeopleCard title="Early adopters" people={showcase.earlyAdopters} />
      )}

      {showcase.contributors.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle>
              <h2>Contributors</h2>
            </CardTitle>
          </CardHeader>
          <CardContent>
            <ul className="flex flex-wrap gap-2">
              {showcase.contributors.map((person) => (
                <li key={person.login}>
                  <Badge asChild variant="outline" className={cn('gap-2 py-0.5 text-foreground', person.avatarUrl && 'pl-1')}>
                    <a href={person.url} target="_blank" rel="noreferrer">
                      {person.avatarUrl && (
                        <img
                          src={vrchatMedia(person.avatarUrl)}
                          alt=""
                          loading="lazy"
                          className="size-5 rounded-full"
                          referrerPolicy="no-referrer"
                        />
                      )}
                      {person.login}
                    </a>
                  </Badge>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      )}
    </PanelGrid>
  )
}

function PeopleCard({ title, people }: { title: string; people: ShowcasePerson[] }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>
          <h2>{title}</h2>
        </CardTitle>
      </CardHeader>
      {/* The panel grid's own lines, on a list: the tiles share one hairline with each other and
          with the panel's edge. */}
      <ul data-slot="panel-grid" className="m-0 sm:grid-cols-2">
        {people.map((person) => (
          <li key={`${person.name}-${person.vrChatGroupId ?? person.link}`}>
            <Person person={person} />
          </li>
        ))}
      </ul>
    </Card>
  )
}

function Person({ person }: { person: ShowcasePerson }) {
  const group = person.vrChatGroupId
    ? `https://vrchat.com/home/group/${encodeURIComponent(person.vrChatGroupId)}`
    : null

  // The group link wins when there is one: for a VRChat group, "open the group" is the thing
  // somebody reading the Credits page actually wants.
  const href = group ?? (person.link || null)

  // These three addresses are Modbot Cloud's own: it fetches each picture when an administrator
  // saves the row and serves it from its domain, because the addresses typed in are usually
  // VRChat's and VRChat will not serve them to a page that is not its own. Nothing to work around
  // here any more — they are ordinary pictures.
  const banner = person.groupBannerUrl
  const picture = person.groupImageUrl || person.imageUrl

  const inside = (
    <>
      {banner && (
        <img
          src={vrchatMedia(banner)}
          alt=""
          loading="lazy"
          className="h-20 w-full object-cover"
        />
      )}
      <div className="flex items-center gap-2">
        {picture && (
          <img
            src={vrchatMedia(picture)}
            alt=""
            loading="lazy"
            className="size-8 shrink-0 rounded-full object-cover"
          />
        )}
        <span className="min-w-0 font-medium [overflow-wrap:anywhere]">{person.name}</span>
        {group && (
          <span className="ml-auto shrink-0 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            VRChat group
          </span>
        )}
      </div>
    </>
  )

  const className = 'flex h-full flex-col gap-2 p-(--panel-pad)'

  return href ? (
    <a href={href} target="_blank" rel="noreferrer" className={`${className} hover:bg-muted/50`}>
      {inside}
    </a>
  ) : (
    <div className={className}>{inside}</div>
  )
}
