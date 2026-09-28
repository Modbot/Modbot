import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { SHEET } from '../src/components/calendar/phone.ts'

/**
 * A dialog becomes a sheet along the bottom of a phone through the `sheet` variant in index.css.
 * The calendar asks the same question from script, to open an event as that dialog rather than as a
 * popover beside the tap. If the two disagreed, an event would open as a centered dialog on the
 * screens between them, where the popover did not fit either.
 */

const css = readFileSync(new URL('../src/index.css', import.meta.url), 'utf8')

test('the calendar and the dialogs agree on which screens get a sheet from the bottom', () => {
  const found = /@custom-variant sheet \{\s*@media ([^{]+)\{/.exec(css)
  assert.ok(found, 'index.css has the sheet variant')
  assert.equal(found[1].trim(), SHEET)
})
