import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Outcome } from '@/components/settings/fields'
import { ApiError, api, type BlueskyProfile } from '@/lib/api'
import { countedGraphemes } from '@/lib/calendarPlaces'
import { timeOfDay } from '@/lib/format'
import { smallJpeg } from '@/lib/pictureFiles'
import { BLUESKY_PICTURE_MAX_BYTES } from '@/lib/postRules'
import { cn } from '@/lib/utils'

/** Bluesky's limits for the name and the bio, in characters. */
const NAME_LIMIT = 64
const BIO_LIMIT = 256
/** And in UTF-8 bytes, which a few characters can reach: the server refuses both. */
const NAME_BYTE_LIMIT = 640
const BIO_BYTE_LIMIT = 2560
/** The longest side of the picture. The banner keeps a wide one. */
const AVATAR_SIDE = 1000
const BANNER_SIDE = 2000

const utf8 = new TextEncoder()

/** The count shown beside a field: in characters, or in bytes once the bytes are the limit that is passed. */
function countedText(text: string, limit: number, byteLimit: number): { label: string; over: boolean } {
  const characters = countedGraphemes(text, limit)
  const bytes = utf8.encode(text.trim()).length
  if (!characters.over && bytes > byteLimit) return { label: `${bytes} / ${byteLimit} bytes`, over: true }
  return characters
}

/**
 * Edit profile: the name, the bio, the picture and the banner, and nothing else on the account.
 *
 * Only what was changed is sent. A name or bio emptied on purpose is sent empty, which clears it. A
 * picture is first made into a copy under 1 MB here, the way a post's picture is, because the server
 * has no image library. The dialog closes when Bluesky has taken the save; a failure keeps it open
 * with the server's own words.
 */
export function EditProfileDialog({
  open,
  onOpenChange,
  profile,
  limitedUntil,
  onSaved,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  profile: BlueskyProfile
  /** While Bluesky is limiting Modbot: when it ends. Save waits for it. */
  limitedUntil: string | null
  onSaved: (saved: BlueskyProfile) => void
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {/* Mounted only while open, so each opening starts from the profile as it is now. */}
      {open && <EditBody profile={profile} limitedUntil={limitedUntil} onClose={() => onOpenChange(false)} onSaved={onSaved} />}
    </Dialog>
  )
}

