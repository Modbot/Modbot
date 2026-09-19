import { StrictMode, type ReactNode } from 'react'
import { createRoot, hydrateRoot } from 'react-dom/client'

/**
 * Puts one page on screen. Every page's entry in src/entries calls this.
 *
 * The built page arrives already rendered, so the browser takes over the markup that is there; the
 * dev server's page does not, so it is drawn from nothing. Whether the privacy policy was built is
 * written onto the root by scripts/prerender.ts, so the footer here matches the rendered one.
 */
export function mount(page: (privacy: boolean) => ReactNode) {
  const root = document.getElementById('root')!
  const app = <StrictMode>{page(root.dataset.privacy === 'yes')}</StrictMode>

  if (root.firstElementChild) hydrateRoot(root, app)
  else createRoot(root).render(app)
}
