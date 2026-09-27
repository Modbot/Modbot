import { useLayoutEffect, useState } from 'react'

/** How tightly a desk packs its rows. A headset has its own sizes and a phone picks its own. */
export type Density = 'dense' | 'comfortable'

/** Where Modbot is being read. A phone is picked out by the screen itself, so it is not a choice here. */
export type Place = 'desk' | 'headset'

export type Theme = 'dark' | 'light'

const KEY = 'modbot.prefs'

interface Stored {
  /** `vr` is from before the headset was a place of its own, and reads as Headset. */
  density?: Density | 'vr'
  place?: Place
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
 * Where you are, how dense a desk is, and the theme, persisted per browser.
 *
 * The headset is a place rather than a third density (UX review 2026-09-25, findings 16 and 17).
 * It changes the layout as well as the sizes: one column, no sidebar, no keyboard hints, and Live
 * as the page it opens on. A headset shows the desktop through a virtual panel at low effective
 * pixels-per-degree, and a laser pointer is far less precise than a mouse -- so it also changes
 * hit targets, type size, border weight and contrast together. See index.css.
 *
 * Dense and Comfortable apply at a desk only. They are a setting people choose once, so they live
 * in Your account rather than in the top bar.
 *
 * The stored values are read before the first render and applied before the first paint, so the
 * page never draws the desk and then jumps to the headset.
 */
export function usePreferences() {
  const [stored] = useState(readStored)
  const [place, setPlace] = useState<Place>(() => stored.place ?? (stored.density === 'vr' ? 'headset' : 'desk'))
  const [density, setDensity] = useState<Density>(() => (stored.density === 'comfortable' ? 'comfortable' : 'dense'))
  const [theme, setTheme] = useState<Theme>(() => stored.theme || 'dark')

  useLayoutEffect(() => {
    const root = document.documentElement
    root.dataset.place = place
    root.dataset.density = density
    root.classList.toggle('dark', theme === 'dark')
    try {
      localStorage.setItem(KEY, JSON.stringify({ place, density, theme }))
    } catch { /* see readStored */ }
  }, [place, density, theme])

  return { place, setPlace, density, setDensity, theme, setTheme }
}
