import { Moon, Sun } from 'lucide-react'
import { buttonVariants } from '@/components/ui/button'
import { SourceBadge, type Source } from '@/components/SourceBadge'
import { DOCS, MY_MODBOT, OPEN_MY_SERVER, SELF_HOSTING_GUIDE } from '@/lib/links'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'

/** The head-only mark, next to the word. The brand design (2026-09-16) sets both. */
export function Mark({ className }: { className?: string }) {
  return <img src="/icon-512.png" alt="" width={28} height={28} className={cn('size-7 shrink-0', className)} />
}

export function Wordmark({ className }: { className?: string }) {
  return (
    <a href="/#top" className={cn('display flex items-center gap-2 rounded-md text-xl tracking-[-0.01em]', className)}>
      <Mark />
      Modbot
    </a>
  )
}

const SECTIONS = [
  { href: '/#live', label: 'Live' },
  { href: '/#discord', label: 'Discord' },
  { href: '/#case-files', label: 'Case files' },
  { href: '/#client', label: 'Client' },
  { href: '/#self-host', label: 'Self-host' },
]

/**
 * The header carries the returning-user path ("Open my server") and, past phone width, the primary
 * call to action. The hero speaks to the owner who installs once; the header serves the moderator
 * who comes back every night.
 */
export function SiteHeader() {
  const [theme, toggle] = useTheme()

  return (
    <header className="sticky top-0 z-40 border-b bg-ground/80 backdrop-blur-md supports-[backdrop-filter]:bg-ground/70">
      <div className="mx-auto flex h-14 max-w-6xl items-center gap-4 px-4 sm:px-6">
        <Wordmark />
        <nav aria-label="Sections" className="hidden flex-1 items-center justify-center gap-0.5 md:flex">
          {SECTIONS.map((s) => (
            <a key={s.href} href={s.href} className={buttonVariants({ variant: 'ghost', size: 'sm' })}>
              {s.label}
            </a>
          ))}
        </nav>
        <div className="ml-auto flex items-center gap-1.5 md:ml-0">
          <button
            type="button"
            onClick={toggle}
            aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
            className={buttonVariants({ variant: 'ghost', size: 'icon' })}
          >
            <Sun className="hidden size-4 dark:block" aria-hidden="true" />
            <Moon className="size-4 dark:hidden" aria-hidden="true" />
          </button>
          <a href={OPEN_MY_SERVER} className={buttonVariants({ variant: 'outline', size: 'sm' })}>
            Open my server
          </a>
          <a href={SELF_HOSTING_GUIDE} className={cn(buttonVariants({ size: 'sm' }), 'hidden sm:inline-flex')}>
            Host your own
          </a>
        </div>
      </div>
    </header>
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
  facts: React.ReactNode[]
  visual: React.ReactNode
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

/**
 * @param privacy Whether the privacy policy was built. Its link is left out until
 * PRIVACY_POLICY.md exists, rather than pointing at a 404.
 */
export function SiteFooter({ privacy = false }: { privacy?: boolean }) {
  return (
    <footer className="border-t">
      <div className="mx-auto grid max-w-6xl gap-8 px-4 py-12 text-sm text-muted-foreground sm:px-6 md:grid-cols-[1fr_auto]">
        <div className="flex flex-col gap-3">
          <Wordmark className="w-fit text-[1.0625rem] text-foreground" />
          <p className="max-w-[40rem] text-pretty">Open source under AGPL-3.0.</p>
          {/* Every mark this site names. Add to it when a new one appears on the page. */}
          <p className="max-w-[40rem] text-pretty">
            Modbot is not endorsed by or affiliated with VRChat Inc. or Discord Inc. VRChat is a trademark of
            VRChat Inc. Discord is a trademark of Discord Inc. SteamVR is a trademark of Valve Corporation. Windows
            is a trademark of Microsoft Corporation. Docker is a trademark of Docker, Inc. PostgreSQL is a trademark
            of the PostgreSQL Community Association of Canada. Railway is a trademark of Railway Corp. Modbot and its
            logo are trademarks of the Modbot project.
          </p>
        </div>
        <nav aria-label="Footer" className="flex flex-col gap-2 md:items-end">
          <a href={OPEN_MY_SERVER} className="hover:text-foreground hover:underline">
            Open my server
          </a>
          <a href={MY_MODBOT} className="hover:text-foreground hover:underline">
            my.modbot.co
          </a>
          <a href={SELF_HOSTING_GUIDE} className="hover:text-foreground hover:underline">
            Host your own
          </a>
          <a href={DOCS} className="hover:text-foreground hover:underline">
            Docs
          </a>
          {privacy && (
            <a href="/privacy" className="hover:text-foreground hover:underline">
              Privacy policy
            </a>
          )}
        </nav>
      </div>
    </footer>
  )
}
