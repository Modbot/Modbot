import assert from 'node:assert/strict'
import { test } from 'node:test'
import { backLabel, closeOne, open, top, type Stack } from '../src/mock/stack.ts'

test('opening from inside a popup stacks, and Back names the one underneath', () => {
  let stack: Stack = []
  stack = open(stack, { kind: 'person', id: 'usr_kiri' })
  assert.equal(backLabel(stack), null)

  stack = open(stack, { kind: 'world', id: 'wrld_harbor' })
  assert.equal(backLabel(stack), 'Back to the person')

  stack = open(stack, { kind: 'instance', id: 'instance_1' })
  assert.equal(backLabel(stack), 'Back to the world')
  assert.deepEqual(top(stack), { kind: 'instance', id: 'instance_1' })
})

test('closing takes one level off, never the whole stack', () => {
  const stack = open(open([], { kind: 'person', id: 'a' }), { kind: 'world', id: 'b' })

  assert.deepEqual(closeOne(stack), [{ kind: 'person', id: 'a' }])
  assert.deepEqual(closeOne(closeOne(stack)), [])
  assert.deepEqual(closeOne([]), [])
})

test('the same world opened twice is two popups', () => {
  const world = { kind: 'world', id: 'wrld_harbor' } as const
  assert.equal(open(open([], world), world).length, 2)
})
