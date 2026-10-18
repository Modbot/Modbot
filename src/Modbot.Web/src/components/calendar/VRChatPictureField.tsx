import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { coverAddress, isGalleryRefusal, PLATFORM_LABEL, VRCHAT_PLUS_URL } from '@/lib/calendar'
import { isVRChatFileHost, vrchatFileIdInLink } from '@/lib/eventPicture'
import { CropField } from './CropField'
import { useOpenGivenLink, type CroppedPicture } from './useCroppedPicture'
import type { PictureThumbnails } from './usePictureThumbnails'
import { vrchatPictureAddress } from './useVRChatPicture'

/**
 * The picture for VRChat's calendar (calendar design §2, §15). With uploads on, a picture chosen on
 * this computer, or the one behind the picture link, opens in a 16:9 crop box, VRChat's shape; Upload
 * turns the crop into a PNG or JPEG and sends it, and the file id VRChat answers with is what the
 * event saves. With uploads off, or no VRChat+ on Modbot's account, Choose picture is shown turned
 * off with the reason on it. No id is typed (§15, 2026-10-17): the picture belongs to the VRChat
 * account Modbot publishes as, so an id from a person's own account is refused. A picture the event
 * already has is shown as set, with Remove, whatever the switches say, and is still sent.
 *
 * A picture link from VRChat fills this field on its own (the form does that, §15.1), and the
 * picture shows from the link.
 *
 * The PC, Android and iOS boxes are beside Choose picture, because the icons are drawn into the
 * picture (§15.3, 2026-10-17). They are turned off with the picture, for the same reason.
 */
export function VRChatPictureField({
  uploads,
  plus = null,
  picture,
  thumbnails,
  value,
  link,
  onChange,
  onFile,
  platforms,
  ticked,
  onTick,
}: {
  /** Whether pictures may be uploaded (Settings). */
  uploads: boolean
  /** Whether Modbot's VRChat account has VRChat+. False turns the field off; null is not known. */
  plus?: boolean | null
  /** The picture being chosen, held by the form. */
  picture: CroppedPicture
  /** The form's thumbnails, so one outlives this field being drawn again. */
  thumbnails: PictureThumbnails
  value: string | null
  /** The event's picture link. */
  link: string | null
  onChange: (fileId: string | null) => void
  /** A picture file chosen here, which the form opens in every crop box that is shown. */
  onFile: (file: Blob) => void
  /** Every platform the event can be ticked for. */
  platforms: string[]
  /** The platforms ticked: their icons are drawn into the picture. */
  ticked: string[]
  onTick: (platform: string) => void
}) {
  // Why Choose picture is turned off, if it is. No VRChat+ comes first: it is why the switch is off.
  const off = plus === false ? 'Needs VRChat+' : !uploads ? 'Uploads off' : null
  const shown = vrchatPictureAddress(value, link, thumbnails)
  const fromLink = link !== null && value !== null && vrchatFileIdInLink(link) === value

  // A picture link that is not VRChat's opens in the crop box on its own; one that is fills the
  // field instead (§15.1).
  useOpenGivenLink(picture, link, off === null && !value && !vrchatFileIdInLink(link))

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
      beside={platforms.map((p) => (
        <Checkbox key={p} checked={ticked.includes(p)} disabled={off !== null} title={off ?? undefined} onChange={() => onTick(p)}>
          {PLATFORM_LABEL[p] ?? p}
        </Checkbox>
      ))}
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
              <span>Picture set</span>
            )}
            {off === null && fromLink && link && isVRChatFileHost(new URL(link).hostname) && (
              <Button size="sm" variant="outline" disabled={picture.opening} onClick={() => picture.openLink(link)}>
                {picture.opening ? 'Opening…' : 'Crop'}
              </Button>
            )}
            <Button size="sm" variant="outline" onClick={remove}>
              Remove
            </Button>
          </>
        ) : off ? (
          <>
            <Button size="sm" variant="outline" disabled>
              Choose picture · {off}
            </Button>
            {plus === false && (
              <a className="underline" href={VRCHAT_PLUS_URL} target="_blank" rel="noreferrer noopener">
                Get VRChat+
              </a>
            )}
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
  label = 'Discord picture',
}: {
  picture: CroppedPicture
  value: string | null
  link: string | null
  onChange: (coverId: string | null) => void
  onFile: (file: Blob) => void
  /** On the Bluesky tab with Discord off, the same cover is only Bluesky's picture. */
  label?: string
}) {
  useOpenGivenLink(picture, link, !value)

  return (
    <CropField
      label={label}
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
