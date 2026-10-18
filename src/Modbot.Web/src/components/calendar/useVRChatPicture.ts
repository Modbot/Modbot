import { useEffect, useRef } from 'react'
import { calendarApi, CALENDAR_COVER_MAX_BYTES, VRCHAT_PICTURE_MAX_BYTES } from '@/lib/calendar'
import { smallJpeg } from '@/lib/pictureFiles'
import { BLUESKY_PICTURE_MAX_BYTES } from '@/lib/postRules'
import { platformsKey } from '@/lib/eventPlatforms'
import { DISCORD_COVER_ASPECT, VRCHAT_PICTURE_ASPECT, vrchatFileIdInLink, type CropBox } from '@/lib/eventPicture'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { useCroppedPicture, type CroppedPicture } from './useCroppedPicture'
import type { PictureThumbnails } from './usePictureThumbnails'

/** Where the set VRChat picture can be seen, when this page knows: one uploaded here, or the picture link's own. */
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

/**
 * VRChat's picture being chosen (calendar design §15.3): a 16:9 crop with the event's platform icons
 * drawn in its corner, uploaded to VRChat, and the file id VRChat answers with saved on the event.
 *
 * The icons are part of the file, so the file has to match the ticks. A picture uploaded here, from
 * a chosen file or the world's own picture, is kept; when the ticks change after that it opens in the
 * crop box again, where it was cropped, and Save uploads it again. Nothing is uploaded on a tick
 * (VRChat takes one upload a minute). A picture the event had before the form opened is not kept, so
 * its icons stay as they were.
 */
export function useVRChatPicture({
  eventId,
  thumbnails,
  onUploaded,
  initialLink,
  value,
  platforms,
}: {
  eventId: string | null
  thumbnails: PictureThumbnails
  onUploaded: (fileId: string) => void
  initialLink: string | null
  /** The event's VRChat picture now: the kept source is used only while this is still the uploaded one. */
  value: string | null
  /** The icons to draw: the event's ticked platforms. */
  platforms: string[]
}): CroppedPicture {
  const uploaded = useRef<{ id: string; source: Blob; box: CropBox; key: string } | null>(null)
  const key = platformsKey(platforms)

  const picture = useCroppedPicture({
    aspect: VRCHAT_PICTURE_ASPECT,
    maxBytes: VRCHAT_PICTURE_MAX_BYTES,
    send: (file) => calendarApi.uploadVRChatPicture(file, eventId).then((r) => r.fileId),
    onUploaded: (fileId, file, from) => {
      thumbnails.add(fileId, file)
      uploaded.current = { id: fileId, source: from.source, box: from.box, key }
      onUploaded(fileId)
    },
    initialLink,
    platforms,
  })

  const { draft, opening, openFile } = picture

  useEffect(() => {
    const kept = uploaded.current
    if (!kept || kept.key === key || kept.id !== value || draft || opening) return

    // Marked as handled, so a Cancel is not undone by this running again.
    kept.key = key
    openFile(kept.source, kept.box)
  }, [key, value, draft, opening, openFile])

  return picture
}

/**
 * Discord's picture being chosen (calendar design §15.4): a 2.5:1 crop, kept by Modbot, and its id
 * saved on the event as the Discord event's cover and the channel post's picture, and Bluesky's
 * picture.
 */
export function useDiscordCover({
  onUploaded,
  initialLink,
}: {
  onUploaded: (coverId: string) => void
  initialLink: string | null
}): CroppedPicture {
  return useCroppedPicture({
    aspect: DISCORD_COVER_ASPECT,
    maxBytes: CALENDAR_COVER_MAX_BYTES,
    send: async (file) => {
      const { coverId } = await calendarApi.uploadCover(file)

      // Bluesky is sent a small JPEG copy of the cover. A cover that cannot be made small enough is
      // still saved: its Bluesky posts then go with the cover itself when it is under 1 MB, else
      // with their link card only.
      try {
        await calendarApi.uploadCoverBlueskyCopy(coverId, await smallJpeg(file, BLUESKY_PICTURE_MAX_BYTES))
      } catch {
        // Nothing to say: the cover is what was asked for.
      }

      return coverId
    },
    onUploaded: (coverId) => onUploaded(coverId),
    initialLink,
  })
}
