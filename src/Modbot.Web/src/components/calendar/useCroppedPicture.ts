import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError } from '@/lib/api'
import { calendarApi } from '@/lib/calendar'
import { centredCrop, type CropBox } from '@/lib/eventPicture'
import { closePicture, cropToFile, openPicture, type OpenPicture } from '@/lib/pictureFiles'

/** A picture opened in a crop box and not uploaded yet. */
export type PictureDraft = { picture: OpenPicture; box: CropBox; from: 'file' | 'link' }

/** One picture being chosen and cropped in the event form: VRChat's, or Discord's (calendar design §15.3, §15.4). */
export type CroppedPicture = {
  /** Width ÷ height of the crop box. */
  aspect: number
  draft: PictureDraft | null
  /** Moves or sizes the crop box. */
  setBox: (box: CropBox) => void
  /** A picture file chosen on this computer. */
  openFile: (file: Blob) => void
  /** The picture behind a link, fetched by Modbot. */
  openLink: (link: string) => void
  /** Turns the crop into a PNG or JPEG and uploads it; answers with its id, or null when refused. */
  upload: () => Promise<string | null>
  /** Closes the crop box without uploading. */
  cancel: () => void
  opening: boolean
  uploading: boolean
  error: string | null
  setError: (error: string | null) => void
  /** The picture link last opened, closed or removed here: not opened again on its own. */
  passed: string | null
  setPassed: (link: string | null) => void
}

/**
 * Opens the picture behind a picture link given in this form in the crop box, once the typing
 * stops, when nothing is set or open yet (calendar design §15.3). A link opened, closed or removed
 * here, or the one the form opened with, is not opened again on its own.
 */
export function useOpenGivenLink(picture: CroppedPicture, link: string | null, wanted: boolean): void {
  const { draft, opening, passed, setPassed, openLink } = picture

  useEffect(() => {
    if (!wanted || draft || opening || !link || link === passed) return
    if (!/^https:\/\//i.test(link)) return

    const timer = setTimeout(() => {
      setPassed(link)
      openLink(link)
    }, 700)
    return () => clearTimeout(timer)
  }, [wanted, draft, opening, link, passed, setPassed, openLink])
}

/**
 * A picture being chosen in one form, in a crop box of one shape. Held by the form rather than the
 * field, so the crop survives the field being drawn again (the Preview tab, a chip turned off and
 * on) and the Preview tab can draw it before anything is uploaded. The picture is let go of when it
 * is uploaded, cancelled, replaced, or the form closes.
 */
export function useCroppedPicture({
  aspect,
  maxBytes,
  send,
  onUploaded,
  initialLink,
}: {
  aspect: number
  /** The largest file the upload takes. */
  maxBytes: number
  /** Uploads the cropped file and answers with the id to save. */
  send: (file: Blob) => Promise<string>
  /** Told the id and the file once the upload is through. */
  onUploaded: (id: string, file: Blob) => void
  /** The picture link the form opened with, which is not opened on its own: only a link given here is. */
  initialLink: string | null
}): CroppedPicture {
  const [passed, setPassed] = useState<string | null>(initialLink)
  const [draft, setDraftState] = useState<PictureDraft | null>(null)
  const [opening, setOpening] = useState(false)
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const held = useRef<OpenPicture | null>(null)
  const latest = useRef<PictureDraft | null>(null)
  // Only the last picture asked for may open: a slow link answering after a file was chosen is dropped.
  const asked = useRef(0)

  const setDraft = useCallback((next: PictureDraft | null) => {
    if (held.current && held.current !== next?.picture) closePicture(held.current)
    held.current = next?.picture ?? null
    latest.current = next
    setDraftState(next)
  }, [])

  useEffect(
    () => () => {
      asked.current++
      closePicture(held.current)
      held.current = null
    },
    [],
  )

  const open = useCallback(
    (source: () => Promise<Blob>, from: PictureDraft['from']) => {
      const ask = ++asked.current
      setError(null)
      setOpening(true)

      source()
        .then(openPicture)
        .then((picture) => {
          if (ask !== asked.current) {
            closePicture(picture)
            return
          }
          setDraft({ picture, box: centredCrop(picture.width, picture.height, aspect), from })
        })
        .catch((e: unknown) => {
          if (ask === asked.current) setError(e instanceof Error ? e.message : 'Could not open the picture.')
        })
        .finally(() => {
          if (ask === asked.current) setOpening(false)
        })
    },
    [setDraft, aspect],
  )

  const upload = useCallback(async (): Promise<string | null> => {
    const current = latest.current
    if (!current) return null

    setError(null)
    setUploading(true)

    try {
      const file = await cropToFile(current.picture, current.box, aspect, maxBytes)
      const id = await send(file)
      onUploaded(id, file)
      if (latest.current === current) setDraft(null)
      return id
    } catch (e: unknown) {
      setError(e instanceof ApiError || e instanceof Error ? e.message : 'Could not upload the picture.')
      return null
    } finally {
      setUploading(false)
    }
  }, [aspect, maxBytes, send, onUploaded, setDraft])

  return useMemo<CroppedPicture>(
    () => ({
      aspect,
      draft,
      setBox: (box) => {
        if (latest.current) setDraft({ ...latest.current, box })
      },
      openFile: (file) => open(() => Promise.resolve(file), 'file'),
      openLink: (link) => open(() => calendarApi.pictureFromLink(link), 'link'),
      upload,
      cancel: () => {
        asked.current++
        setOpening(false)
        setError(null)
        setDraft(null)
      },
      opening,
      uploading,
      error,
      setError,
      passed,
      setPassed,
    }),
    [aspect, draft, open, upload, opening, uploading, error, setDraft, passed],
  )
}
