import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createElement, Fragment, type ReactNode } from 'react'
import { matchTyped, moveActive, readChoices, textOf, type Choice } from '../src/lib/dropdown.ts'

const option = (value: string | undefined, label: ReactNode, disabled?: boolean) =>
  createElement('option', { value, disabled }, label)

const list = (...labels: string[]): Choice[] =>
  labels.map((label) => ({ value: label.toLowerCase(), label, disabled: false, group: null }))

test('every option is read out of the children, whatever they are wrapped in', () => {
  const children = [
    option('', 'All features'),
    ['a', 'b'].map((id) => option(id, id.toUpperCase())),
    createElement(Fragment, null, option('last', 'Last')),
    null,
    false,
  ]

  assert.deepEqual(
    readChoices(children).map((c) => [c.value, c.label]),
    [
      ['', 'All features'],
      ['a', 'A'],
      ['b', 'B'],
      ['last', 'Last'],
    ],
  )
})

test('an option written in pieces reads as one label', () => {
  assert.equal(textOf(['Member joined', ' (', 'vrchat.group.member.join', ')']), 'Member joined (vrchat.group.member.join)')
  assert.deepEqual(readChoices(option('t', ['Name', ' (', 'id', ')'])).map((c) => c.label), ['Name (id)'])
})

test('an option with no value of its own is its own text, the same as the native control', () => {
  assert.deepEqual(readChoices(option(undefined, 'Europe')), [
    { value: 'Europe', label: 'Europe', disabled: false, group: null },
  ])
})

test('a heading is carried down onto the options under it', () => {
  const children = [
    option('everyone:', 'Everyone'),
    createElement('optgroup', { label: 'Features' }, [option('feature:chat', 'Chat'), option('feature:alerts', 'Alerts')]),
    createElement('optgroup', { label: 'Chat · roles' }, option('role:1', 'Moderators')),
  ]

  assert.deepEqual(
    readChoices(children).map((c) => [c.group, c.value]),
    [
      [null, 'everyone:'],
      ['Features', 'feature:chat'],
      ['Features', 'feature:alerts'],
      ['Chat · roles', 'role:1'],
    ],
  )
})

test('arrows step one at a time and stop at the ends rather than wrapping', () => {
  const choices = list('Apple', 'Banana', 'Cherry')

  assert.equal(moveActive(choices, 0, 'down'), 1)
  assert.equal(moveActive(choices, 2, 'down'), 2)
  assert.equal(moveActive(choices, 1, 'up'), 0)
  assert.equal(moveActive(choices, 0, 'up'), 0)
  assert.equal(moveActive(choices, 1, 'first'), 0)
  assert.equal(moveActive(choices, 1, 'last'), 2)
})

test('with nothing chosen yet, down starts at the top and up starts at the bottom', () => {
  const choices = list('Apple', 'Banana', 'Cherry')

  assert.equal(moveActive(choices, -1, 'down'), 0)
  assert.equal(moveActive(choices, -1, 'up'), 2)
  assert.equal(moveActive([], -1, 'down'), -1)
})

test('an option nobody may pick is stepped over, not landed on', () => {
  const choices = list('Apple', 'Banana', 'Cherry', 'Damson')
  choices[1].disabled = true
  choices[2].disabled = true

  assert.equal(moveActive(choices, 0, 'down'), 3)
  assert.equal(moveActive(choices, 3, 'up'), 0)

  choices[0].disabled = true
  assert.equal(moveActive(choices, -1, 'first'), 3)
})

test('typing a letter walks through the options that start with it', () => {
  const choices = list('Alerts', 'Apple', 'Banana', 'Avocado')

  assert.equal(matchTyped(choices, 'a', -1), 0)
  assert.equal(matchTyped(choices, 'aa', 0), 1)
  assert.equal(matchTyped(choices, 'aaa', 1), 3)
  // Past the last one it comes back round, so a letter always finds its options.
  assert.equal(matchTyped(choices, 'aaaa', 3), 0)
})

test('typing more letters narrows rather than walking', () => {
  const choices = list('Alerts', 'Apple', 'Avocado')

  assert.equal(matchTyped(choices, 'av', 0), 2)
  assert.equal(matchTyped(choices, 'avo', 2), 2)
  assert.equal(matchTyped(choices, 'al', 2), 0)
})

test('typing what nothing starts with leaves the cursor where it was', () => {
  const choices = list('Alerts', 'Apple')

  assert.equal(matchTyped(choices, 'z', 1), 1)
  assert.equal(matchTyped(choices, '', 1), 1)
  assert.equal(matchTyped([], 'a', -1), -1)
})

test('typing ignores case and skips an option nobody may pick', () => {
  const choices = list('Alerts', 'Apple')
  choices[0].disabled = true

  assert.equal(matchTyped(choices, 'A', -1), 1)
})
