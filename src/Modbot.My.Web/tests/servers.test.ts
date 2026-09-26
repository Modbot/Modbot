import assert from 'node:assert/strict'
import { test } from 'node:test'
import { mergeServers, type SavedServer, type SeenServer } from '../src/lib/merge.ts'
import { normaliseServerUrl } from '../src/lib/serverUrl.ts'

test('a server URL keeps only its https origin', () => {
  assert.equal(normaliseServerUrl('https://modbot.example/settings?x=1'), 'https://modbot.example')
  assert.equal(normaliseServerUrl('  https://modbot.example:8443/  '), 'https://modbot.example:8443')
})

test('a server URL that is not a plain https address is refused', () => {
  assert.equal(normaliseServerUrl('http://modbot.example'), null)
  assert.equal(normaliseServerUrl('http://localhost:5000'), null)
  assert.equal(normaliseServerUrl('https://user:password@modbot.example'), null)
  assert.equal(normaliseServerUrl('https://user@modbot.example'), null)
  assert.equal(normaliseServerUrl('modbot.example'), null)
  assert.equal(normaliseServerUrl('javascript:alert(1)'), null)
  assert.equal(normaliseServerUrl(''), null)
  assert.equal(normaliseServerUrl(null), null)
  assert.equal(normaliseServerUrl(`https://modbot.example/${'a'.repeat(2048)}`), null)
})

const saved = (url: string, lastUsedAt: string): SavedServer => ({ url, name: null, addedAt: lastUsedAt, lastUsedAt })
const seen = (serverUrl: string, lastSeenAt: string): SeenServer => ({
  serverUrl,
  firstSeenAt: lastSeenAt,
  lastSeenAt,
  visits: 1,
})

test('the same URL saved in the browser and seen by Cloud is one entry, with the later time', () => {
  const merged = mergeServers(
    [saved('https://a.example', '2026-09-01T00:00:00Z')],
    [seen('https://a.example', '2026-09-10T00:00:00Z')],
    [],
  )

  assert.deepEqual(merged, [{ url: 'https://a.example', name: null, lastUsedAt: '2026-09-10T00:00:00Z' }])
})

test('the list is most recently used first, whichever side it came from', () => {
  const merged = mergeServers(
    [saved('https://old.example', '2026-09-01T00:00:00Z'), { url: 'https://legacy.example', name: 'Main', addedAt: '2026-08-01T00:00:00Z' }],
    [seen('https://new.example', '2026-09-12T00:00:00Z')],
    [],
  )

  assert.deepEqual(merged.map((s) => s.url), ['https://new.example', 'https://old.example', 'https://legacy.example'])
  assert.equal(merged[2].name, 'Main')
})

test('a server removed in this browser stays hidden when only Cloud still has it', () => {
  const merged = mergeServers(
    [saved('https://kept.example', '2026-09-01T00:00:00Z')],
    [seen('https://removed.example', '2026-09-12T00:00:00Z'), seen('https://kept.example', '2026-09-02T00:00:00Z')],
    ['https://removed.example', 'https://kept.example'],
  )

  assert.deepEqual(merged.map((s) => s.url), ['https://kept.example'])
})

test('a seen entry that is not an https origin is dropped', () => {
  assert.deepEqual(mergeServers([], [seen('http://plain.example', '2026-09-12T00:00:00Z')], []), [])
})
