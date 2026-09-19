import { useSyncExternalStore } from 'react'
import { read, subscribe, toggle, type Theme } from '@/lib/theme'

// Kept apart from lib/theme.ts so the pages that carry no React never import React through it.

/** Null while rendering on the server, which cannot know the visitor's theme. */
export function useTheme(): [Theme | null, () => void] {
  const theme = useSyncExternalStore<Theme | null>(subscribe, read, () => null)
  return [theme, toggle]
}
