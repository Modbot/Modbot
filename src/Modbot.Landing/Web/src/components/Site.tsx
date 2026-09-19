import { Moon, Sun } from 'lucide-react'
import type { ReactNode } from 'react'
import { buttonVariants } from '@/components/ui/button'
import { DiscordIcon, GithubIcon } from '@/components/Icons'
import { SourceBadge, type Source } from '@/components/SourceBadge'
import { DISCORD, DOCS, FOUNDER, GITHUB, MY_MODBOT, OPEN_MY_SERVER, PAGES, type PageName } from '@/lib/links'
import { THEME_BUTTON } from '@/lib/theme'
import { useTheme } from '@/lib/useTheme'
import { cn } from '@/lib/utils'

/** The head-only mark, next to the word. The brand design (2026-09-16) sets both. */
export function Mark({ className }: { className?: string }) {
  return <img src="/icon-512.png" alt="" width={28} height={28} className={cn('size-7 shrink-0', className)} />
}

export function Wordmark({ className }: { className?: string }) {
  return (
    <a href={PAGES.home} className={cn('display flex items-center gap-2 rounded-md text-xl tracking-[-0.01em]', className)}>
      <Mark />
      Modbot
    </a>
  )
}

const NAV: [PageName, string][] = [
  ['features', 'Features'],
  ['instances', 'Instances'],
  ['selfHost', 'Self-host'],
  ['about', 'About'],
]

/**
 * Every page wears this: the skip link, the header, the page itself and the footer.
 *
 * @param page Which entry of the header is the page being read, so it is marked as the current one.
 * Left out on a page the header does not list.
 * @param privacy Whether the privacy policy was built, for the footer link.
 */
export function Page({ page, privacy = false, children }: { page?: PageName; privacy?: boolean; children: ReactNode }) {
  return (
    <>
      <a
        href="#main"
        className="sr-only z-50 rounded-md bg-primary px-3 py-2 text-primary-foreground focus:not-sr-only focus:fixed focus:top-2 focus:left-2"
      >
        Skip to content
      </a>
      <SiteHeader page={page} />
      <main id="main" tabIndex={-1} className="outline-none">
        {children}
      </main>
      <SiteFooter page={page} privacy={privacy} />
    </>
  )
}

/**
 * The header carries the pages and the returning-user path ("Open my server"). The pages move to
 * their own row on a phone rather than behind a button, so every one of them is one tap away.
 */
export function SiteHeader({ page }: { page?: PageName }) {
  const [theme, toggle] = useTheme()

  return (
    <header className="sticky top-0 z-40 border-b bg-ground/80 backdrop-blur-md supports-[backdrop-filter]:bg-ground/70">
      <div className="mx-auto flex h-14 max-w-6xl items-center gap-4 px-4 sm:px-6">
        <Wordmark />

        <nav aria-label="Pages" className="hidden flex-1 items-center justify-center gap-0.5 md:flex">
          {NAV.map(([name, label]) => (
            <a
              key={name}
              href={PAGES[name]}
              aria-current={page === name ? 'page' : undefined}
              className={cn(buttonVariants({ variant: 'ghost', size: 'sm' }), page === name && 'bg-accent text-accent-foreground')}
            >
              {label}
            </a>
          ))}
          <a href={DOCS} className={buttonVariants({ variant: 'ghost', size: 'sm' })}>
            Docs
          </a>
        </nav>

        <div className="ml-auto flex items-center gap-0.5 md:ml-0 md:gap-1.5">
          {/* On a phone these two sit on the second row instead, so the header fits across it. */}
          <SocialLinks className="hidden md:flex" />
          <button
            type="button"
            onClick={toggle}
            {...{ [THEME_BUTTON]: '' }}
            aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
            className={buttonVariants({ variant: 'ghost', size: 'icon' })}
          >
            <Sun className="hidden size-4 dark:block" aria-hidden="true" />
            <Moon className="size-4 dark:hidden" aria-hidden="true" />
          </button>
          <a href={OPEN_MY_SERVER} className={cn(buttonVariants({ variant: 'outline', size: 'sm' }), 'ml-1')}>
            Open my server
          </a>
        </div>
      </div>

      <div className="flex items-center border-t px-2 pb-1 md:hidden">
        <nav aria-label="Pages" className="flex gap-0.5 overflow-x-auto">
          {NAV.map(([name, label]) => (
            <a
              key={name}
              href={PAGES[name]}
              aria-current={page === name ? 'page' : undefined}
              className={cn(buttonVariants({ variant: 'ghost', size: 'sm' }), page === name && 'bg-accent text-accent-foreground')}
            >
              {label}
            </a>
          ))}
          <a href={DOCS} className={buttonVariants({ variant: 'ghost', size: 'sm' })}>
            Docs
          </a>
        </nav>
        <SocialLinks className="ml-auto" />
      </div>
    </header>
  )
}

/** Discord and GitHub, the two places the project lives outside this site. */
export function SocialLinks({ className, variant = 'ghost' }: { className?: string; variant?: 'ghost' | 'outline' }) {
  return (
    <div className={cn('flex items-center gap-0.5', className)}>
      <a href={DISCORD} aria-label="Modbot on Discord" className={buttonVariants({ variant, size: 'icon' })}>
        <DiscordIcon />
      </a>
      <a href={GITHUB} aria-label="Modbot on GitHub" className={buttonVariants({ variant, size: 'icon' })}>
        <GithubIcon />
      </a>
    </div>
  )
}

