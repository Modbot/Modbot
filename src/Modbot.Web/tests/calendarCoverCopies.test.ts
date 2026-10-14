import assert from 'node:assert/strict'
import { test } from 'node:test'
import { makeMissingCoverCopies, type CoverCopyParts } from '../src/lib/calendarCoverCopies.ts'

/** A server that lists, newest first, the covers not yet kept and not asked to be left out, 20 at most. */
function server(all: string[], kept: Set<string>) {
  return async (skip: string[]) => all.filter((id) => !kept.has(id) && !skip.includes(id)).slice(0, 20)
}

function parts(over: Partial<CoverCopyParts> = {}, log: string[] = [], all = ['a', 'b']): CoverCopyParts {
  const kept = new Set<string>()

  return {
    missing: server(all, kept),
    picture: async (id) => {
      log.push(`picture ${id}`)
      return new Blob([id])
    },
    smallCopy: async (picture) => picture,
    keep: async (id) => {
      log.push(`keep ${id}`)
      kept.add(id)
    },
    stopped: () => false,
    ...over,
  }
}

test('each missing cover is fetched, made small and kept, one after the other', async () => {
  const log: string[] = []

  assert.equal(await makeMissingCoverCopies(parts({}, log), new Set()), 2)
  assert.deepEqual(log, ['picture a', 'keep a', 'picture b', 'keep b'])
})

test('a cover that fails is skipped and the next one is still done', async () => {
  const log: string[] = []
  const kept = await makeMissingCoverCopies(
    parts(
      {
        smallCopy: async (picture) => {
          if ((await picture.text()) === 'a') throw new Error('Could not make the picture small enough for Bluesky.')
          return picture
        },
      },
      log,
    ),
    new Set(),
  )

  assert.equal(kept, 1)
  assert.deepEqual(log, ['picture a', 'picture b', 'keep b'])
})

test('a list that cannot be read, or an empty one, does nothing and says nothing', async () => {
  assert.equal(
    await makeMissingCoverCopies(
      parts({
        missing: async () => {
          throw new Error('403')
        },
      }),
      new Set(),
    ),
    0,
  )
  assert.equal(await makeMissingCoverCopies(parts({ missing: async () => [] }), new Set()), 0)
})

test('no cover is started once the page has stopped', async () => {
  const log: string[] = []
  let stopped = false

  const kept = await makeMissingCoverCopies(
    parts(
      {
        keep: async (id) => {
          log.push(`keep ${id}`)
          stopped = true
        },
        stopped: () => stopped,
      },
      log,
    ),
    new Set(),
  )

  assert.equal(kept, 1)
  assert.deepEqual(log, ['picture a', 'keep a'])
})

test('twenty covers that cannot be converted do not hide the ones behind them', async () => {
  const stuck = Array.from({ length: 20 }, (_, i) => `stuck${i}`)
  const fine = ['old1', 'old2', 'old3']
  const log: string[] = []
  const skipped: string[][] = []
  const base = parts({}, log, [...stuck, ...fine])
  const tried = new Set<string>()

  const kept = await makeMissingCoverCopies(
    {
      ...base,
      missing: (skip) => {
        skipped.push(skip)
        return base.missing(skip)
      },
      smallCopy: async (picture) => {
        if ((await picture.text()).startsWith('stuck')) throw new Error('Could not make the picture small enough for Bluesky.')
        return picture
      },
    },
    tried,
  )

  assert.equal(kept, 3)
  assert.deepEqual(log.filter((line) => line.startsWith('keep')), ['keep old1', 'keep old2', 'keep old3'])
  // The second ask leaves out the twenty that failed (not the ones kept), and a third finds nothing left.
  assert.equal(skipped[0].length, 0)
  assert.equal(skipped[1].length, 20)
  assert.equal(skipped[2].length, 20)
  assert.equal(skipped.length, 3)
  assert.equal(tried.size, 20)
})

test('a cover that failed before is left out', async () => {
  const log: string[] = []

  await makeMissingCoverCopies(parts({}, log), new Set(['a']))

  assert.deepEqual(log, ['picture b', 'keep b'])
})

test('a visit stops after five asks, a hundred covers', async () => {
  const all = Array.from({ length: 150 }, (_, i) => `c${i}`)
  let asks = 0
  const base = parts({}, [], all)

  const kept = await makeMissingCoverCopies(
    {
      ...base,
      missing: (skip) => {
        asks++
        return base.missing(skip)
      },
    },
    new Set(),
  )

  assert.equal(kept, 100)
  assert.equal(asks, 5)
})

test('no more than a hundred failed ids are sent to leave out', async () => {
  const many = new Set(Array.from({ length: 150 }, (_, i) => `x${i}`))
  let sent = -1

  await makeMissingCoverCopies(
    parts({
      missing: async (skip) => {
        sent = skip.length
        return []
      },
    }),
    many,
  )

  assert.equal(sent, 100)
})
