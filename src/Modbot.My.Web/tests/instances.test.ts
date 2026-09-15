import assert from 'node:assert/strict'
import { test } from 'node:test'
import { normaliseInstanceUrl } from '../src/lib/instanceUrl.ts'
import { mergeInstances, type SavedInstance, type ServerInstance } from '../src/lib/merge.ts'

test('an instance URL keeps only its https origin', () => {
  assert.equal(normaliseInstanceUrl('https://modbot.example/settings?x=1'), 'https://modbot.example')
  assert.equal(normaliseInstanceUrl('  https://modbot.example:8443/  '), 'https://modbot.example:8443')
})

test('an instance URL that is not a plain https address is refused', () => {
  assert.equal(normaliseInstanceUrl('http://modbot.example'), null)
  assert.equal(normaliseInstanceUrl('https://user:password@modbot.example'), null)
  assert.equal(normaliseInstanceUrl('https://user@modbot.example'), null)
  assert.equal(normaliseInstanceUrl('modbot.example'), null)
  assert.equal(normaliseInstanceUrl('javascript:alert(1)'), null)
  assert.equal(normaliseInstanceUrl(''), null)
  assert.equal(normaliseInstanceUrl(null), null)
  assert.equal(normaliseInstanceUrl(`https://modbot.example/${'a'.repeat(2048)}`), null)
})

const saved = (url: string, lastUsedAt: string): SavedInstance => ({ url, name: null, addedAt: lastUsedAt, lastUsedAt })
const server = (instanceUrl: string, lastSeenAt: string): ServerInstance => ({
  instanceUrl,
  firstSeenAt: lastSeenAt,
  lastSeenAt,
  visits: 1,
})

test('the same URL from the browser and the server is one entry, with the later time', () => {
  const merged = mergeInstances(
    [saved('https://a.example', '2026-09-01T00:00:00Z')],
    [server('https://a.example', '2026-09-10T00:00:00Z')],
    [],
  )

  assert.deepEqual(merged, [{ url: 'https://a.example', name: null, lastUsedAt: '2026-09-10T00:00:00Z' }])
})

test('the list is most recently used first, whichever side it came from', () => {
  const merged = mergeInstances(
    [saved('https://old.example', '2026-09-01T00:00:00Z'), { url: 'https://legacy.example', name: 'Main', addedAt: '2026-08-01T00:00:00Z' }],
    [server('https://new.example', '2026-09-12T00:00:00Z')],
    [],
  )

  assert.deepEqual(merged.map((i) => i.url), ['https://new.example', 'https://old.example', 'https://legacy.example'])
  assert.equal(merged[2].name, 'Main')
})

test('an instance removed in this browser stays hidden when only the server still has it', () => {
  const merged = mergeInstances(
    [saved('https://kept.example', '2026-09-01T00:00:00Z')],
    [server('https://removed.example', '2026-09-12T00:00:00Z'), server('https://kept.example', '2026-09-02T00:00:00Z')],
    ['https://removed.example', 'https://kept.example'],
  )

  assert.deepEqual(merged.map((i) => i.url), ['https://kept.example'])
})

test('a server entry that is not an https origin is dropped', () => {
  assert.deepEqual(mergeInstances([], [server('http://plain.example', '2026-09-12T00:00:00Z')], []), [])
})
