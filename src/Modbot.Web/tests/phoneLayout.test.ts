import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { PHONE_LAYOUT } from '../src/lib/phoneLayout.ts'

/**
 * The person popup's classes use the `big` variant in index.css, and its script asks
 * `PHONE_LAYOUT`. If the two ever disagreed, the popup would draw the desk's columns with the
 * phone's pieces in them, or the other way round. So every screen below must be exactly one of
 * the two.
 */

type Screen = { width: number; height: number; coarse: boolean }

const css = readFileSync(new URL('../src/index.css', import.meta.url), 'utf8')

function bigQuery(): string {
  const found = /@custom-variant big \{\s*@media ([^{]+)\{/.exec(css)
  assert.ok(found, 'index.css has the big variant')
  return found[1].trim()
}

/** Enough of a media query to read the two above: `,` is or, `and` is and, `not (…)` and three features. */
function matches(query: string, screen: Screen): boolean {
  return query.split(',').some((part) => all(part.trim(), screen))
}

function all(part: string, screen: Screen): boolean {
  return splitAnd(part).every((term) => one(term, screen))
}

function splitAnd(part: string): string[] {
  const terms: string[] = []
  let depth = 0
  let start = 0
  for (let i = 0; i < part.length; i++) {
    if (part[i] === '(') depth++
    else if (part[i] === ')') depth--
    else if (depth === 0 && part.startsWith(' and ', i)) {
      terms.push(part.slice(start, i).trim())
      start = i + 5
    }
  }
  terms.push(part.slice(start).trim())
  return terms
}

function one(term: string, screen: Screen): boolean {
  const inner = term.replace(/^\((.*)\)$/, '$1').trim()

  if (inner.startsWith('not ')) return !one(inner.slice(4).trim(), screen)
  if (inner === 'pointer: coarse') return screen.coarse

  const range = /^(width|height) (<|<=|>|>=) ([\d.]+)rem$/.exec(inner)
  assert.ok(range, `a feature this test can read: ${term}`)
  const value = screen[range[1] as 'width' | 'height']
  const limit = Number(range[3]) * 16
  switch (range[2]) {
    case '<': return value < limit
    case '<=': return value <= limit
    case '>': return value > limit
    default: return value >= limit
  }
}

const screens: (Screen & { name: string; phone: boolean })[] = [
  { name: 'phone, upright', width: 390, height: 844, coarse: true, phone: true },
  { name: 'small phone, upright', width: 360, height: 800, coarse: true, phone: true },
  { name: 'phone, on its side', width: 844, height: 390, coarse: true, phone: true },
  { name: 'large phone, on its side', width: 932, height: 430, coarse: true, phone: true },
  { name: 'tablet, on its side', width: 1024, height: 768, coarse: true, phone: false },
  { name: 'tablet, upright', width: 768, height: 1024, coarse: true, phone: false },
  { name: 'narrow desk window', width: 700, height: 900, coarse: false, phone: true },
  { name: 'short desk window', width: 1280, height: 400, coarse: false, phone: false },
  { name: 'desk', width: 1440, height: 900, coarse: false, phone: false },
]

for (const screen of screens) {
  test(`${screen.name} gets the ${screen.phone ? 'phone' : 'desk'} layout from both the styles and the script`, () => {
    assert.equal(matches(PHONE_LAYOUT, screen), screen.phone)
    assert.equal(matches(bigQuery(), screen), !screen.phone)
  })
}
