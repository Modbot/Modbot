import { calendarApi, CALENDAR_COVER_MAX_BYTES, VRCHAT_PICTURE_MAX_BYTES } from '@/lib/calendar'
import { DISCORD_COVER_ASPECT, VRCHAT_PICTURE_ASPECT, vrchatFileIdInLink } from '@/lib/eventPicture'
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
 * VRChat's picture being chosen (calendar design §15.3): a 16:9 crop, uploaded to VRChat, and the
 * file id VRChat answers with saved on the event.
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
  initialLink: string | null
}): CroppedPicture {
  return useCroppedPicture({
    aspect: VRCHAT_PICTURE_ASPECT,
    maxBytes: VRCHAT_PICTURE_MAX_BYTES,
    send: (file) => calendarApi.uploadVRChatPicture(file, eventId).then((r) => r.fileId),
    onUploaded: (fileId, file) => {
      thumbnails.add(fileId, file)
      onUploaded(fileId)
    },
    initialLink,
  })
}

/**
 * Discord's picture being chosen (calendar design §15.4): a 2.5:1 crop, kept by Modbot, and its id
 * saved on the event as the Discord event's cover and the channel post's picture.
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
    send: (file) => calendarApi.uploadCover(file).then((r) => r.coverId),
    onUploaded: (coverId) => onUploaded(coverId),
    initialLink,
  })
}
