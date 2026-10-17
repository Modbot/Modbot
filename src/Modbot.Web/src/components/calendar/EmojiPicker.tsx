import { lazy, Suspense, useState, type ReactNode } from 'react'
import { Popover } from 'radix-ui'
import { PopoverPortal } from '@/components/ui/dialog-portal'

// Loaded when first opened: the picker carries every emoji's data.
const Picker = lazy(() => import('emoji-picker-react'))

/** A button that opens the full emoji picker; a pick goes to `onPick`. The trigger is `children`. */
export function EmojiPicker({ onPick, children }: { onPick: (emoji: string) => void; children: ReactNode }) {
  const [open, setOpen] = useState(false)

  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      <Popover.Trigger asChild>{children}</Popover.Trigger>
      <PopoverPortal>
        <Popover.Content
          align="start"
          sideOffset={4}
          // The caret stays in the box it was in, so the emoji goes in there.
          onOpenAutoFocus={(e) => e.preventDefault()}
          onCloseAutoFocus={(e) => e.preventDefault()}
          className="z-50 rounded-sm border border-(length:--hairline) bg-popover shadow-sm"
        >
          <Suspense fallback={<div className="h-[380px] w-[320px]" />}>
            <Picker
              cssLayer="epr"
              className="emoji-picker"
              width={320}
              height={380}
              previewConfig={{ showPreview: false }}
              onEmojiClick={(emoji) => {
                setOpen(false)
                onPick(emoji.emoji)
              }}
            />
          </Suspense>
        </Popover.Content>
      </PopoverPortal>
    </Popover.Root>
  )
}
