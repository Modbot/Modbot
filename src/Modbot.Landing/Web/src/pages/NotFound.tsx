import { buttonVariants } from '@/components/ui/button'
import { Wordmark } from '@/components/Site'
import { PAGES } from '@/lib/links'

/** Rendered once at build, with no script on the page. */
export function NotFound() {
  return (
    <main className="mx-auto flex min-h-dvh max-w-6xl flex-col justify-center px-4 py-16 sm:px-6">
      <Wordmark className="w-fit" />
      <img src="/mascot.png" alt="" width={445} height={512} className="mt-10 w-32" />
      <h1 className="display mt-6 text-[3rem] leading-[0.98] sm:text-[4.5rem]">Nothing here.</h1>
      <p className="mt-4 max-w-[30rem] text-lg text-muted-foreground">This address doesn&rsquo;t lead to a page.</p>
      <div className="mt-8">
        <a href={PAGES.home} className={buttonVariants({ size: 'lg' })}>
          Go to modbot.co
        </a>
      </div>
    </main>
  )
}
