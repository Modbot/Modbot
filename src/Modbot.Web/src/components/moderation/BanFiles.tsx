import { useId, useRef } from 'react'
import { FileImage, FileVideo, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ProgressLine } from '@/components/EvidenceGallery'
import type { EvidenceDelivery } from '@/lib/api'
import type { BanFile } from '@/components/moderation/useBanFiles'

/** The picked files, each with its own progress. Removable until the ban is sent. */
export function BanFileList({ items, onRemove }: { items: BanFile[]; onRemove?: (key: string) => void }) {
  if (items.length === 0) return null

  return (
    <ul className="flex flex-col gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
      {items.map((item) => {
        const Icon = item.video ? FileVideo : FileImage
        return (
          <li key={item.key} className="flex flex-col gap-0.5">
            <div className="flex min-w-0 items-center gap-1.5">
              <Icon className="size-4 shrink-0 text-muted-foreground" aria-hidden />
              <span className="min-w-0 flex-1 truncate" title={item.name}>
                {item.name}
              </span>
              {onRemove && (
                <Button size="icon-xs" variant="ghost" aria-label={`Remove ${item.name}`} onClick={() => onRemove(item.key)}>
                  <X />
                </Button>
              )}
            </div>
            {item.progress.phase !== 'done' ? (
              <ProgressLine progress={item.progress} />
            ) : (
              <p className="text-ok">Attached</p>
            )}
          </li>
        )
      })}
    </ul>
  )
}

/** The button that opens the file picker: the camera roll, on a phone. */
export function AddFileButton({
  delivery,
  label,
  onPick,
}: {
  delivery: EvidenceDelivery
  label: string
  onPick: (file: File) => void
}) {
  const id = useId()
  const input = useRef<HTMLInputElement>(null)
  const refused = !delivery.uploadsAllowed

  return (
    <>
      <input
        ref={input}
        id={id}
        type="file"
        multiple
        accept={delivery.acceptedTypes.join(',')}
        disabled={refused}
        className="hidden"
        onChange={(e) => {
          for (const file of Array.from(e.target.files ?? [])) onPick(file)
          if (input.current) input.current.value = ''
        }}
      />
      <Button asChild size="sm" variant="outline" disabled={refused}>
        <label htmlFor={id} className={refused ? 'pointer-events-none opacity-50' : 'cursor-pointer'}>
          {label}
        </label>
      </Button>
    </>
  )
}
