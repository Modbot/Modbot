/**
 * Light or dark. The page follows the system until somebody picks one, and remembers that pick in
 * this browser only. The inline script in every page applies it before first paint; keep the storage
 * key in step with it.
 *
 * Nothing here imports React, so the pages that carry no React (self-host, about, license and the
 * privacy policy) can still work their button without downloading it.
 */
export const THEME_KEY = 'modbot-theme'

export const THEME_BUTTON = 'data-theme-button'

export type Theme = 'light' | 'dark'

export const read = (): Theme => (document.documentElement.classList.contains('dark') ? 'dark' : 'light')

export function subscribe(onChange: () => void) {
  const observer = new MutationObserver(onChange)
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })
  return () => observer.disconnect()
}

export function toggle() {
  const next: Theme = read() === 'dark' ? 'light' : 'dark'
  document.documentElement.classList.toggle('dark', next === 'dark')
  try {
    localStorage.setItem(THEME_KEY, next)
  } catch {
    // Storage refused (a private window, blocked site data): the choice lasts for this visit.
  }
}

/**
 * The same button on the pages that carry no React. It is already on screen from the build, so this
 * only gives it its click and the label a screen reader reads.
 */
export function wireThemeButton() {
  const button = document.querySelector<HTMLButtonElement>(`[${THEME_BUTTON}]`)
  if (!button) return

  const label = () => (read() === 'dark' ? 'Switch to light theme' : 'Switch to dark theme')

  button.setAttribute('aria-label', label())
  button.addEventListener('click', () => {
    toggle()
    button.setAttribute('aria-label', label())
  })
}
