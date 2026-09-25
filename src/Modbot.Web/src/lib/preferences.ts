import { useEffect, useState } from 'react'

export type Density = 'dense' | 'comfortable' | 'vr'
export type Theme = 'dark' | 'light'

const KEY = 'modbot.prefs'

interface Stored {
  density?: Density
  theme?: Theme
}

function readStored(): Stored {
  try {
    const raw = localStorage.getItem(KEY)
    if (raw) {
      const p = JSON.parse(raw)
      if (p && typeof p === 'object') return p as Stored
    }
  } catch {
    // A corrupt or blocked store is a default, never a crash. Private windows
    // and locked-down browsers both land here.
  }
  return {}
}

/**
 * Density and theme, persisted per browser.
 *
 * VR is a first-class density rather than a zoom level. A headset shows the
 * desktop through a virtual panel at low effective pixels-per-degree, and a
 * laser pointer is far less precise than a mouse -- so VR mode changes hit
 * targets, type size, border weight and contrast together. See index.css.
 *
 * The stored values are read before the first render, so the page never
 * paints in the default theme and then switches.
 */
export function usePreferences() {
  const [density, setDensity] = useState<Density>(() => readStored().density || 'dense')
  const [theme, setTheme] = useState<Theme>(() => readStored().theme || 'dark')

  useEffect(() => {
    const root = document.documentElement
    root.dataset.density = density
    root.classList.toggle('dark', theme === 'dark')
    try {
      localStorage.setItem(KEY, JSON.stringify({ density, theme }))
    } catch { /* see readStored */ }
  }, [density, theme])

  return { density, setDensity, theme, setTheme }
}
