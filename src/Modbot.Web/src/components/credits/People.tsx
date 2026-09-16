import { useEffect, useState } from 'react'
import { Card, CardContent } from '@/components/ui/card'
import { api, type Showcase, type ShowcasePerson } from '@/lib/api'

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
        <CardContent className="py-10 text-center text-muted-foreground">Loading…</CardContent>
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
        <CardContent className="py-10 text-center text-muted-foreground">Nothing to show.</CardContent>
      </Card>
    )
  }

  return (
    <div className="flex flex-col gap-4">
      {showcase.sponsors.length > 0 && <PeopleCard title="Sponsors" people={showcase.sponsors} />}
      {showcase.earlyAdopters.length > 0 && (
        <PeopleCard title="Early adopters" people={showcase.earlyAdopters} />
      )}

      {showcase.contributors.length > 0 && (
        <Card className="gap-3 py-4">
          <CardContent className="flex flex-col gap-3 px-4 sm:px-5">
            <h2 className="font-medium">Contributors</h2>
            <ul className="flex flex-wrap gap-2">
              {showcase.contributors.map((person) => (
                <li key={person.login}>
                  <a
                    href={person.url}
                    target="_blank"
                    rel="noreferrer"
                    className="flex items-center gap-2 rounded-full border px-2.5 py-1 hover:bg-muted/50"
                    style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
                  >
                    {person.avatarUrl && (
                      <img
                        src={person.avatarUrl}
                        alt=""
                        loading="lazy"
                        className="size-5 rounded-full"
                        referrerPolicy="no-referrer"
                      />
                    )}
                    {person.login}
                  </a>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      )}
    </div>
  )
}

function PeopleCard({ title, people }: { title: string; people: ShowcasePerson[] }) {
  return (
    <Card className="gap-3 py-4">
      <CardContent className="flex flex-col gap-3 px-4 sm:px-5">
        <h2 className="font-medium">{title}</h2>
        <ul className="grid gap-3 sm:grid-cols-2">
          {people.map((person) => (
            <li key={`${person.name}-${person.vrChatGroupId ?? person.link}`}>
              <Person person={person} />
            </li>
          ))}
        </ul>
      </CardContent>
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
  const banner = person.groupBannerUrl
  const picture = person.groupImageUrl || person.imageUrl

  const inside = (
    <>
      {banner && (
        <img
          src={banner}
          alt=""
          loading="lazy"
          referrerPolicy="no-referrer"
          className="h-20 w-full rounded-md object-cover"
        />
      )}
      <div className="flex items-center gap-2">
        {picture && (
          <img
            src={picture}
            alt=""
            loading="lazy"
            referrerPolicy="no-referrer"
            className="size-8 shrink-0 rounded-md object-cover"
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

  const className = 'flex flex-col gap-2 rounded-lg border p-2'

  return href ? (
    <a
      href={href}
      target="_blank"
      rel="noreferrer"
      className={`${className} hover:bg-muted/50`}
      style={{ borderWidth: 'var(--hairline)' }}
    >
      {inside}
    </a>
  ) : (
    <div className={className} style={{ borderWidth: 'var(--hairline)' }}>
      {inside}
    </div>
  )
}
