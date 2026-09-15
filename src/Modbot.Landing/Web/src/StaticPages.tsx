import { buttonVariants } from '@/components/ui/button'
import { Mark, SiteFooter } from '@/components/Site'

/*
 * The pages rendered once at build with no script: the not-found page and the privacy policy.
 */

function Top() {
  return (
    <header className="border-b">
      <div className="mx-auto flex h-14 max-w-6xl items-center px-4 sm:px-6">
        <a href="/" className="flex items-center gap-2 rounded-md font-semibold tracking-tight">
          <Mark />
          Modbot
        </a>
      </div>
    </header>
  )
}

export function NotFound() {
  return (
    <main className="mx-auto flex min-h-dvh max-w-6xl flex-col justify-center px-4 py-16 sm:px-6">
      <a href="/" className="flex w-fit items-center gap-2 rounded-md font-semibold tracking-tight">
        <Mark />
        Modbot
      </a>
      <h1 className="display mt-10 text-[3.5rem] leading-[0.95] sm:text-[5rem]">Nothing here.</h1>
      <p className="mt-4 max-w-[30rem] text-lg text-muted-foreground">This address doesn&rsquo;t lead to a page.</p>
      <div className="mt-8">
        <a href="/" className={buttonVariants({ size: 'lg' })}>
          Go to modbot.co
        </a>
      </div>
    </main>
  )
}

/**
 * PRIVACY_POLICY.md from the repository root, turned into HTML at build time. The markup comes from
 * a file in this repository, never from a visitor, and the page's policy allows no inline script.
 */
export function Privacy({ html }: { html: string }) {
  return (
    <>
      <Top />
      <main className="mx-auto max-w-6xl px-4 py-14 sm:px-6 md:py-20">
        <article className="policy max-w-[42rem]" dangerouslySetInnerHTML={{ __html: html }} />
      </main>
      <SiteFooter privacy />
    </>
  )
}
