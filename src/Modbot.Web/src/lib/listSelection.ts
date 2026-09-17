import { useCallback, useEffect, useState } from 'react'
import { useShortcuts } from '@/lib/shortcuts'

/**
 * A moving selection over a list of rows, driven from the keyboard.
 *
 * `j` and `↓` move down, `k` and `↑` move up, `Enter` opens the selected row and `Esc` drops the
 * selection. The keys are page keys: they wait while a popup is open, because the list is behind
 * it. Nothing is selected until a key is pressed, so a page that was only ever used with the
 * mouse looks exactly as it did.
 *
 * The rows are found by `data-row-index`, which the caller writes on each row, so the selected
 * one can be scrolled into view without the hook holding a ref per row.
 */
export function useListSelection(
  count: number,
  open: (index: number) => void,
): {
  selected: number | null
  setSelected: (index: number | null) => void
  /** The attributes a row needs: its index for scrolling, and its selected state for styling. */
  rowProps: (index: number) => { 'data-row-index': number; 'data-selected': boolean | undefined; 'aria-selected': boolean }
} {
  const [chosen, setSelected] = useState<number | null>(null)

  // A list that shrank under the selection points at its last row rather than past the end.
  const selected = chosen === null ? null : count === 0 ? null : Math.min(chosen, count - 1)

  useEffect(() => {
    if (selected === null) return
    document
      .querySelector(`[data-row-index="${selected}"]`)
      ?.scrollIntoView({ block: 'nearest' })
  }, [selected])

  const move = (by: number) => {
    if (count === 0) return
    setSelected(selected === null ? (by > 0 ? 0 : count - 1) : Math.min(count - 1, Math.max(0, selected + by)))
  }

  useShortcuts([
    { keys: 'j', label: 'Next row', group: 'Lists', page: true, run: () => move(1) },
    { keys: 'arrowdown', label: 'Next row', group: 'Lists', page: true, hidden: true, run: () => move(1) },
    { keys: 'k', label: 'Previous row', group: 'Lists', page: true, run: () => move(-1) },
    { keys: 'arrowup', label: 'Previous row', group: 'Lists', page: true, hidden: true, run: () => move(-1) },
    {
      keys: 'enter',
      label: 'Open the selected row',
      group: 'Lists',
      page: true,
      run: () => {
        if (selected !== null) open(selected)
      },
    },
    {
      keys: 'escape',
      label: 'Drop the selection',
      group: 'Lists',
      page: true,
      hidden: true,
      run: () => setSelected(null),
    },
  ])

  const rowProps = useCallback(
    (index: number) => ({
      'data-row-index': index,
      'data-selected': selected === index ? true : undefined,
      'aria-selected': selected === index,
    }),
    [selected],
  )

  return { selected, setSelected, rowProps }
}
