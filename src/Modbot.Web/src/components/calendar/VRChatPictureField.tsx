import { useEffect, useRef, useState } from 'react'
import { Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { isGalleryRefusal, VRCHAT_PLUS_URL } from '@/lib/calendar'
import {
  isVRChatFileHost,
  VRCHAT_FILE_NOT_FOUND,
  VRCHAT_PICTURE_ASPECT,
  vrchatFileIdIn,
  vrchatFileIdInLink,
} from '@/lib/eventPicture'
import { PictureCrop } from './PictureCrop'
import type { PictureThumbnails } from './usePictureThumbnails'
import { vrchatPictureAddress, type VRChatPicture } from './useVRChatPicture'

/**
 * The picture for VRChat's calendar (calendar design §2, §15). With uploads on, a picture chosen on
 * this computer, or the one behind the picture link, opens in a 16:9 crop box, VRChat's shape; Upload
 * turns the crop into a PNG or JPEG and sends it, and the file id VRChat answers with is what the
 * event saves. With uploads off it is the id typed or pasted, and any VRChat link pasted here is
 * cut down to the id inside it.
 *
 * A picture link from VRChat fills this field on its own (the form does that, §15.1), and the
 * picture shows from the link.
 */
export function VRChatPictureField({
  uploads,
  picture,
  thumbnails,
  value,
  saved,
  link,
  onChange,
}: {
  /** Whether pictures may be uploaded (Settings). */
  uploads: boolean
  /** The picture being chosen, held by the form. */
  picture: VRChatPicture
  /** The form's thumbnails, so one outlives this field being drawn again. */
  thumbnails: PictureThumbnails
  value: string | null
  /** The id the event was saved with, which is never refused whatever it looks like. */
  saved: string | null
  /** The event's picture link. */
  link: string | null
  onChange: (fileId: string | null) => void
}) {
  if (!uploads) return <TypedId value={value} saved={saved} onChange={onChange} />

  return <Upload picture={picture} thumbnails={thumbnails} value={value} link={link} onChange={onChange} />
}

function Upload({
  picture,
  thumbnails,
  value,
  link,
  onChange,
}: {
  picture: VRChatPicture
  thumbnails: PictureThumbnails
  value: string | null
  link: string | null
  onChange: (fileId: string | null) => void
}) {
  const chooser = useRef<HTMLInputElement>(null)
  const { draft, opening, uploading, error, passed, setPassed } = picture
  const shown = vrchatPictureAddress(value, link, thumbnails)
  const fromLink = link !== null && value !== null && vrchatFileIdInLink(link) === value

  // A picture link that is not VRChat's opens in the crop box on its own, once the typing stops.
  const { openLink } = picture
  useEffect(() => {
    if (value || draft || opening || !link || link === passed) return
    if (!/^https:\/\//i.test(link) || vrchatFileIdInLink(link)) return

    const timer = setTimeout(() => {
      setPassed(link)
      openLink(link)
    }, 700)
    return () => clearTimeout(timer)
  }, [value, draft, opening, link, passed, setPassed, openLink])

  const remove = () => {
    if (value) thumbnails.drop(value)
    setPassed(link)
    picture.setError(null)
    onChange(null)
  }

  return (
    <div className={draft ? 'flex flex-col gap-1 sm:col-span-2' : 'flex flex-col gap-1'} style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">VRChat picture</span>
      {draft ? (
        <>
          <PictureCrop
            picture={draft.picture}
            aspect={VRCHAT_PICTURE_ASPECT}
            box={draft.box}
            onBox={picture.setBox}
            label="VRChat picture crop"
          />
          <div className="flex flex-wrap items-center gap-2">
            <Button size="sm" disabled={uploading} onClick={() => void picture.upload()}>
              {uploading ? 'Uploading…' : 'Upload'}
            </Button>
            <Button
              size="sm"
              variant="outline"
              disabled={uploading}
              onClick={() => {
                if (draft.from === 'link') setPassed(link)
                picture.cancel()
              }}
            >
              Cancel
            </Button>
          </div>
        </>
      ) : (
        <div className="flex min-h-(--control-h) flex-wrap items-center gap-2">
          {value ? (
            <>
              {shown ? (
                <img src={shown} alt="" className="aspect-video h-16 rounded-sm border border-(length:--hairline) object-cover" />
              ) : (
                <span>VRChat picture set</span>
              )}
              {fromLink && link && isVRChatFileHost(new URL(link).hostname) && (
                <Button size="sm" variant="outline" disabled={opening} onClick={() => picture.openLink(link)}>
                  {opening ? 'Opening…' : 'Crop'}
                </Button>
              )}
              <Button size="sm" variant="outline" onClick={remove}>
                Remove
              </Button>
            </>
          ) : (
            <Button size="sm" variant="outline" disabled={opening} onClick={() => chooser.current?.click()}>
              {opening ? 'Opening…' : 'Choose picture'}
            </Button>
          )}
        </div>
      )}
      <input
        ref={chooser}
        type="file"
        accept="image/*"
        className="hidden"
        aria-label="VRChat picture"
        onChange={(e) => {
          const file = e.target.files?.[0]
          if (file) picture.openFile(file)
          // The same file can be chosen again after a refusal.
          e.target.value = ''
        }}
      />
      {error && (
        <div className="flex flex-wrap items-center gap-x-3">
          <Outcome tone="problem">{error}</Outcome>
          {isGalleryRefusal(error) && (
            <a className="underline" href={VRCHAT_PLUS_URL} target="_blank" rel="noreferrer noopener">
              Get VRChat+
            </a>
          )}
        </div>
      )}
    </div>
  )
}

/**
 * The id typed or pasted, with uploads off. A VRChat link becomes the id inside it as it is pasted;
 * text with no id in it is kept as typed, so it can still be typed, and says so once the field is
 * left.
 */
function TypedId({
  value,
  saved,
  onChange,
}: {
  value: string | null
  saved: string | null
  onChange: (fileId: string | null) => void
}) {
  const [left, setLeft] = useState(false)
  const text = value ?? ''
  const missing = text.trim() !== '' && text.trim() !== saved && vrchatFileIdIn(text) === null

  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">VRChat image id</span>
      <Input
        value={text}
        placeholder="file_…"
        aria-invalid={left && missing}
        onChange={(e) => {
          const typed = e.target.value
          onChange(vrchatFileIdIn(typed) ?? (typed.trim() || null))
        }}
        onBlur={() => setLeft(true)}
      />
      {left && missing && <Outcome tone="problem">{VRCHAT_FILE_NOT_FOUND}</Outcome>}
    </label>
  )
}
