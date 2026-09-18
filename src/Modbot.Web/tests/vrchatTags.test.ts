import assert from 'node:assert/strict'
import { test } from 'node:test'
import { languageName, moreTagsLabel, parseTags, platformOf } from '../src/lib/vrchatTags.ts'

// `last_platform` is free text on the wire. The three values VRChat actually sends get a word; anything
// else is shown as sent rather than treated as a mistake.
test('the platforms VRChat sends get a word and a picture', () => {
  assert.deepEqual(platformOf('standalonewindows'), { word: 'PC', icon: 'pc', known: true })
  assert.deepEqual(platformOf('android'), { word: 'Android', icon: 'phone', known: true })
  assert.deepEqual(platformOf('ios'), { word: 'iOS', icon: 'phone', known: true })
  assert.deepEqual(platformOf('StandaloneWindows'), { word: 'PC', icon: 'pc', known: true })
})

test('an unknown platform is shown as sent, and nothing is shown for nothing', () => {
  assert.deepEqual(platformOf('quest'), { word: 'quest', icon: 'other', known: false })
  assert.deepEqual(platformOf('  web '), { word: 'web', icon: 'other', known: false })
  assert.equal(platformOf(null), null)
  assert.equal(platformOf(undefined), null)
  assert.equal(platformOf(''), null)
  assert.equal(platformOf('   '), null)
})

test('the well-known tags become badges and everything else stays raw', () => {
  const parsed = parseTags([
    'system_supporter',
    'system_early_adopter',
    'language_eng',
    'language_jpn',
    'system_no_captcha',
    'system_world_access',
  ])

  assert.deepEqual(parsed.badges, [
    { kind: 'vrcplus' },
    { kind: 'early-adopter' },
    { kind: 'language', code: 'eng', name: 'English' },
    { kind: 'language', code: 'jpn', name: 'Japanese' },
  ])
  assert.deepEqual(parsed.rest, ['system_no_captcha', 'system_world_access'])
})

test('staff and nuisance come first, and a language without a name keeps its code', () => {
  const parsed = parseTags(['language_zzq', 'system_probable_troll', 'admin_moderator', 'system_troll'])

  assert.deepEqual(parsed.badges, [
    { kind: 'staff' },
    { kind: 'nuisance' },
    { kind: 'language', code: 'zzq', name: 'zzq' },
  ])
  assert.deepEqual(parsed.rest, [])
})

// The rank tags are the trust rank badge, computed by the server from these same tags, and the age
// tags are the 18+ card. Neither is repeated as a raw tag. The old trust subdivisions are not ranks
// (research §1) and are left raw so nobody wonders where they went.
test('tags shown elsewhere on the profile are dropped rather than repeated', () => {
  const parsed = parseTags(['system_trust_veteran', 'age_verified', 'system_age_verified', 'system_trust_intermediate'])

  assert.deepEqual(parsed.badges, [])
  assert.deepEqual(parsed.rest, ['system_trust_intermediate'])
})

test('blank, repeated and non-string tags are ignored', () => {
  const parsed = parseTags(['', '  ', 'system_supporter', 'system_supporter', 42 as unknown as string, ' language_kor '])

  assert.deepEqual(parsed.badges, [{ kind: 'vrcplus' }, { kind: 'language', code: 'kor', name: 'Korean' }])
  assert.deepEqual(parsed.rest, [])
  assert.deepEqual(parseTags(null), { badges: [], rest: [] })
  assert.deepEqual(parseTags(undefined), { badges: [], rest: [] })
})

test('a bare language_ prefix is a raw tag, not an empty language', () => {
  assert.deepEqual(parseTags(['language_']), { badges: [], rest: ['language_'] })
})

test('language names cover the sign languages VRChat lists and fall back to the code', () => {
  assert.equal(languageName('ase'), 'American Sign Language')
  assert.equal(languageName('ENG'), 'English')
  assert.equal(languageName('xyz'), 'xyz')
})

test('the hidden-tags control counts in plain words', () => {
  assert.equal(moreTagsLabel(1), '1 more tag')
  assert.equal(moreTagsLabel(12), '12 more tags')
})
