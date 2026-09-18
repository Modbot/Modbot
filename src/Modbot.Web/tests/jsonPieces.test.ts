import assert from 'node:assert/strict'
import { test } from 'node:test'
import { jsonPieces, type JsonPiece } from '../src/lib/jsonPieces.ts'

/** The pieces as `kind:text` pairs, which reads better in a failure than a wall of objects. */
function pairs(text: string): string[] {
  return jsonPieces(text).map((p) => `${p.kind}:${p.text}`)
}

/** Every piece of a kind, in order. */
function of(text: string, kind: JsonPiece['kind']): string[] {
  return jsonPieces(text)
    .filter((p) => p.kind === kind)
    .map((p) => p.text)
}

// The one rule the whole viewer rests on: the pieces are the document, not a rewrite of it. If
// this ever fails, the screen is showing something the server did not send.
function survivesWhole(text: string) {
  assert.equal(jsonPieces(text).map((p) => p.text).join(''), text)
}

test('every kind of value is told apart', () => {
  const json = JSON.stringify({ name: 'Ava', count: 12, ok: true, off: false, gone: null }, null, 2)

  assert.deepEqual(of(json, 'key'), ['"name"', '"count"', '"ok"', '"off"', '"gone"'])
  assert.deepEqual(of(json, 'string'), ['"Ava"'])
  assert.deepEqual(of(json, 'number'), ['12'])
  assert.deepEqual(of(json, 'boolean'), ['true', 'false'])
  assert.deepEqual(of(json, 'null'), ['null'])
  survivesWhole(json)
})

test('a key is a string with a colon after it, wherever the whitespace falls', () => {
  assert.deepEqual(pairs('{"a":1}'), ['punctuation:{', 'key:"a"', 'punctuation::', 'number:1', 'punctuation:}'])
  assert.deepEqual(of('{"a"\n  :\n  "b"}', 'key'), ['"a"'])
  assert.deepEqual(of('{"a"\n  :\n  "b"}', 'string'), ['"b"'])
  // The same text as a list member is a value, not a key.
  assert.deepEqual(of('["a","b"]', 'key'), [])
  assert.deepEqual(of('["a","b"]', 'string'), ['"a"', '"b"'])
})

test('punctuation and the indentation around it come back as one piece', () => {
  assert.deepEqual(pairs('{\n  "a": 1,\n  "b": 2\n}'), [
    'punctuation:{\n  ',
    'key:"a"',
    'punctuation:: ',
    'number:1',
    'punctuation:,\n  ',
    'key:"b"',
    'punctuation:: ',
    'number:2',
    'punctuation:\n}',
  ])
})

test('nested objects and lists keep their keys and values apart', () => {
  const json = JSON.stringify({ person: { id: 'usr_1', tags: [1, 'two', null] } }, null, 2)

  assert.deepEqual(of(json, 'key'), ['"person"', '"id"', '"tags"'])
  assert.deepEqual(of(json, 'string'), ['"usr_1"', '"two"'])
  assert.deepEqual(of(json, 'number'), ['1'])
  assert.deepEqual(of(json, 'null'), ['null'])
  survivesWhole(json)
})

test('an empty object and an empty list are punctuation and nothing else', () => {
  assert.deepEqual(pairs('{}'), ['punctuation:{}'])
  assert.deepEqual(pairs('[]'), ['punctuation:[]'])
  assert.deepEqual(pairs('{\n  "a": {},\n  "b": []\n}').filter((p) => p.startsWith('key')), ['key:"a"', 'key:"b"'])
})

test('a quote inside a string does not end it', () => {
  const json = JSON.stringify({ said: 'she said "hi"' })

  assert.deepEqual(of(json, 'string'), ['"she said \\"hi\\""'])
  assert.deepEqual(of(json, 'key'), ['"said"'])
  survivesWhole(json)
})

