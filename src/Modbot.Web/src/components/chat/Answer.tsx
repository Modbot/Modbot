import { useMemo } from 'react'
import type { Pluggable } from 'unified'
import rehypeHighlight from 'rehype-highlight'
import bash from 'highlight.js/lib/languages/bash'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import diff from 'highlight.js/lib/languages/diff'
import ini from 'highlight.js/lib/languages/ini'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdown from 'highlight.js/lib/languages/markdown'
import python from 'highlight.js/lib/languages/python'
import sql from 'highlight.js/lib/languages/sql'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'
import { Markdown } from '@/components/Markdown'
import { subjectLinks } from '@/components/chat/subjectLinks'
import type { ChatReference } from '@/lib/api'
import { openSubject, type SubjectKind } from '@/lib/subject'

/**
 * Enough languages for the code an answer about a VRChat group might carry, and no more: every
 * one of these is bundled with the page.
 */
const LANGUAGES = {
  bash,
  csharp,
  css,
  diff,
  ini,
  javascript,
  json,
  markdown,
  python,
  sql,
  typescript,
  xml,
  yaml,
}

/**
 * One reply, as Markdown.
 *
 * Raw HTML from the model is never rendered (see `Markdown`). Everything drawn here is built from
 * the text: headings, lists, tables, code with a copy button, and the people, worlds and instances the
 * tools found, which become the same popups the rest of Modbot opens.
 */
export function Answer({ text, references }: { text: string; references: readonly ChatReference[] }) {
  const plugins = useMemo(
    () => [subjectLinks(references), [rehypeHighlight, { languages: LANGUAGES, detect: false }] as Pluggable],
    [references],
  )

  return (
    <Markdown
      text={text}
      rehypePlugins={plugins}
      components={{
        a: ({ href, children, ...rest }) => {
          const marked = rest as { 'data-subject-kind'?: string; 'data-subject-id'?: string }
          const kind = marked['data-subject-kind']
          const id = marked['data-subject-id']

          if (kind && id) {
            return (
              <button
                type="button"
                onClick={() => openSubject({ kind: kind as SubjectKind, id })}
                className="rounded-sm font-medium hover:underline focus-visible:outline-2 focus-visible:outline-ring"
              >
                {children}
              </button>
            )
          }

          return (
            <a href={href} target="_blank" rel="noopener noreferrer" className="underline underline-offset-2">
              {children}
              <span className="text-muted-foreground" aria-hidden="true">
                {' '}
                ↗
              </span>
            </a>
          )
        },
      }}
      className="text-(length:--text-base) leading-relaxed"
    />
  )
}
