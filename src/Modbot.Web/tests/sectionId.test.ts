import assert from 'node:assert/strict'
import { test } from 'node:test'
import { sectionId } from '../src/pages/analytics/sectionId.ts'

test('a section id is its title in lower case, joined with dashes', () => {
  assert.equal(sectionId('VRChat instances'), 'section-vrchat-instances')
  assert.equal(sectionId('VRChat worlds'), 'section-vrchat-worlds')
  assert.equal(sectionId('Discord'), 'section-discord')
})

test('a run of other characters becomes one dash', () => {
  assert.equal(sectionId('When the community is active (your time)'), 'section-when-the-community-is-active-your-time-')
})
