import { useSyncExternalStore } from 'react'

/**
 * Light or dark. The page follows the system until somebody picks one, and remembers that pick in
 * this browser only. The inline script in index.html applies it before first paint; keep the
 * storage key in step with it.
 */
export const THEME_KEY = 'modbot-theme'

export type Theme = 'light' | 'dark'

const read = (): Theme => (document.documentElement.classList.contains('dark') ? 'dark' : 'light')

function subscribe(onChange: () => void) {
  const observer = new MutationObserver(onChange)
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })
  return () => observer.disconnect()
}

function toggle() {
  const next: Theme = read() === 'dark' ? 'light' : 'dark'
  document.documentElement.classList.toggle('dark', next === 'dark')
  try {
    localStorage.setItem(THEME_KEY, next)
  } catch {
    // Storage refused (a private window, blocked site data): the choice lasts for this visit.
  }
}

/** Null while rendering on the server, which cannot know the visitor's theme. */
export function useTheme(): [Theme | null, () => void] {
  const theme = useSyncExternalStore<Theme | null>(subscribe, read, () => null)
  return [theme, toggle]
}
