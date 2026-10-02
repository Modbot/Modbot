import { useRef, type ReactNode } from 'react'
import { Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { PictureCrop } from './PictureCrop'
import type { CroppedPicture } from './useCroppedPicture'

/**
 * One picture field of the event form with its crop box (calendar design §15.3, §15.4): the crop
 * box with Upload and Cancel while a picture is open, otherwise what is set (`set`) or Choose
 * picture, and the reason under it when something was refused.
 */
export function CropField({
  label,
  picture,
  link,
  set,
  onFile,
  refusal,
}: {
  label: string
  picture: CroppedPicture
  /** The event's picture link, which a Cancel marks as not to be opened again on its own. */
  link: string | null
  /** What is set, with its buttons; null when nothing is. */
  set: ReactNode
  /** A picture file chosen here, which the form opens in every crop box that is shown. */
  onFile: (file: Blob) => void
  /** Anything to show beside a refusal, such as where to get what was missing. */
  refusal?: (error: string) => ReactNode
}) {
  const chooser = useRef<HTMLInputElement>(null)
  const { draft, opening, uploading, error } = picture

  return (
    <div className={draft ? 'flex flex-col gap-1 sm:col-span-2' : 'flex flex-col gap-1'} style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      {draft ? (
        <>
          <PictureCrop
            picture={draft.picture}
            aspect={picture.aspect}
            box={draft.box}
            onBox={picture.setBox}
            label={`${label} crop`}
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
                if (draft.from === 'link') picture.setPassed(link)
                picture.cancel()
              }}
            >
              Cancel
            </Button>
          </div>
        </>
      ) : (
        <div className="flex min-h-(--control-h) flex-wrap items-center gap-2">
          {set ?? (
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
        aria-label={label}
        onChange={(e) => {
          const file = e.target.files?.[0]
          if (file) onFile(file)
          // The same file can be chosen again after a refusal.
          e.target.value = ''
        }}
      />
      {error && (
        <div className="flex flex-wrap items-center gap-x-3">
          <Outcome tone="problem">{error}</Outcome>
          {refusal?.(error)}
        </div>
      )}
    </div>
  )
}
