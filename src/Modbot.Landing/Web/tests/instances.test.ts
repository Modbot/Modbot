import assert from 'node:assert/strict'
import { test } from 'node:test'
import { type Group, openFor, order, regionName } from '../src/lib/instances.ts'

const group = (groupId: string, groupName: string | null, instances: number): Group => ({
  groupId,
  groupName,
  groupUrl: `https://vrchat.com/home/group/${groupId}`,
  reportedAt: '2026-09-16T12:00:00Z',
  instances: Array.from({ length: instances }, (_, i) => ({
    location: `wrld_1:${i}`,
    worldId: 'wrld_1',
    openedAt: '2026-09-16T11:00:00Z',
  })),
})

test('a region VRChat names is shown in words, and one it does not is shown as it arrived', () => {
  assert.equal(regionName('us'), 'US West')
  assert.equal(regionName('EU'), 'Europe')
  assert.equal(regionName('jp'), 'Japan')
  assert.equal(regionName('mars'), 'MARS')
  assert.equal(regionName(null), null)
  assert.equal(regionName(''), null)
})

test('how long an instance has been open is the largest whole unit', () => {
  const opened = '2026-09-16T12:00:00Z'
  const at = (minutes: number) => Date.parse(opened) + minutes * 60_000

  assert.equal(openFor(opened, at(0)), 'Just now')
  assert.equal(openFor(opened, at(14)), '14 min')
  assert.equal(openFor(opened, at(59)), '59 min')
  assert.equal(openFor(opened, at(60)), '1 hr')
  assert.equal(openFor(opened, at(60 * 25)), '1 d')
})

test('a time that cannot be read shows nothing rather than NaN', () => {
  assert.equal(openFor('not a time', Date.now()), '')
})

test('groups with instances come first, then by name, and a group with none is still listed', () => {
  const groups = [group('grp_c', 'Quiet Ones', 0), group('grp_b', 'Zebra', 1), group('grp_a', 'Aardvark', 2)]

  assert.deepEqual(
    order(groups).map((g) => g.groupName),
    ['Aardvark', 'Zebra', 'Quiet Ones'],
  )
})

test('a group with no name is ordered by its id', () => {
  const groups = [group('grp_b', null, 1), group('grp_a', null, 1)]

  assert.deepEqual(
    order(groups).map((g) => g.groupId),
    ['grp_a', 'grp_b'],
  )
})
