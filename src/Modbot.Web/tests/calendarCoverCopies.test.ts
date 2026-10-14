import assert from 'node:assert/strict'
import { test } from 'node:test'
import { makeMissingCoverCopies, type CoverCopyParts } from '../src/lib/calendarCoverCopies.ts'

function parts(over: Partial<CoverCopyParts> = {}, log: string[] = []): CoverCopyParts {
  return {
    missing: async () => ['a', 'b'],
    picture: async (id) => {
      log.push(`picture ${id}`)
      return new Blob([id])
    },
    smallCopy: async (picture) => picture,
    keep: async (id) => {
      log.push(`keep ${id}`)
    },
    stopped: () => false,
    ...over,
  }
}

test('each missing cover is fetched, made small and kept, one after the other', async () => {
  const log: string[] = []

  assert.equal(await makeMissingCoverCopies(parts({}, log)), 2)
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
    ),
    0,
  )
  assert.equal(await makeMissingCoverCopies(parts({ missing: async () => [] })), 0)
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
  )

  assert.equal(kept, 1)
  assert.deepEqual(log, ['picture a', 'keep a'])
})
