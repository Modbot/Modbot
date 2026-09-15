import type { ComponentProps, MouseEvent } from 'react'
import { go } from '@/lib/router'

/** A link inside the app. A modified click (new tab, new window) is left to the browser. */
export function Link({ href, onClick, ...props }: ComponentProps<'a'> & { href: string }) {
  const handle = (e: MouseEvent<HTMLAnchorElement>) => {
    onClick?.(e)
    if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return
    e.preventDefault()
    go(href)
  }

  return <a href={href} onClick={handle} {...props} />
}