/**
 * One feature: its words on one side and a piece of the product on the other. The badges say where
 * the feature's information comes from, in the app's own source colours.
 */
export function Feature({
  id,
  sources,
  title,
  lead,
  facts,
  visual,
  flip = false,
}: {
  id: string
  sources: Source[]
  title: string
  lead: string
  facts: ReactNode[]
  visual: ReactNode
  flip?: boolean
}) {
  return (
    <section id={id} aria-labelledby={`${id}-title`} className="mx-auto max-w-6xl px-4 py-20 sm:px-6 md:py-28">
      <div className="grid items-center gap-10 lg:grid-cols-12 lg:gap-14">
        <div className={cn('min-w-0 lg:col-span-5', flip && 'lg:order-2')}>
          <div className="flex flex-wrap gap-1.5" style={{ ['--text-small' as string]: '0.8125rem' }}>
            {sources.map((s) => (
              <SourceBadge key={s} source={s} className="bg-card" />
            ))}
          </div>
          <h2 id={`${id}-title`} className="display mt-5 text-[2.25rem] leading-[1.04] sm:text-[2.875rem]">
            {title}
          </h2>
          <p className="mt-4 max-w-[34rem] text-lg text-pretty text-muted-foreground">{lead}</p>
          <ul className="mt-7 max-w-[34rem] border-t">
            {facts.map((fact, i) => (
              <li key={i} className="border-b py-2.5 text-[0.96875rem] text-pretty">
                {fact}
              </li>
            ))}
          </ul>
        </div>
        <div className={cn('min-w-0 lg:col-span-7', flip && 'lg:order-1')}>{visual}</div>
      </div>
    </section>
  )
}

/** A grid of short facts. Three across, so six of them fill two rows with no gap left over. */
export function Tiles({ items, className }: { items: [string, string][]; className?: string }) {
  return (
    <dl className={cn('grid gap-px overflow-hidden rounded-xl border bg-border sm:grid-cols-2 lg:grid-cols-3', className)}>
      {items.map(([term, detail]) => (
        <div key={term} className="bg-card p-5">
          <dt className="font-semibold">{term}</dt>
          <dd className="mt-1.5 text-[0.9375rem] text-pretty text-muted-foreground">{detail}</dd>
        </div>
      ))}
    </dl>
  )
}

/**
 * @param page The page being read, so its own link here is marked as the current one. Self-host,
 * about, license and the privacy policy are only reachable from this list on some pages.
 * @param privacy Whether the privacy policy was built. Its link is left out until
 * PRIVACY_POLICY.md exists, rather than pointing at a 404.
 */
export function SiteFooter({ page, privacy = false }: { page?: PageName; privacy?: boolean }) {
  const here = (name: PageName) => (page === name ? ('page' as const) : undefined)

  return (
    <footer className="border-t">
      <div className="mx-auto grid max-w-6xl gap-8 px-4 py-12 text-sm text-muted-foreground sm:px-6 md:grid-cols-[1fr_auto]">
        <div className="flex flex-col gap-3">
          <Wordmark className="w-fit text-[1.0625rem] text-foreground" />
          <p className="max-w-[40rem] text-pretty">Open source under AGPL-3.0.</p>
          <SocialLinks variant="outline" className="gap-1.5" />
          {/* Every mark this site names. Add to it when a new one appears on the page. */}
          <p className="max-w-[40rem] text-pretty">
            Modbot is not endorsed by or affiliated with VRChat Inc. or Discord Inc. VRChat is a trademark of
            VRChat Inc. Discord is a trademark of Discord Inc. SteamVR is a trademark of Valve Corporation. Windows
            is a trademark of Microsoft Corporation. Docker is a trademark of Docker, Inc. PostgreSQL is a trademark
            of the PostgreSQL Community Association of Canada. Railway is a trademark of Railway Corp. GitHub is a
            trademark of GitHub, Inc. Modbot and its logo are trademarks of the Modbot project.
          </p>
          <p>
            Made with{' '}
            <span aria-label="love" role="img">
              &#10084;&#65039;
            </span>{' '}
            by{' '}
            <a href={FOUNDER} className="font-medium text-foreground underline underline-offset-4">
              bin
            </a>
          </p>
        </div>
        <nav aria-label="Footer" className="flex flex-col gap-2 md:items-end">
          <a href={OPEN_MY_SERVER} className="hover:text-foreground hover:underline">
            Open my server
          </a>
          <a href={MY_MODBOT} className="hover:text-foreground hover:underline">
            my.modbot.co
          </a>
          <a href={PAGES.features} aria-current={here('features')} className="hover:text-foreground hover:underline">
            Features
          </a>
          <a href={PAGES.selfHost} aria-current={here('selfHost')} className="hover:text-foreground hover:underline">
            Self-host
          </a>
          <a href={PAGES.instances} aria-current={here('instances')} className="hover:text-foreground hover:underline">
            Open instances
          </a>
          <a href={DOCS} className="hover:text-foreground hover:underline">
            Docs
          </a>
          <a href={PAGES.about} aria-current={here('about')} className="hover:text-foreground hover:underline">
            About
          </a>
          <a href={PAGES.license} aria-current={here('license')} className="hover:text-foreground hover:underline">
            Licence
          </a>
          {privacy && (
            <a href={PAGES.privacy} aria-current={here('privacy')} className="hover:text-foreground hover:underline">
              Privacy policy
            </a>
          )}
        </nav>
      </div>
    </footer>
  )
}
