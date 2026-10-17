import { createContext } from 'react'

/**
 * The open dialog's own element, or null outside a dialog.
 *
 * A dialog locks scrolling for everything outside its element, and a menu drawn on the page body
 * is outside it: the mouse wheel and touch did nothing over the open list. Menus opened inside a
 * dialog are drawn inside it instead (`DialogContent` sets this, `dialog-portal.tsx` reads it).
 */
export const DialogElementContext = createContext<HTMLElement | null>(null)