function EditBody({
  profile,
  limitedUntil,
  onClose,
  onSaved,
}: {
  profile: BlueskyProfile
  limitedUntil: string | null
  onClose: () => void
  onSaved: (saved: BlueskyProfile) => void
}) {
  const [name, setName] = useState(profile.displayName ?? '')
  const [bio, setBio] = useState(profile.bio ?? '')
  const [avatar, setAvatar] = useState<File | null>(null)
  const [banner, setBanner] = useState<File | null>(null)
  const [saving, setSaving] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const nameCount = countedText(name, NAME_LIMIT, NAME_BYTE_LIMIT)
  const bioCount = countedText(bio, BIO_LIMIT, BIO_BYTE_LIMIT)

  const nameChanged = name.trim() !== (profile.displayName ?? '').trim()
  const bioChanged = bio.trim() !== (profile.bio ?? '').trim()
  const changed = nameChanged || bioChanged || avatar !== null || banner !== null

  const save = async () => {
    setSaving(true)
    setProblem(null)

    try {
      const form = new FormData()
      if (nameChanged) form.append('displayName', name.trim())
      if (bioChanged) form.append('bio', bio.trim())
      if (avatar) form.append('avatar', await smallJpeg(avatar, BLUESKY_PICTURE_MAX_BYTES, AVATAR_SIDE), 'avatar.jpg')
      if (banner) form.append('banner', await smallJpeg(banner, BLUESKY_PICTURE_MAX_BYTES, BANNER_SIDE), 'banner.jpg')

      onSaved(await api.saveBlueskyProfile(form))
      onClose()
    } catch (e: unknown) {
      setProblem(e instanceof ApiError || e instanceof Error ? e.message : 'Could not save the profile.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <DialogContent
      title="Edit profile"
      className="max-w-[520px]"
      foot={
        <DialogFoot>
          <Button size="sm" variant="outline" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button size="sm" onClick={save} disabled={saving || !changed || nameCount.over || bioCount.over || limitedUntil !== null}>
            {saving ? 'Saving…' : 'Save'}
          </Button>
        </DialogFoot>
      }
    >
      <div className="flex flex-col gap-4" style={{ fontSize: 'var(--text-small)' }}>
        <PictureField label="Picture" current={profile.avatarUrl} onChoose={setAvatar} disabled={saving} />
        <PictureField label="Banner" current={profile.bannerUrl} onChoose={setBanner} disabled={saving} wide />

        <label className="flex flex-col gap-1">
          <span className="flex items-baseline justify-between gap-2">
            <span className="text-muted-foreground">Name</span>
            <Count counted={nameCount} />
          </span>
          <Input value={name} onChange={(e) => setName(e.target.value)} disabled={saving} aria-invalid={nameCount.over || undefined} />
        </label>

        <label className="flex flex-col gap-1">
          <span className="flex items-baseline justify-between gap-2">
            <span className="text-muted-foreground">Bio</span>
            <Count counted={bioCount} />
          </span>
          <Textarea
            rows={4}
            value={bio}
            onChange={(e) => setBio(e.target.value)}
            disabled={saving}
            aria-invalid={bioCount.over || undefined}
          />
        </label>

        {limitedUntil && <Outcome tone="problem">Bluesky is limiting Modbot. Try again after {timeOfDay(limitedUntil)}.</Outcome>}
        {problem && <Outcome tone="problem">{problem}</Outcome>}
      </div>
    </DialogContent>
  )
}

function Count({ counted }: { counted: { label: string; over: boolean } }) {
  return <span className={cn('font-mono', counted.over ? 'text-destructive' : 'text-muted-foreground')}>{counted.label}</span>
}

/** The picture as it is on Bluesky, or the file just chosen, and the button that chooses one. */
function PictureField({
  label,
  current,
  onChoose,
  disabled,
  wide = false,
}: {
  label: string
  current: string | null
  onChoose: (file: File | null) => void
  disabled: boolean
  wide?: boolean
}) {
  const [preview, setPreview] = useState<string | null>(null)
  const latest = useRef<string | null>(null)
  // Changing the key empties the file box, so un-choosing leaves nothing chosen in it either.
  const [box, setBox] = useState(0)

  // The address of the chosen file's preview is given back when a new file replaces it and when the dialog closes.
  useEffect(
    () => () => {
      if (latest.current) URL.revokeObjectURL(latest.current)
    },
    [],
  )

  const choose = (file: File | null) => {
    if (latest.current) URL.revokeObjectURL(latest.current)
    latest.current = file ? URL.createObjectURL(file) : null
    setPreview(latest.current)
    onChoose(file)
  }

  const shown = preview ?? current

  return (
    <div className="flex flex-col gap-1">
      <span className="text-muted-foreground">{label}</span>
      <div className="flex flex-wrap items-center gap-3">
        {shown ? (
          <img
            src={shown}
            alt=""
            referrerPolicy="no-referrer"
            className={cn('shrink-0 bg-muted object-cover', wide ? 'aspect-[3/1] w-48 rounded-sm' : 'size-16 rounded-full')}
          />
        ) : (
          <div className={cn('shrink-0 bg-muted', wide ? 'aspect-[3/1] w-48 rounded-sm' : 'size-16 rounded-full')} />
        )}
        <Input
          type="file"
          accept="image/*"
          aria-label={label}
          disabled={disabled}
          className="min-w-0 flex-1 basis-48"
          key={box}
          onChange={(e) => choose(e.target.files?.[0] ?? null)}
        />
        {preview && (
          <Button
            size="sm"
            variant="outline"
            disabled={disabled}
            onClick={() => {
              choose(null)
              setBox((n) => n + 1)
            }}
          >
            Undo
          </Button>
        )}
      </div>
    </div>
  )
}
