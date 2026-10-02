import { useState } from 'react'
import { Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { coverAddress, isGalleryRefusal, VRCHAT_PLUS_URL } from '@/lib/calendar'
import { isVRChatFileHost, VRCHAT_FILE_NOT_FOUND, vrchatFileIdIn, vrchatFileIdInLink } from '@/lib/eventPicture'
import { CropField } from './CropField'
import { useOpenGivenLink, type CroppedPicture } from './useCroppedPicture'
import type { PictureThumbnails } from './usePictureThumbnails'
import { vrchatPictureAddress } from './useVRChatPicture'

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
  onFile,
}: {
  /** Whether pictures may be uploaded (Settings). */
  uploads: boolean
  /** The picture being chosen, held by the form. */
  picture: CroppedPicture
  /** The form's thumbnails, so one outlives this field being drawn again. */
  thumbnails: PictureThumbnails
  value: string | null
  /** The id the event was saved with, which is never refused whatever it looks like. */
  saved: string | null
  /** The event's picture link. */
  link: string | null
  onChange: (fileId: string | null) => void
  /** A picture file chosen here, which the form opens in every crop box that is shown. */
  onFile: (file: Blob) => void
}) {
  if (!uploads) return <TypedId value={value} saved={saved} onChange={onChange} />

  return <Upload picture={picture} thumbnails={thumbnails} value={value} link={link} onChange={onChange} onFile={onFile} />
}

function Upload({
  picture,
  thumbnails,
  value,
  link,
  onChange,
  onFile,
}: {
  picture: CroppedPicture
  thumbnails: PictureThumbnails
  value: string | null
  link: string | null
  onChange: (fileId: string | null) => void
  onFile: (file: Blob) => void
}) {
  const shown = vrchatPictureAddress(value, link, thumbnails)
  const fromLink = link !== null && value !== null && vrchatFileIdInLink(link) === value

  // A picture link that is not VRChat's opens in the crop box on its own; one that is fills the
  // field instead (§15.1).
  useOpenGivenLink(picture, link, !value && !vrchatFileIdInLink(link))

  const remove = () => {
    if (value) thumbnails.drop(value)
    picture.setPassed(link)
    picture.setError(null)
    onChange(null)
  }

  return (
    <CropField
      label="VRChat picture"
      picture={picture}
      link={link}
      onFile={onFile}
      refusal={(error) =>
        isGalleryRefusal(error) && (
          <a className="underline" href={VRCHAT_PLUS_URL} target="_blank" rel="noreferrer noopener">
            Get VRChat+
          </a>
        )
      }
      set={
        value ? (
          <>
            {shown ? (
              <img src={shown} alt="" className="aspect-video h-16 rounded-sm border border-(length:--hairline) object-cover" />
            ) : (
              <span>VRChat picture set</span>
            )}
            {fromLink && link && isVRChatFileHost(new URL(link).hostname) && (
              <Button size="sm" variant="outline" disabled={picture.opening} onClick={() => picture.openLink(link)}>
                {picture.opening ? 'Opening…' : 'Crop'}
              </Button>
            )}
            <Button size="sm" variant="outline" onClick={remove}>
              Remove
            </Button>
          </>
        ) : null
      }
    />
  )
}

/**
 * The picture for Discord (calendar design §15.4): the Discord event's cover and the channel post's
 * picture, cropped to 2.5:1, Discord's cover shape, and kept by Modbot. A picture chosen on this
 * computer or the one behind the picture link opens in the crop box. Without one, Discord gets the
 * picture link as it is.
 */
export function DiscordPictureField({
  picture,
  value,
  link,
  onChange,
  onFile,
}: {
  picture: CroppedPicture
  value: string | null
  link: string | null
  onChange: (coverId: string | null) => void
  onFile: (file: Blob) => void
}) {
  useOpenGivenLink(picture, link, !value)

  return (
    <CropField
      label="Discord picture"
      picture={picture}
      link={link}
      onFile={onFile}
      set={
        value ? (
          <>
            <img
              src={coverAddress(value)}
              alt=""
              className="aspect-[2.5/1] h-16 rounded-sm border border-(length:--hairline) object-cover"
            />
            {link && (
              <Button size="sm" variant="outline" disabled={picture.opening} onClick={() => picture.openLink(link)}>
                {picture.opening ? 'Opening…' : 'Crop'}
              </Button>
            )}
            <Button
              size="sm"
              variant="outline"
              onClick={() => {
                picture.setPassed(link)
                picture.setError(null)
                onChange(null)
              }}
            >
              Remove
            </Button>
          </>
        ) : null
      }
    />
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
