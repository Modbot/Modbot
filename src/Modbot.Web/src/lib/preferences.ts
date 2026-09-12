import { useEffect, useState } from 'react'

export type Density = 'dense' | 'comfortable' | 'vr'
export type Theme = 'dark' | 'light'

const KEY = 'modbot.prefs'

/**
 * Density and theme, persisted per browser.
 *
 * VR is a first-class density rather than a zoom level. A headset shows the
 * desktop through a virtual panel at low effective pixels-per-degree, and a
 * laser pointer is far less precise than a mouse -- so VR mode changes hit
 * targets, type size, border weight and contrast together. See index.css.
 */
export function usePreferences() {
  const [density, setDensity] = useState<Density>('dense')
  const [theme, setTheme] = useState<Theme>('dark')

  useEffect(() => {
    try {
      const raw = localStorage.getItem(KEY)
      if (raw) {
        const p = JSON.parse(raw)
        if (p.density) setDensity(p.density)
        if (p.theme) setTheme(p.theme)
      }
    } catch {
      // A corrupt or blocked store is a default, never a crash. Private windows
      // and locked-down browsers both land here.
    }
  }, [])

  useEffect(() => {
    const root = document.documentElement
    root.dataset.density = density
    root.classList.toggle('dark', theme === 'dark')
    try {
      localStorage.setItem(KEY, JSON.stringify({ density, theme }))
    } catch { /* see above */ }
  }, [density, theme])

  return { density, setDensity, theme, setTheme }
}
