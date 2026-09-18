import assert from 'node:assert/strict'
import { test } from 'node:test'
import { registerLink, setMyModbotOrigin, setServerGroup } from '../src/lib/myModbot.ts'

/**
 * The register link my.modbot.co is opened with (server info and account email design §3).
 *
 * `registerLink()` with no argument reads `window.location.origin`, which does not exist here, so
 * every test passes the address it means.
 */

function parameters(url: string): URLSearchParams {
  return new URL(url).searchParams
}

test('the address alone, before the server knows its group', () => {
  setMyModbotOrigin('https://my.example')
  setServerGroup(null)

  const link = registerLink('https://modbot.example')

  assert.equal(parameters(link).get('url'), 'https://modbot.example')
  assert.equal(parameters(link).get('groupId'), null)
  assert.equal(parameters(link).get('name'), null)
  assert.equal(parameters(link).get('icon'), null)
  assert.equal(parameters(link).get('banner'), null)
})

test('the group travels with it, each value encoded', () => {
  setMyModbotOrigin('https://my.example')
  setServerGroup({
    id: 'grp_1234',
    name: 'Sunset & Friends',
    iconUrl: 'https://files.example/icon.png?v=2',
    bannerUrl: 'https://files.example/banner.png',
  })

  const link = registerLink('https://modbot.example')
  const read = parameters(link)

  assert.equal(read.get('groupId'), 'grp_1234')
  assert.equal(read.get('name'), 'Sunset & Friends')
  assert.equal(read.get('icon'), 'https://files.example/icon.png?v=2')
  assert.equal(read.get('banner'), 'https://files.example/banner.png')

  // Encoded rather than pasted in: an ampersand in a group's name would otherwise end the
  // parameter and start one nobody meant.
  assert.ok(link.includes('name=Sunset%20%26%20Friends') || link.includes('name=Sunset+%26+Friends'))
})

test('a group with no pictures leaves those out rather than sending empty ones', () => {
  setMyModbotOrigin('https://my.example')
  setServerGroup({ id: 'grp_1234', name: 'Sunset', iconUrl: null, bannerUrl: null })

  const read = parameters(registerLink('https://modbot.example'))

  assert.equal(read.get('groupId'), 'grp_1234')
  assert.equal(read.get('name'), 'Sunset')
  assert.equal(read.get('icon'), null)
  assert.equal(read.get('banner'), null)
})

test('the selector a group runs itself is where the link points', () => {
  setMyModbotOrigin('https://selector.example/')
  setServerGroup(null)

  assert.ok(registerLink('https://modbot.example').startsWith('https://selector.example/register?'))
})
