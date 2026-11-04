import assert from 'node:assert/strict'
import { test } from 'node:test'
import { dictationStart, dictationText } from '../src/components/chat/useDictation.ts'

const said = (...parts: string[]) => parts.map((transcript) => [{ transcript }])

test('what is said goes after the text already in the box', () => {
  assert.equal(dictationStart(''), '')
  assert.equal(dictationStart('Who is'), 'Who is ')
  assert.equal(dictationStart('Who is '), 'Who is ')
  assert.equal(dictationStart('Who is\n'), 'Who is\n')
})

test('finished and still-heard speech are joined after the start', () => {
  assert.equal(dictationText('', said('hello', ' world'), 100), 'hello world')
  assert.equal(dictationText('Who is ', said(' banned'), 100), 'Who is banned')
})

test('dictation never goes past the message limit', () => {
  assert.equal(dictationText('abc ', said('defgh'), 6), 'abc de')
})
