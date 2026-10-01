import { useEffect, useMemo, useRef } from 'react'

/** The thumbnails of the pictures uploaded in one form, by file id (see `usePictureThumbnails`). */
export type PictureThumbnails = {
  get: (fileId: string) => string | undefined
  /** Draws the file as the thumbnail of `fileId`. Nothing is kept once the form has closed. */
  add: (fileId: string, file: Blob) => void
  /** Lets go of one thumbnail: the picture was removed. */
  drop: (fileId: string) => void
}

/**
 * The thumbnails of pictures uploaded in a form: links to the files on this computer. Held by the
 * form rather than by the field, so a thumbnail survives the field being drawn again (the Preview
 * tab, the VRChat chip turned off and on), and every link is let go of when the form closes, which
 * is when the browser may free the files.
 */
export function usePictureThumbnails(): PictureThumbnails {
  const urls = useRef(new Map<string, string>())
  const open = useRef(true)

  useEffect(() => {
    const held = urls.current
    open.current = true

    return () => {
      open.current = false
      for (const url of held.values()) URL.revokeObjectURL(url)
      held.clear()
    }
  }, [])

  return useMemo<PictureThumbnails>(
    () => ({
      get: (fileId) => urls.current.get(fileId),
      add: (fileId, file) => {
        // An upload that finishes after the form closed has nowhere to show its thumbnail.
        if (open.current) urls.current.set(fileId, URL.createObjectURL(file))
      },
      drop: (fileId) => {
        const url = urls.current.get(fileId)
        if (url) URL.revokeObjectURL(url)
        urls.current.delete(fileId)
      },
    }),
    [],
  )
}
