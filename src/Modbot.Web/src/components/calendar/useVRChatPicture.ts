import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError } from '@/lib/api'
import { calendarApi, VRCHAT_PICTURE_MAX_BYTES } from '@/lib/calendar'
import { centredCrop, VRCHAT_PICTURE_ASPECT, vrchatFileIdInLink, type CropBox } from '@/lib/eventPicture'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { closePicture, cropToFile, openPicture, type OpenPicture } from '@/lib/pictureFiles'
import type { PictureThumbnails } from './usePictureThumbnails'

/** Where the set picture can be seen, when this page knows: one uploaded here, or the picture link's own. */
export function vrchatPictureAddress(
  value: string | null,
  link: string | null,
  thumbnails: PictureThumbnails,
): string | null {
  if (!value) return null
  const uploaded = thumbnails.get(value)
  if (uploaded) return uploaded
  return link && vrchatFileIdInLink(link) === value ? vrchatMedia(link) : null
}

/** A picture opened in the crop box and not uploaded yet. */
export type PictureDraft = { picture: OpenPicture; box: CropBox; from: 'file' | 'link' }

export type VRChatPicture = {
  draft: PictureDraft | null
  /** Moves or sizes the crop box. */
  setBox: (box: CropBox) => void
  /** A picture file chosen on this computer. */
  openFile: (file: Blob) => void
  /** The picture behind a link, fetched by Modbot. */
  openLink: (link: string) => void
  /** Turns the crop into a PNG or JPEG and uploads it; answers with the file id, or null when refused. */
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
 * The VRChat picture being chosen in one form (calendar design §15.3). Held by the form rather than
 * the field, so the crop survives the field being drawn again (the Preview tab, the VRChat chip
 * turned off and on) and the Preview tab can draw it before anything is uploaded. The picture is
 * let go of when it is uploaded, cancelled, replaced, or the form closes.
 */
export function useVRChatPicture({
  eventId,
  thumbnails,
  onUploaded,
  initialLink,
}: {
  eventId: string | null
  thumbnails: PictureThumbnails
  onUploaded: (fileId: string) => void
  /** The picture link the form opened with, which is not opened on its own: only a link given here is. */
  initialLink: string | null
}): VRChatPicture {
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
          setDraft({ picture, box: centredCrop(picture.width, picture.height, VRCHAT_PICTURE_ASPECT), from })
        })
        .catch((e: unknown) => {
          if (ask === asked.current) setError(e instanceof Error ? e.message : 'Could not open the picture.')
        })
        .finally(() => {
          if (ask === asked.current) setOpening(false)
        })
    },
    [setDraft],
  )

  const upload = useCallback(async (): Promise<string | null> => {
    const current = latest.current
    if (!current) return null

    setError(null)
    setUploading(true)

    try {
      const file = await cropToFile(current.picture, current.box, VRCHAT_PICTURE_ASPECT, VRCHAT_PICTURE_MAX_BYTES)
      const { fileId } = await calendarApi.uploadVRChatPicture(file, eventId)
      thumbnails.add(fileId, file)
      onUploaded(fileId)
      if (latest.current === current) setDraft(null)
      return fileId
    } catch (e: unknown) {
      setError(e instanceof ApiError || e instanceof Error ? e.message : 'Could not upload the picture.')
      return null
    } finally {
      setUploading(false)
    }
  }, [eventId, thumbnails, onUploaded, setDraft])

  return useMemo<VRChatPicture>(
    () => ({
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
    [draft, open, upload, opening, uploading, error, setDraft, passed],
  )
}
