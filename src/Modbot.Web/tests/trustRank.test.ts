import assert from 'node:assert/strict'
import { test } from 'node:test'
import { trustRank, trustRankColour, trustRankLabel, trustRankVariant } from '../src/lib/trustRank.ts'

// Nuisance is the one rank drawn loud; its VRChat colour is too dark to carry it alone.
test('Nuisance is drawn as a warning and every other rank as an outline', () => {
  assert.equal(trustRankVariant('Nuisance'), 'warn')
  for (const rank of ['Visitor', 'NewUser', 'User', 'KnownUser', 'TrustedUser', 'Legend', 'VRChatTeam'] as const) {
    assert.equal(trustRankVariant(rank), 'outline', rank)
  }
})

// The rank arrives as the server enum's name. Anything else -- a number, a rank added after this
// build, null for a person nobody has read -- is no rank, and the badge draws nothing.
test('a rank name from the server is recognised and anything else is not', () => {
  assert.equal(trustRank('KnownUser'), 'KnownUser')
  assert.equal(trustRank('VRChatTeam'), 'VRChatTeam')
  assert.equal(trustRank(null), null)
  assert.equal(trustRank(undefined), null)
  assert.equal(trustRank(3), null)
  assert.equal(trustRank('knownuser'), null)
  assert.equal(trustRank('Wizard'), null)
})

test('every rank has the words VRChat uses and the colour it paints them', () => {
  assert.equal(trustRankLabel('NewUser'), 'New User')
  assert.equal(trustRankLabel('TrustedUser'), 'Trusted User')
  assert.equal(trustRankLabel('VRChatTeam'), 'VRChat Team')
  assert.equal(trustRankColour('Visitor'), '#CCCCCC')
  assert.equal(trustRankColour('NewUser'), '#1778FF')
  assert.equal(trustRankColour('User'), '#2BCF5C')
  assert.equal(trustRankColour('KnownUser'), '#FF7B42')
  assert.equal(trustRankColour('TrustedUser'), '#8143E6')
  assert.equal(trustRankColour('Legend'), '#FFD000')
  assert.equal(trustRankColour('Nuisance'), '#782F2F')
  assert.equal(trustRankColour('VRChatTeam'), '#FF2626')
})
