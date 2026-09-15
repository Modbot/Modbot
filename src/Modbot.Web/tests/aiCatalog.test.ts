import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  chipsOf,
  contextText,
  filterModels,
  makersOf,
  noFilters,
  priceText,
  sortModels,
} from '../src/lib/aiCatalog.ts'
import type { AiCatalogModel } from '../src/lib/api.ts'

const model = (id: string, extra: Partial<AiCatalogModel> = {}): AiCatalogModel => ({
  id,
  name: id,
  maker: id.split('/')[0] ?? '',
  contextLength: 200000,
  maxOutputTokens: 8000,
  inputModalities: ['text'],
  outputModalities: ['text'],
  addedAt: null,
  inputPerMillion: 1,
  cachedInputPerMillion: null,
  outputPerMillion: 2,
  priceSource: 'openrouter',
  priceVaries: false,
  free: false,
  tools: true,
  structuredOutput: true,
  imagesIn: false,
  recommended: true,
  missing: [],
  costPerThousandCalls: null,
  ...extra,
})

test('a context length is written short', () => {
  assert.equal(contextText(200000), '200K')
  assert.equal(contextText(1048576), '1M')
  assert.equal(contextText(8192), '8K')
  assert.equal(contextText(null), '—')
})

test('a missing price is a dash, not zero', () => {
  assert.equal(priceText(null), '—')
  assert.equal(priceText(0), '$0.00')
})

test('what a model cannot do is a chip', () => {
  const chips = chipsOf(model('a/b', { missing: ['tools'], tools: false, priceVaries: true }))

  assert.deepEqual(
    chips.map((c) => c.text),
    ['No tools', 'Price varies', 'Structured output'],
  )
  assert.equal(chips[0]?.warn, true)
})

test('search, maker, what it can do and a price ceiling all narrow the list', () => {
  const models = [
    model('maker-a/cheap', { inputPerMillion: 1, outputPerMillion: 1 }),
    model('maker-b/dear', { inputPerMillion: 30, outputPerMillion: 60, tools: false }),
    model('maker-b/free', { free: true, inputPerMillion: 0, outputPerMillion: 0 }),
  ]

  assert.deepEqual(filterModels(models, { ...noFilters, search: 'DEAR' }).map((m) => m.id), ['maker-b/dear'])
  assert.deepEqual(filterModels(models, { ...noFilters, maker: 'maker-b' }).map((m) => m.id), [
    'maker-b/dear',
    'maker-b/free',
  ])
  assert.deepEqual(filterModels(models, { ...noFilters, tools: true }).map((m) => m.id), [
    'maker-a/cheap',
    'maker-b/free',
  ])
  assert.deepEqual(filterModels(models, { ...noFilters, free: true }).map((m) => m.id), ['maker-b/free'])
  assert.deepEqual(filterModels(models, { ...noFilters, maxPrice: '2' }).map((m) => m.id), [
    'maker-a/cheap',
    'maker-b/free',
  ])
  assert.deepEqual(makersOf(models), ['maker-a', 'maker-b'])
})

test('the list arrives recommended-first and stays that way until a column is sorted', () => {
  const models = [model('a/one', { inputPerMillion: 5 }), model('b/two', { inputPerMillion: 1 })]

  assert.deepEqual(sortModels(models, 'best', false).map((m) => m.id), ['a/one', 'b/two'])
  assert.deepEqual(sortModels(models, 'input', false).map((m) => m.id), ['b/two', 'a/one'])
  assert.deepEqual(sortModels(models, 'input', true).map((m) => m.id), ['a/one', 'b/two'])
})

test('a model with nothing to sort by goes last either way', () => {
  const models = [model('a/none', { contextLength: null }), model('b/some', { contextLength: 1000 })]

  assert.deepEqual(sortModels(models, 'context', false).map((m) => m.id), ['b/some', 'a/none'])
  assert.deepEqual(sortModels(models, 'context', true).map((m) => m.id), ['b/some', 'a/none'])
})
