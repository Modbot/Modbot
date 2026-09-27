import { useCallback, useRef, useState } from 'react'
import type { EvidenceDelivery } from '@/lib/api'
import { attach, busy, failure, send, tooLarge, type Progress } from '@/lib/evidenceUpload'

export type BanFile = { key: string; name: string; video: boolean; progress: Progress }

/** Where the files go once the ban has answered: a case file, or nowhere and why. */
type Outcome = { caseId: string } | { message: string }

/**
 * The screenshots and videos picked in the ban dialog.
 *
 * Each file starts sending the moment it is picked, with no case file named, so by the time the
 * moderator has typed what happened the bytes are usually already there. The case file is only
 * written once VRChat has accepted the ban; `settle` then names it and every file is committed to
 * it -- a file still sending is committed when it finishes. A file picked after the ban goes the
 * same way and is committed straight away. A ban that did not go through attaches nothing: the
 * staged bytes are left for the store's sweep.
 */
export function useBanFiles() {
  const [items, setItems] = useState<BanFile[]>([])
  const removed = useRef(new Set<string>())
  const outcome = useRef(deferred<Outcome>())

  const update = useCallback(
    (key: string, progress: Progress) =>
      setItems((previous) => previous.map((item) => (item.key === key ? { ...item, progress } : item))),
    [],
  )

  const add = useCallback(
    (file: File, delivery: EvidenceDelivery) => {
      const key = crypto.randomUUID()
      const refusal = tooLarge(file, delivery)

      setItems((previous) => [
        ...previous,
        {
          key,
          name: file.name,
          video: file.type.startsWith('video/'),
          progress: refusal ? { phase: 'failed', message: refusal } : { phase: 'hashing' },
        },
      ])
      if (refusal) return

      const onProgress = (progress: Progress) => {
        if (!removed.current.has(key)) update(key, progress)
      }

      void send(file, null, onProgress)
        .then(async (sent) => {
          const settled = await outcome.current.promise
          if (removed.current.has(key)) return
          if ('message' in settled) {
            onProgress({ phase: 'failed', message: settled.message })
            return
          }
          await attach(sent, settled.caseId, onProgress)
        })
        .catch((e: unknown) => onProgress(failure(e)))
    },
    [update],
  )

  const remove = useCallback((key: string) => {
    removed.current.add(key)
    setItems((previous) => previous.filter((item) => item.key !== key))
  }, [])

  const settle = useCallback((next: Outcome) => outcome.current.resolve(next), [])

  return { items, add, remove, settle, sending: items.some((item) => busy(item.progress)) }
}

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((r) => {
    resolve = r
  })
  return { promise, resolve }
}
