import assert from 'node:assert/strict'
import { test } from 'node:test'
import { audience, draftOf, emptyDraft, postBody, postPages, postProblem } from '../src/lib/groupPosts.ts'
import type { GroupPostRow } from '../src/lib/api.ts'

const roles = [
  { id: 'grol_mod', name: 'Moderator' },
  { id: 'grol_vip', name: 'VIP' },
]

const post: GroupPostRow = {
  id: 'not_1',
  title: 'Hello',
  text: 'Words',
  authorId: 'usr_1',
  authorName: 'Nova',
  imageUrl: 'https://api.vrchat.cloud/api/1/file/file_1/1',
  imageId: 'file_1',
  visibility: 'group',
  roleIds: ['grol_vip'],
  createdAt: '2026-09-26T18:00:00Z',
  updatedAt: '2026-09-26T18:00:00Z',
}

test('who a post is for, in words', () => {
  assert.equal(audience({ visibility: 'public', roleIds: [] }, roles), 'Everyone')
  assert.equal(audience({ visibility: 'group', roleIds: [] }, roles), 'Members only')
  assert.equal(audience({ visibility: 'group', roleIds: ['grol_mod', 'grol_vip'] }, roles), 'Moderator, VIP')
})

test('a role Modbot has no name for is shown by its id, not dropped', () => {
  assert.equal(audience({ visibility: 'group', roleIds: ['grol_gone'] }, roles), 'grol_gone')
})

test('a post needs a title and some text', () => {
  assert.notEqual(postProblem(emptyDraft()), null)
  assert.notEqual(postProblem({ ...emptyDraft(), title: 'Hi', text: '   ' }), null)
  assert.equal(postProblem({ ...emptyDraft(), title: 'Hi', text: 'There' }), null)
})

test('a new post is for members by default and may notify them', () => {
  const body = postBody({ ...emptyDraft(), title: ' Hi ', text: 'There', notify: true }, null)

  assert.deepEqual(body, {
    id: null,
    title: 'Hi',
    text: 'There',
    visibility: 'group',
    roleIds: [],
    notify: true,
    imageId: null,
  })
})

test('an edit carries the post and its picture, and never notifies again', () => {
  const body = postBody({ ...draftOf(post), notify: true, title: 'Hello again' }, post)

  assert.equal(body.id, 'not_1')
  assert.equal(body.imageId, 'file_1')
  assert.equal(body.notify, false)
  assert.deepEqual(body.roleIds, ['grol_vip'])
})

test('a post for everyone sends no roles', () => {
  const body = postBody({ ...draftOf(post), visibility: 'public' }, post)
  assert.deepEqual(body.roleIds, [])
})

test('the list has at least one page, and as many as the total needs', () => {
  assert.equal(postPages(0, 20), 1)
  assert.equal(postPages(20, 20), 1)
  assert.equal(postPages(21, 20), 2)
  assert.equal(postPages(5, 0), 1)
})
