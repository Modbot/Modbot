import type { Element, Parent, Root, RootContent } from 'hast'
import type { ChatReference } from '@/lib/api'

/** Past this many, matching every name in every answer costs more than it is worth. */
const MOST_REFERENCES = 200

/** Shorter than this, a name matches too much of the text to be worth linking. */
const SHORTEST_NAME = 3

/** Tags whose text is left alone: code is not prose, and a link is already a link. */
const LEFT_ALONE = new Set(['a', 'code', 'pre', 'script', 'style'])

/**
 * Turns the people, worlds and rooms a tool found into links, wherever the answer names them.
 *
 * The model is asked to write ids out, and it mostly does — but it also writes display names, and
 * a moderator reading "TeaSpoon was banned in March" wants to open TeaSpoon from that sentence
 * rather than hunt for the chip above it. Only what a tool actually returned is matched, so a name
 * the model invented links to nothing.
 */
export function subjectLinks(references: readonly ChatReference[]) {
  const terms = termsOf(references)

  // A Markdown plugin: called once for the pipeline, and gives back the pass over the text.
  return () => (tree: Root) => {
    if (terms.size === 0) return
    walk(tree, terms, new RegExp([...terms.keys()].map(escape).join('|'), 'g'))
  }
}

type Term = { kind: ChatReference['kind']; id: string }

function termsOf(references: readonly ChatReference[]): Map<string, Term> {
  const terms = new Map<string, Term>()

  for (const reference of references.slice(0, MOST_REFERENCES)) {
    if (reference.id) terms.set(reference.id, { kind: reference.kind, id: reference.id })

    const label = reference.label?.trim()
    if (label && label.length >= SHORTEST_NAME && !terms.has(label))
      terms.set(label, { kind: reference.kind, id: reference.id })
  }

  // Longest first, so a name that contains another one wins.
  return new Map([...terms].sort((a, b) => b[0].length - a[0].length))
}

function walk(node: Parent, terms: Map<string, Term>, pattern: RegExp) {
  const children: RootContent[] = []
  let changed = false

  for (const child of node.children) {
    if (child.type === 'element' && LEFT_ALONE.has(child.tagName)) {
      children.push(child)
      continue
    }

    if (child.type === 'element') {
      walk(child, terms, pattern)
      children.push(child)
      continue
    }

    if (child.type !== 'text') {
      children.push(child)
      continue
    }

    const split = splitText(child.value, terms, pattern)
    if (split === null) {
      children.push(child)
      continue
    }

    changed = true
    children.push(...split)
  }

  if (changed) node.children = children
}

function splitText(text: string, terms: Map<string, Term>, pattern: RegExp): RootContent[] | null {
  pattern.lastIndex = 0

  const parts: RootContent[] = []
  let at = 0
  let match: RegExpExecArray | null

  while ((match = pattern.exec(text)) !== null) {
    const term = terms.get(match[0])
    if (!term || !whole(text, match.index, match[0])) continue

    if (match.index > at) parts.push({ type: 'text', value: text.slice(at, match.index) })
    parts.push(link(match[0], term))
    at = match.index + match[0].length
  }

  if (parts.length === 0) return null
  if (at < text.length) parts.push({ type: 'text', value: text.slice(at) })

  return parts
}

/** A name only counts when it is not part of a longer word. */
function whole(text: string, at: number, match: string): boolean {
  const before = text[at - 1]
  const after = text[at + match.length]
  const word = /[\p{L}\p{N}_]/u

  return !(before && word.test(before) && word.test(match[0])) && !(after && word.test(after) && word.test(match[match.length - 1]))
}

/**
 * An anchor with no address: the Chat page draws it as a button that opens the popup. Nothing
 * here can become a real link, so nothing the model writes can send a moderator anywhere.
 */
function link(text: string, term: Term): Element {
  return {
    type: 'element',
    tagName: 'a',
    properties: { dataSubjectKind: term.kind, dataSubjectId: term.id },
    children: [{ type: 'text', value: text }],
  }
}

function escape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}
