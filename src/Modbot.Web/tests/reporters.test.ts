import assert from 'node:assert/strict'
import { test } from 'node:test'
import { reporterNames } from '../src/lib/reporters.ts'
import type { AuditEntry, AuditReporter } from '../src/lib/api.ts'

/**
 * Whose clients reported a fact, as the row shows it.
 *
 * The order matters: the first name is the client whose report became the fact and the rest are
 * the ones folded into it, so re-ordering them would misstate who saw it first.
 */

function entry(reportedBy: AuditReporter[] | null): Pick<AuditEntry, 'reportedBy'> {
  return { reportedBy }
}

function reporter(name: string | null, at = '2026-09-19T01:12:02+00:00'): AuditReporter {
  return { accountId: '0192f0b4-0000-7000-8000-00000000000a', name, at }
}

test('a fact one client reported names that client', () => {
  assert.deepEqual(reporterNames(entry([reporter('Ada')])), ['Ada'])
})

test('a fact several clients reported names all of them, oldest report first', () => {
  const several = entry([
    reporter('Ada', '2026-09-19T01:12:02+00:00'),
    reporter('Ben', '2026-09-19T01:12:04+00:00'),
    reporter('Cid', '2026-09-19T01:12:09+00:00'),
  ])

  assert.deepEqual(reporterNames(several), ['Ada', 'Ben', 'Cid'])
})

test('a fact no client reported names nobody', () => {
  assert.deepEqual(reporterNames(entry(null)), [])
  assert.deepEqual(reporterNames(entry([])), [])
  assert.deepEqual(reporterNames({}), [])
})

test('a client whose account cannot be named is left out rather than shown as an id', () => {
  assert.deepEqual(reporterNames(entry([reporter('Ada'), reporter(null)])), ['Ada'])
})
