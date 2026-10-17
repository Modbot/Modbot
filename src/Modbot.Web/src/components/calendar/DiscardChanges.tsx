import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'

/**
 * Asked when an event form is closed with something typed that was not saved: the form stays open
 * under it, and Keep editing (also Escape) goes back to it. Mounted only while asking.
 */
export function DiscardChanges({ onKeep, onDiscard }: { onKeep: () => void; onDiscard: () => void }) {
  return (
    <Dialog open onOpenChange={(open) => !open && onKeep()}>
      <DialogContent
        title="Discard changes?"
        className="max-w-[460px]"
        foot={
          <DialogFoot>
            <Button size="sm" variant="outline" autoFocus onClick={onKeep}>
              Keep editing
            </Button>
            <Button size="sm" variant="destructive" onClick={onDiscard}>
              Discard
            </Button>
          </DialogFoot>
        }
      />
    </Dialog>
  )
}