test('a backslash before the closing quote does not swallow it', () => {
  const json = JSON.stringify({ path: 'C:\\', next: 1 })

  assert.deepEqual(of(json, 'string'), ['"C:\\\\"'])
  assert.deepEqual(of(json, 'key'), ['"path"', '"next"'])
  assert.deepEqual(of(json, 'number'), ['1'])
  survivesWhole(json)
})

test('a string full of markup is one string and stays one string', () => {
  const json = JSON.stringify({ bio: '<script>alert("x")</script>', ok: true }, null, 2)

  assert.deepEqual(of(json, 'string'), ['"<script>alert(\\"x\\")</script>"'])
  assert.deepEqual(of(json, 'boolean'), ['true'])
  survivesWhole(json)
})

test('a string that reads like JSON is still one string', () => {
  const json = JSON.stringify({ note: '{"a": 1, "b": null}' }, null, 2)

  assert.equal(of(json, 'string').length, 1)
  assert.deepEqual(of(json, 'key'), ['"note"'])
  assert.deepEqual(of(json, 'number'), [])
  assert.deepEqual(of(json, 'null'), [])
})

test('numbers are read the way JSON writes them', () => {
  assert.deepEqual(of('[0, -1, 1.5, -2.25, 1e3, 1E+3, 2e-3, 1234567890123]', 'number'), [
    '0',
    '-1',
    '1.5',
    '-2.25',
    '1e3',
    '1E+3',
    '2e-3',
    '1234567890123',
  ])
})

test('what is not JSON is left alone rather than coloured as if it were', () => {
  // Leading plus, a bare point, a trailing point: none of these are JSON numbers.
  assert.deepEqual(of('[+1, .5, 5., NaN, undefined]', 'number'), [])
  assert.deepEqual(of('[+1, .5, 5., NaN, undefined]', 'other'), ['+1', '.5', '5.', 'NaN', 'undefined'])
  // A word that only starts like a value is not that value.
  assert.deepEqual(of('[truely, nullish, falsehood]', 'boolean'), [])
  assert.deepEqual(of('[truely, nullish, falsehood]', 'null'), [])
  survivesWhole('[+1, .5, 5., NaN, undefined, truely]')
})

test('a document cut off part way through still comes back whole', () => {
  survivesWhole('{\n  "a": "unfinis')
  survivesWhole('{\n  "a": 1,')
  survivesWhole('')
  assert.deepEqual(jsonPieces(''), [])
  assert.deepEqual(of('{"a": "unfinis', 'string'), ['"unfinis'])
})

test('values that are not inside an object are read the same way', () => {
  assert.deepEqual(pairs('"alone"'), ['string:"alone"'])
  assert.deepEqual(pairs('null'), ['null:null'])
  assert.deepEqual(pairs('42'), ['number:42'])
  assert.deepEqual(pairs('false'), ['boolean:false'])
})

test('a real record of the shape the app shows survives whole', () => {
  const json = JSON.stringify(
    {
      id: 'usr_00000000-0000-0000-0000-000000000000',
      displayName: 'Ava',
      bio: 'line one\nline two\ttabbed — “curly” 🙂',
      tags: ['system_trust_veteran', 'language_eng'],
      profilePicOverride: '',
      ageVerified: false,
      last_login: null,
      friendKey: { count: 0, list: [] },
    },
    null,
    2,
  )

  survivesWhole(json)
  assert.deepEqual(of(json, 'number'), ['0'])
  assert.deepEqual(of(json, 'null'), ['null'])
  assert.deepEqual(of(json, 'boolean'), ['false'])
  assert.deepEqual(of(json, 'other'), [])
})

test('a big document is split quickly enough to render', () => {
  const json = JSON.stringify(
    Array.from({ length: 5000 }, (_, i) => ({ id: `usr_${i}`, count: i, ok: i % 2 === 0, gone: null })),
    null,
    2,
  )

  const started = performance.now()
  const pieces = jsonPieces(json)
  const took = performance.now() - started

  assert.ok(pieces.length > 10_000)
  assert.ok(took < 500, `splitting ${json.length} characters took ${took.toFixed(0)} ms`)
  survivesWhole(json)
})
