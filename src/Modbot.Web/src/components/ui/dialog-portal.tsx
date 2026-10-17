import * as React from 'react'
import { DropdownMenu, Popover } from 'radix-ui'
import { DialogElementContext } from './dialog-element'

/** `Popover.Portal`, drawn inside the open dialog if there is one (see `dialog-element.ts`). */
export function PopoverPortal(props: React.ComponentProps<typeof Popover.Portal>) {
  const dialog = React.useContext(DialogElementContext)
  return <Popover.Portal container={dialog} {...props} />
}

/** `DropdownMenu.Portal`, drawn inside the open dialog if there is one. */
export function MenuPortal(props: React.ComponentProps<typeof DropdownMenu.Portal>) {
  const dialog = React.useContext(DialogElementContext)
  return <DropdownMenu.Portal container={dialog} {...props} />
}
