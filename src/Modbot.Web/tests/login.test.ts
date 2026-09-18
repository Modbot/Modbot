import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { api } from '../src/lib/api.ts'

/**
 * What the sign-in form puts on the wire.
 *
 * The choice the person makes with "Keep me signed in" has to reach the server on the same request
 * as the password, because that is the only request that starts a session. A yes-or-no and nothing
 * else: how long a kept session lasts is the server's to decide.
 */

const realFetch = globalThis.fetch

afterEach(() => {
  globalThis.fetch = realFetch
})

/** Captures the one request `body` builder sends and answers it with an empty session user. */
async function sent(send: () => Promise<unknown>): Promise<{ path: string; body: unknown }> {
  let seen: { path: string; body: unknown } | null = null

  globalThis.fetch = ((path: string, init?: RequestInit) => {
    seen = { path, body: JSON.parse(String(init?.body)) }
    return Promise.resolve(new Response('{}', { status: 200 }))
  }) as typeof fetch

  await send()

  assert.notEqual(seen, null)
  return seen!
}

test('ticked, the choice goes with the password', async () => {
  const request = await sent(() =>
    api.login({ username: 'gunner24', password: 'hunter2hunter2', keepSignedIn: true }),
  )

  assert.equal(request.path, '/api/auth/login')
  assert.deepEqual(request.body, {
    username: 'gunner24',
    password: 'hunter2hunter2',
    keepSignedIn: true,
  })
})

test('unticked, it is sent as false rather than left out', async () => {
  const request = await sent(() =>
    api.login({ username: 'gunner24', password: 'hunter2hunter2', keepSignedIn: false }),
  )

  // Left out it would mean the same thing, but sending it makes the two sign-ins one shape, and a
  // field that is only sometimes there is a field somebody eventually forgets to send.
  assert.deepEqual(request.body, {
    username: 'gunner24',
    password: 'hunter2hunter2',
    keepSignedIn: false,
  })
})
