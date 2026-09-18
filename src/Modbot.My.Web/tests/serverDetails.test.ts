import assert from 'node:assert/strict'
import { test } from 'node:test'
import { askServer, detailsFromLink, detailsFromServer } from '../src/lib/serverDetails.ts'

const link = (query: string) => detailsFromLink(new URLSearchParams(query))

test('a register link carries a group to show while the Modbot is asked', () => {
  const details = link(
    'url=https%3A%2F%2Fmodbot.example&groupId=grp_1&name=VRChat%20Kings' +
      '&icon=https%3A%2F%2Fapi.vrchat.cloud%2Ficon.png&banner=https%3A%2F%2Fapi.vrchat.cloud%2Fbanner.png',
  )

  assert.deepEqual(details, {
    groupId: 'grp_1',
    name: 'VRChat Kings',
    iconUrl: 'https://api.vrchat.cloud/icon.png',
    bannerUrl: 'https://api.vrchat.cloud/banner.png',
  })
})

test('a register link with no group at all is nothing to show', () => {
  assert.deepEqual(link('url=https%3A%2F%2Fmodbot.example'), {
    groupId: null,
    name: null,
    iconUrl: null,
    bannerUrl: null,
  })
})

test('a picture in a link is only taken when it is an https address', () => {
  assert.equal(link('icon=http%3A%2F%2Fmodbot.example%2Ficon.png').iconUrl, null)
  assert.equal(link('icon=javascript%3Aalert(1)').iconUrl, null)
  assert.equal(link('banner=not-a-url').bannerUrl, null)
})

test('a name in a link cannot hide what it really says', () => {
  // A right-to-left override makes one name render as another.
  assert.equal(link('name=%E2%80%AEsgnik%20tahcRV').name, 'sgnik tahcRV')
  assert.equal(link(`name=${'a'.repeat(400)}`).name?.length, 200)
  assert.equal(link('name=%20%20').name, null)
})

test('what a Modbot answers is read the same way, and its owner address is not read at all', () => {
  const details = detailsFromServer({
    name: 'VRChat Kings',
    groupId: 'grp_1',
    iconUrl: 'https://api.vrchat.cloud/icon.png',
    bannerUrl: 'https://api.vrchat.cloud/banner.png',
    ownerEmail: 'owner@example.com',
    version: '2026.9.0',
    publicAddress: 'https://modbot.example',
  })

  assert.deepEqual(details, {
    groupId: 'grp_1',
    name: 'VRChat Kings',
    iconUrl: 'https://api.vrchat.cloud/icon.png',
    bannerUrl: 'https://api.vrchat.cloud/banner.png',
  })
  assert.equal(Object.keys(details ?? {}).includes('ownerEmail'), false)
})

test('an answer that is not an answer is none', () => {
  assert.equal(detailsFromServer(null), null)
  assert.equal(detailsFromServer('a string'), null)
  assert.deepEqual(detailsFromServer({ name: 42, iconUrl: 7 }), {
    groupId: null,
    name: null,
    iconUrl: null,
    bannerUrl: null,
  })
})

/** Puts a `fetch` in place for one test and gives it back afterwards. */
function withFetch(answer: () => Promise<Response>): () => void {
  const real = globalThis.fetch
  globalThis.fetch = (() => answer()) as typeof globalThis.fetch
  return () => {
    globalThis.fetch = real
  }
}

test('the page asks the Modbot itself and takes what it says', async () => {
  const restore = withFetch(() =>
    Promise.resolve(
      new Response(JSON.stringify({ name: 'The real group', iconUrl: 'https://api.vrchat.cloud/real.png' }), {
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  )

  try {
    const details = await askServer('https://modbot.example')
    assert.equal(details?.name, 'The real group')
    assert.equal(details?.iconUrl, 'https://api.vrchat.cloud/real.png')
  } finally {
    restore()
  }
})

test('a Modbot that does not answer leaves the page with nothing of its own', async () => {
  const refused = withFetch(() => Promise.resolve(new Response('no', { status: 404 })))

  try {
    assert.equal(await askServer('https://modbot.example'), null)
  } finally {
    refused()
  }

  const broken = withFetch(() => Promise.reject(new Error('unreachable')))

  try {
    assert.equal(await askServer('https://modbot.example'), null)
  } finally {
    broken()
  }
})
