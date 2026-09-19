import { Page } from '@/components/Site'

/**
 * PRIVACY_POLICY.md from the repository root, turned into HTML at build time. The markup comes from
 * a file in this repository, never from a visitor, and the page's policy allows no inline script.
 */
export function Privacy({ html }: { html: string }) {
  return (
    <Page page="privacy" privacy>
      <div className="mx-auto max-w-6xl px-4 py-14 sm:px-6 md:py-20">
        <article className="policy max-w-[42rem]" dangerouslySetInnerHTML={{ __html: html }} />
      </div>
    </Page>
  )
}
