import { Page, SocialLinks } from '@/components/Site'
import people from '@/data/about.json'

/** One person on the page. Only the name is certain; a role and a link are both optional. */
type Person = { name: string; role?: string; url?: string }

/*
 * The four groups, in the order the site owner wrote them. Their titles are his own words and carry
 * no full stop. A group with nobody in it renders nothing at all, so the page never announces that a
 * section is empty.
 */
function sections(contributors: Person[]): [string, string, Person[]][] {
  return [
    ['team', 'Modbot Team', people.team],
    ['contributors', 'Contributors', [...people.contributors, ...contributors]],
    ['sponsors', 'Sponsors', people.sponsors],
    ['early-adopters', 'Early Adopters', people.earlyAdopters],
  ]
}

/**
 * @param privacy Whether the privacy policy was built, for the footer link.
 * @param contributors Everyone who has committed to the repository, read from GitHub at build time
 * by scripts/prerender.ts and already stripped of anybody about.json names. Empty when that build
 * had no network.
 */
export function About({ privacy = false, contributors = [] }: { privacy?: boolean; contributors?: Person[] }) {
  return (
    <Page page="about" privacy={privacy}>
      <section aria-labelledby="mission-title" className="mx-auto max-w-6xl px-4 py-14 sm:px-6 md:py-20">
        <h1 id="mission-title" className="display max-w-[16ch] text-[2.5rem] leading-[1] sm:text-[3.5rem]">
          What Modbot is for.
        </h1>
        <p className="mt-5 max-w-[42rem] text-lg text-pretty text-muted-foreground">
          VRChat&rsquo;s group tools do not record why someone was banned, and the audit log goes back about a
          month. Modbot keeps those records for as long as you want, in a database you own, on a server you run.
          It is open source under AGPL-3.0, self-hosted by each group, and not affiliated with VRChat Inc.
        </p>
        <SocialLinks variant="outline" className="mt-7 gap-1.5" />
      </section>

      {sections(contributors)
        .filter(([, , list]) => list.length > 0)
        .map(([id, title, list]) => (
          <section key={id} aria-labelledby={`${id}-title`} className="mx-auto max-w-6xl px-4 pb-14 sm:px-6 md:pb-20">
            <h2 id={`${id}-title`} className="display text-[1.75rem] leading-[1.05] sm:text-[2.25rem]">
              {title}
            </h2>
            <ul className="mt-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {list.map((person) => (
                <li key={person.name} className="rounded-xl border bg-card p-5">
                  <p className="font-semibold break-words">
                    {person.url ? <PersonLink person={person} /> : person.name}
                  </p>
                  {person.role && <p className="mt-1 text-[0.9375rem] text-muted-foreground">{person.role}</p>}
                </li>
              ))}
            </ul>
          </section>
        ))}
    </Page>
  )
}

/* A link off this site is opened in its own tab and told not to pass the address along. */
function PersonLink({ person }: { person: Person }) {
  const external = person.url?.startsWith('http') ?? false

  return (
    <a
      href={person.url}
      className="rounded-md underline underline-offset-4"
      {...(external && { target: '_blank', rel: 'noreferrer' })}
    >
      {person.name}
    </a>
  )
}
