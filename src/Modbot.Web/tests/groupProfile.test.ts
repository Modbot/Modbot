import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  ABOUT_FOLDED_KEY,
  LIMITS,
  descriptionProblem,
  draftFrom,
  draftProblem,
  isEmptyEdit,
  joinStateLabel,
  languageChoices,
  languagesProblem,
  linksProblem,
  nameProblem,
  profileChanges,
  readFolded,
  tidyLinks,
  webLink,
  writeFolded,
} from '../src/lib/groupProfile.ts'
import type { GroupInfo } from '../src/lib/api.ts'

const info: GroupInfo = {
  id: 'grp_1',
  name: 'Test Group',
  shortCode: 'TEST',
  discriminator: '1234',
  iconUrl: null,
  bannerUrl: null,
  description: 'A group',
  rules: 'Be kind',
  languages: ['eng'],
  links: ['https://example.com/'],
  joinState: 'open',
  members: 10,
  online: 1,
  countedAt: null,
  readAt: null,
  generatedAt: '2026-09-27T12:00:00Z',
}

test("VRChat's documented limits", () => {
  assert.deepEqual(LIMITS, { nameMin: 3, nameMax: 64, descriptionMax: 250, languages: 3, links: 3 })
})

test('a name is three to sixty-four characters, counted without the spaces around it', () => {
  assert.equal(nameProblem('abc'), null)
  assert.notEqual(nameProblem('  ab  '), null)
  assert.equal(nameProblem('n'.repeat(64)), null)
  assert.notEqual(nameProblem('n'.repeat(65)), null)
})

test('a description may be two hundred and fifty characters and no more', () => {
  assert.equal(descriptionProblem('d'.repeat(250)), null)
  assert.notEqual(descriptionProblem('d'.repeat(251)), null)
})

test('a link is a web address, written as the server stores it', () => {
  assert.equal(webLink(' https://discord.gg/example '), 'https://discord.gg/example')
  assert.equal(webLink('https://example.com'), 'https://example.com/')
  assert.equal(webLink('javascript:alert(1)'), null)
  assert.equal(webLink('discord.gg/example'), null)
})

test('links are cleaned of blanks and repeats, and at most three are allowed', () => {
  assert.deepEqual(tidyLinks(['https://a.example', '', 'https://a.example/', ' ']), ['https://a.example/'])
  assert.equal(linksProblem(['https://a.example', 'https://b.example', 'https://c.example']), null)
  assert.notEqual(linksProblem(['https://a.example', 'https://b.example', 'https://c.example', 'https://d.example']), null)
  assert.notEqual(linksProblem(['not a link']), null)
  assert.equal(linksProblem(['', ' ']), null)
})

test('at most three languages, and the picker offers only the ones not chosen', () => {
  assert.equal(languagesProblem(['eng', 'jpn', 'deu']), null)
  assert.notEqual(languagesProblem(['eng', 'jpn', 'deu', 'fra']), null)

  const offered = languageChoices(['eng']).map((l) => l.code)
  assert.ok(!offered.includes('eng'))
  assert.ok(offered.includes('jpn'))
  // Filipino has two codes; it is offered once.
  assert.equal(languageChoices([]).filter((l) => l.name === 'Filipino').length, 1)
})

test('an unchanged draft is no change, and nothing is sent', () => {
  const edit = profileChanges(info, draftFrom(info))
  assert.ok(isEmptyEdit(edit))
})

test('only the fields that differ are sent', () => {
  const edit = profileChanges(info, {
    ...draftFrom(info),
    description: '  New words ',
    links: ['https://example.com', 'https://discord.gg/example', ''],
  })

  assert.deepEqual(edit, { description: 'New words', links: ['https://example.com/', 'https://discord.gg/example'] })
})

test('clearing a field is a change', () => {
  assert.deepEqual(profileChanges(info, { ...draftFrom(info), rules: '', languages: [] }), { rules: '', languages: [] })
})

test('a draft is checked field by field before it can be sent', () => {
  assert.equal(draftProblem(draftFrom(info)), null)
  assert.notEqual(draftProblem({ ...draftFrom(info), name: 'x' }), null)
  assert.notEqual(draftProblem({ ...draftFrom(info), links: ['nope'] }), null)
})

test('who can join reads in words', () => {
  assert.equal(joinStateLabel('invite'), 'Invite only')
  assert.equal(joinStateLabel(null), '—')
})

function memoryStore() {
  const values = new Map<string, string>()
  return {
    values,
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => void values.set(key, value),
  }
}

test('the About card starts unfolded and remembers being folded', () => {
  const store = memoryStore()

  assert.equal(readFolded(store), false)
  writeFolded(store, true)
  assert.equal(store.values.get(ABOUT_FOLDED_KEY), '1')
  assert.equal(readFolded(store), true)
  writeFolded(store, false)
  assert.equal(readFolded(store), false)
})

test('a store that refuses, or none at all, reads as unfolded and never throws', () => {
  const refusing = {
    getItem: () => {
      throw new Error('denied')
    },
    setItem: () => {
      throw new Error('denied')
    },
  }

  assert.equal(readFolded(refusing), false)
  assert.doesNotThrow(() => writeFolded(refusing, true))
  assert.equal(readFolded(null), false)
  assert.doesNotThrow(() => writeFolded(undefined, true))
})
