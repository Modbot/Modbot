import { useRef, useState } from 'react'
import { Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { ApiError } from '@/lib/api'
import { calendarApi, VRCHAT_PICTURE_MAX_BYTES, VRCHAT_PICTURE_TYPES } from '@/lib/calendar'
import type { PictureThumbnails } from './usePictureThumbnails'

/**
 * The picture for VRChat's calendar (calendar design §2). Choosing a file uploads it to VRChat at
 * once and keeps the file id VRChat answers with; saving the event saves that id. A picture chosen
 * here shows as a thumbnail, drawn from the file on this computer. One an event already had, from
 * before or from VRChat itself, is only a file id, so it shows as "VRChat picture set".
 *
 * The type and size are checked here before anything is sent, and again by the server from the
 * bytes. A refusal shows under the field.
 */
export function VRChatPictureField({
  eventId,
  thumbnails,
  value,
  onChange,
  onUploading,
}: {
  /** The event, when it is already saved: the server names it in the audit log. */
  eventId: string | null
  /** The form's thumbnails, so one outlives this field being drawn again. */
  thumbnails: PictureThumbnails
  value: string | null
  onChange: (fileId: string | null) => void
  /** Told when an upload starts and ends, so the form does not save without the picture. */
  onUploading: (uploading: boolean) => void
}) {
  const picker = useRef<HTMLInputElement>(null)
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const thumbnail = value ? thumbnails.get(value) : undefined

  const busy = (on: boolean) => {
    setUploading(on)
    onUploading(on)
  }

  const choose = (file: File | undefined) => {
    if (!file) return

    setError(null)

    if (!VRCHAT_PICTURE_TYPES.includes(file.type)) {
      setError('The picture must be a PNG or JPEG.')
      return
    }

    if (file.size > VRCHAT_PICTURE_MAX_BYTES) {
      setError(`The picture is larger than ${VRCHAT_PICTURE_MAX_BYTES / (1024 * 1024)} MB.`)
      return
    }

    busy(true)

    calendarApi
      .uploadVRChatPicture(file, eventId)
      .then(({ fileId }) => {
        thumbnails.add(fileId, file)
        onChange(fileId)
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not upload the picture.'))
      .finally(() => busy(false))
  }

  const remove = () => {
    if (value) thumbnails.drop(value)

    setError(null)
    onChange(null)
  }

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">VRChat picture</span>
      <div className="flex min-h-(--control-h) flex-wrap items-center gap-2">
        {value ? (
          <>
            {thumbnail ? (
              <img src={thumbnail} alt="" className="h-16 max-w-40 rounded-sm border border-(length:--hairline) object-cover" />
            ) : (
              <span>VRChat picture set</span>
            )}
            <Button size="sm" variant="outline" onClick={remove}>
              Remove
            </Button>
          </>
        ) : (
          <Button size="sm" variant="outline" disabled={uploading} onClick={() => picker.current?.click()}>
            {uploading ? 'Uploading…' : 'Choose picture'}
          </Button>
        )}
        <input
          ref={picker}
          type="file"
          accept={VRCHAT_PICTURE_TYPES.join(',')}
          className="hidden"
          aria-label="VRChat picture"
          onChange={(e) => {
            choose(e.target.files?.[0])
            // The same file can be chosen again after a refusal.
            e.target.value = ''
          }}
        />
      </div>
      {error && <Outcome tone="problem">{error}</Outcome>}
    </div>
  )
}
