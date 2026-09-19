import { buttonVariants } from '@/components/ui/button'
import { Page } from '@/components/Site'
import { GITHUB, PAGES } from '@/lib/links'

/**
 * What /discord shows when the server has no MODBOT_DISCORD_URL to send people to. The icon for it
 * is on every page and is built once, so it cannot be hidden when the address is missing; this is
 * what the person who clicks it gets instead of a not-found page they cannot explain.
 */
export function NoDiscord({ privacy = false }: { privacy?: boolean }) {
  return (
    <Page privacy={privacy}>
      <div className="mx-auto max-w-6xl px-4 py-20 sm:px-6 md:py-28">
        <h1 className="display max-w-[16ch] text-[2.5rem] leading-[1] sm:text-[3.5rem]">No Discord invite yet.</h1>
        <p className="mt-5 max-w-[34rem] text-lg text-pretty text-muted-foreground">
          Modbot&rsquo;s code, its issues and its releases are on GitHub in the meantime.
        </p>
        <div className="mt-8 flex flex-wrap gap-3">
          <a href={GITHUB} className={buttonVariants({ size: 'lg' })}>
            Open GitHub
          </a>
          <a href={PAGES.home} className={buttonVariants({ variant: 'outline', size: 'lg' })}>
            Go to modbot.co
          </a>
        </div>
      </div>
    </Page>
  )
}
