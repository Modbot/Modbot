import { Page } from '@/components/Site'

/**
 * LICENSE from the repository root, put on the page as it is written. The file wraps its own lines
 * at about seventy characters, which is wider than a phone; pre-wrap keeps those breaks and folds
 * the rest, so the text reads down the screen instead of off the side of it.
 */
export function License({ text, privacy = false }: { text: string; privacy?: boolean }) {
  return (
    <Page page="license" privacy={privacy}>
      <div className="mx-auto max-w-6xl px-4 py-14 sm:px-6 md:py-20">
        <h1 className="display max-w-[16ch] text-[2.5rem] leading-[1] sm:text-[3.5rem]">Licence.</h1>
        <p className="mt-5 max-w-[42rem] text-lg text-pretty text-muted-foreground">
          Modbot is open source. You may use, modify, self-host and fork it under the terms of the GNU Affero
          General Public License v3.0.
        </p>
        <pre className="mt-10 rounded-xl border bg-card p-5 font-mono text-[0.8125rem] leading-6 whitespace-pre-wrap">
          {text}
        </pre>
      </div>
    </Page>
  )
}
