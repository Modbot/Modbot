import type { ComponentProps } from 'react'
import ReactMarkdown, { defaultUrlTransform, type Components } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { CodeBlock } from '@/components/CodeBlock'
import { cn } from '@/lib/utils'

/**
 * A moderator's written reason, rendered for other moderators.
 *
 * Evidence design §17: markdown authored by staff and read by staff is an attack surface too, so
 * two rules hold here and nowhere else decides them.
 *
 * **Raw HTML is never rendered.** `skipHtml` drops every HTML node before anything reaches the
 * DOM, and there is no `rehype-raw` in the pipeline; `<script>` in a written reason is simply
 * absent from what is shown. Links keep only `http`, `https` and `mailto` (react-markdown's own
 * default), so `javascript:` and `data:` cannot arrive on an `href`, and every link opens in a
 * new tab with `rel="noopener noreferrer"`.
 *
 * **Images resolve only to evidence attached to the same case file**, through an internal scheme:
 * `![what it shows](evidence:<sha256>)`. An image pointing at an external server would fire
 * whenever any moderator opened the case file, reporting their address and the time they read it
 * -- a tracking pixel aimed at the moderation team. So an external image URL is never fetched;
 * it is shown as its alt text with a note saying why.
 */
export function Markdown({
  text,
  images,
  className,
  rehypePlugins,
  components: extra,
}: {
  text: string
  /** Object URLs for the attached evidence this case file may show inline, by hash. */
  images?: ReadonlyMap<string, string>
  className?: string
  /**
   * Added after the built-in pipeline, for a caller that marks the text up further -- Chat
   * highlights code and turns the people a tool found into links. Never `rehype-raw`: the rule
   * above holds for every caller.
   */
  rehypePlugins?: ComponentProps<typeof ReactMarkdown>['rehypePlugins']
  /** Drawn instead of the built-in rendering for these tags. */
  components?: Partial<Components>
}) {
  const components: Components = {
    a: ({ href, children }) => (
      <a href={href} target="_blank" rel="noopener noreferrer" className="underline underline-offset-2">
        {children}
        <span className="text-muted-foreground" aria-hidden="true">
          {' '}
          ↗
        </span>
      </a>
    ),
    img: ({ src, alt }) => {
      const match = EVIDENCE.exec(typeof src === 'string' ? src : '')
      const url = match ? images?.get(match[1]) : undefined

      if (url) {
        return <img src={url} alt={alt ?? ''} className="my-2 max-h-96 max-w-full rounded-xl border" />
      }

      return (
        <span
          className="inline-block rounded-md border px-1.5 py-0.5 text-muted-foreground"
          style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
          title={match ? 'Evidence not available' : 'External image not shown'}
        >
          [image{alt ? `: ${alt}` : ''}]
        </span>
      )
    },
    pre: ({ children }) => <CodeBlock>{children}</CodeBlock>,
    // Wide tables scroll inside the answer rather than stretching the column.
    table: ({ children }) => (
      <div className="my-2 max-w-full overflow-x-auto">
        <table className="border-collapse">{children}</table>
      </div>
    ),
    ...extra,
  }

  return (
    <div
      className={cn(
        'break-words [&_a]:text-foreground [&_blockquote]:border-l-2 [&_blockquote]:pl-3 [&_blockquote]:text-muted-foreground',
        '[&_code]:rounded [&_code]:bg-muted [&_code]:px-1 [&_code]:font-mono [&_code]:text-[0.9em]',
        '[&_h1]:mt-3 [&_h1]:mb-1 [&_h1]:font-semibold [&_h2]:mt-3 [&_h2]:mb-1 [&_h2]:font-semibold [&_h3]:mt-2 [&_h3]:font-medium',
        '[&_hr]:my-3 [&_li]:my-0.5 [&_ol]:my-1 [&_ol]:list-decimal [&_ol]:pl-5 [&_p]:my-1.5 [&_ul]:my-1 [&_ul]:list-disc [&_ul]:pl-5',
        // The code block styles itself (see CodeBlock); only the code inside it is set here.
        '[&_pre_code]:bg-transparent [&_pre_code]:p-0',
        '[&_table]:my-2 [&_table]:border-collapse [&_td]:border [&_td]:px-2 [&_td]:py-0.5 [&_th]:border [&_th]:px-2 [&_th]:py-0.5 [&_th]:text-left',
        className,
      )}
    >
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={rehypePlugins}
        skipHtml
        urlTransform={transformUrl}
        components={components}
      >
        {text}
      </ReactMarkdown>
    </div>
  )
}

const EVIDENCE = /^evidence:([0-9a-f]{64})$/

/**
 * `src` may only be the internal evidence scheme; anything else on an image becomes an empty
 * string, which the `img` component above renders as a note rather than a request. `href` keeps
 * react-markdown's default allowlist.
 */
function transformUrl(url: string, key: string): string {
  if (key === 'src') return EVIDENCE.test(url) ? url : ''
  return defaultUrlTransform(url)
}
