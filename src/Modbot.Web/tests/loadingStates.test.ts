import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { test } from 'node:test'

/**
 * Loading and failed states are drawn by one part, `EmptyRow`, and the page messages built on it
 * (console look §10). A screen that writes "Loading…" into one of them as words, or keeps a copy of
 * them, is back to the state where loading and empty looked the same; a failed row with no way
 * out is back to a dead screen on a flaky connection. These read the source, since the rule is
 * about how screens are written.
 */
const src = path.resolve(import.meta.dirname, '..', 'src')

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name)
    if (entry.isDirectory()) return sources(full)
    return entry.name.endsWith('.tsx') ? [full] : []
  })
}

const files = sources(src).map((file) => ({ file: path.relative(src, file), text: readFileSync(file, 'utf8') }))

const ROW_PARTS = 'Empty|EmptyRow|Placeholder|PageMessage|Nothing'

test('no state row says "Loading…" in words: it is tone="loading"', () => {
  const words = new RegExp(`<(${ROW_PARTS})\\b[^>]*>\\s*(\\{[^}]*)?'?Loading…`)
  const found = files.filter(({ text }) => words.test(text)).map(({ file }) => file)
  assert.deepEqual(found, [])
})

test('no state row picks between a failure and "Loading…" as its words', () => {
  const found = files.filter(({ text }) => /\?\? 'Loading…'/.test(text)).map(({ file }) => file)
  assert.deepEqual(found, [])
})

test('the analytics page message and the settings placeholder are the list pages\' Empty', () => {
  const analytics = files.find(({ file }) => file === path.join('pages', 'analytics', 'shared.tsx'))!.text
  const settings = files.find(({ file }) => file === path.join('components', 'settings', 'fields.tsx'))!.text

  assert.match(analytics, /export const PageMessage = Empty\b/)
  assert.match(settings, /<Empty className="col-span-12" \{\.\.\.row\} \/>/)
})

test('a failed row has "Try again", and only a row that states a fact leaves it out', () => {
  const panelGrid = files.find(({ file }) => file === path.join('components', 'PanelGrid.tsx'))!.text
  assert.match(panelGrid, /Try again/)

  const optedOut = files
    .filter(({ text }) => /onTryAgain=\{null\}/.test(text))
    .map(({ file }) => file)
    .sort()
  assert.deepEqual(optedOut, [path.join('pages', 'CaseFile.tsx')])
})
