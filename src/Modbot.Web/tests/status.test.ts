import assert from 'node:assert/strict'
import { test } from 'node:test'
import { statusRows } from '../src/lib/status.ts'
import type { SyncHealth } from '../src/lib/api.ts'

/** Only the fields the rows read. The rest of the answer is not their business. */
function health(over: Partial<SyncHealth> = {}): SyncHealth {
  return {
    discordBot: {
      state: 'Connected',
      connectedSince: null,
      lastError: null,
      lastErrorAt: null,
      commandsRegistered: 0,
      logChannelConfigured: true,
      lastPostedAt: null,
      postedInThisProcess: 0,
      missingIntents: null,
    },
    syncRunningInThisProcess: true,
    ...over,
  } as SyncHealth
}

function labels(rows: { id: string; state: string }[]) {
  return Object.fromEntries(rows.map((r) => [r.id, r.state]))
}

test('nothing has answered yet: every row says unknown, never healthy', () => {
  const rows = statusRows({ gate: null, health: null, databaseReachable: null })

  assert.deepEqual(labels(rows), {
    vrchat: 'unknown',
    discord: 'unknown',
    database: 'unknown',
    sync: 'unknown',
  })
  assert.ok(rows.every((r) => r.tone === 'muted'))
})

test('everything working', () => {
  const rows = statusRows({ gate: 'Working', health: health(), databaseReachable: true })

  assert.deepEqual(labels(rows), {
    vrchat: 'working',
    discord: 'working',
    database: 'online',
    sync: 'running',
  })
  assert.ok(rows.every((r) => r.tone === 'ok'))
})

test('waiting out a rate limit is amber, not red', () => {
  const [vrchat] = statusRows({ gate: 'WaitingOnPurpose', health: null, databaseReachable: null })

  assert.equal(vrchat.state, 'waiting on purpose')
  assert.equal(vrchat.tone, 'warn')
})

test('a gate status this build does not know reads as unknown rather than throwing', () => {
  const [vrchat] = statusRows({ gate: 'SomethingNewer', health: null, databaseReachable: null })

  assert.equal(vrchat.state, 'unknown (somethingnewer)')
  assert.equal(vrchat.tone, 'muted')
})

test('a database Modbot cannot reach is unreachable and red', () => {
  const rows = statusRows({ gate: 'Working', health: health(), databaseReachable: false })
  const database = rows.find((r) => r.id === 'database')

  assert.equal(database?.state, 'unreachable')
  assert.equal(database?.tone, 'bad')
})

test('no Discord bot registered in this host is not set up, which is not a fault', () => {
  const rows = statusRows({ gate: 'Working', health: health({ discordBot: null }), databaseReachable: true })
  const discord = rows.find((r) => r.id === 'discord')

  assert.equal(discord?.state, 'not set up')
  assert.equal(discord?.tone, 'muted')
})

test('a bot Discord refused needs somebody', () => {
  const rows = statusRows({
    gate: 'Working',
    health: health({ discordBot: { ...health().discordBot!, state: 'Failed' } }),
    databaseReachable: true,
  })

  assert.equal(rows.find((r) => r.id === 'discord')?.state, 'stopped, needs you')
  assert.equal(rows.find((r) => r.id === 'discord')?.tone, 'bad')
})

test('sync not running in this process is amber', () => {
  const rows = statusRows({
    gate: 'Working',
    health: health({ syncRunningInThisProcess: false }),
    databaseReachable: true,
  })

  assert.equal(rows.find((r) => r.id === 'sync')?.state, 'stopped')
  assert.equal(rows.find((r) => r.id === 'sync')?.tone, 'warn')
})

test('no AI row on a deployment that does not use AI', () => {
  const rows = statusRows({ gate: 'Working', health: health(), databaseReachable: true })

  assert.equal(rows.find((r) => r.id === 'ai'), undefined)
})

test('AI appears once there are calls, and says what is wrong with them', () => {
  const calls = { calls: 10, errors: 0, timedOut: 0, fallbacks: 0, answeringModel: null }

  const working = statusRows({ gate: 'Working', health: health({ aiCalls: calls }), databaseReachable: true })
  assert.equal(working.find((r) => r.id === 'ai')?.state, 'working')

  const fallback = statusRows({
    gate: 'Working',
    health: health({ aiCalls: { ...calls, fallbacks: 3 } }),
    databaseReachable: true,
  })
  assert.equal(fallback.find((r) => r.id === 'ai')?.state, 'on the fallback')
  assert.equal(fallback.find((r) => r.id === 'ai')?.tone, 'warn')

  const failing = statusRows({
    gate: 'Working',
    health: health({ aiCalls: { ...calls, errors: 2 } }),
    databaseReachable: true,
  })
  assert.equal(failing.find((r) => r.id === 'ai')?.state, 'calls failing')
  assert.equal(failing.find((r) => r.id === 'ai')?.tone, 'bad')
})

test('a spend limit that has been reached beats everything else AI could say', () => {
  const warning = {
    appliesTo: 'everyone',
    feature: null,
    label: null,
    period: 'month',
    unit: 'money',
    limit: 10,
    spent: 10,
    estimate: null,
    reached: true,
    partUnknown: false,
  }

  const rows = statusRows({
    gate: 'Working',
    health: health({ aiSpend: [warning] as SyncHealth['aiSpend'] }),
    databaseReachable: true,
  })

  assert.equal(rows.find((r) => r.id === 'ai')?.state, 'limit reached')
  assert.equal(rows.find((r) => r.id === 'ai')?.tone, 'bad')
})
