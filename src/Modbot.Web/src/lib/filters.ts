import { useCallback, useEffect, useMemo } from 'react'
import { useLocation } from './router.ts'

/**
 * Filters the way Linear does them (research 2026-09-16): add a filter, pick a property, pick
 * an operator, pick values. One chip per property; chips combine with AND; the values inside a
 * chip are "any of". The chips live in the address, so a link reproduces the view, and Back
 * brings back the set a page was left with.
 *
 * The pure parts -- what a chip is, how it is written into the address and read back -- are
 * here with no DOM, so they can be tested with Node alone. The bar that draws them is
 * `components/filters/FilterBar.tsx`.
 */

/** What kind of values a property takes, which decides its operators and its editor. */
export type FilterKind =
  /** One or more of a fixed list: a source, a role, a type. */
  | 'choice'
  /** One id, matched exactly. */
  | 'id'
  /** One id, found by name: a person, a world. A pasted id is taken as it is. */
  | 'search'
  /** A word or phrase, matched anywhere. */
  | 'text'
  /** A day, or a stretch of days. */
  | 'date'
  /** Yes or no. */
  | 'yesno'
  /** One moment: a day and a time, held as an ISO instant. */
  | 'moment'

export type FilterOperator =
  /** Choice: any of the values. Id: exactly this. */
  | 'is'
  /** Choice: none of the values. */
  | 'is-not'
  /** Text: contains this. */
  | 'contains'
  /** Date: on or after the day. */
  | 'after'
  /** Date: before the day. */
  | 'before'
  /** Date: from the first day up to the second, both included. */
  | 'between'
  | 'yes'
  | 'no'
  /** Moment: at this instant. The chip's property says the rest ("Here at"). */
  | 'at'

export type FilterOption = {
  value: string
  label: string
  /** How many rows the value would show, where the server knows. */
  count?: number | null
  /** A swatch beside the label, as a CSS colour. */
  color?: string | null
  /** A second, quieter line beside the label: the id under a name. */
  detail?: string | null
}

export type FilterProperty = {
  id: string
  label: string
  kind: FilterKind
  /** Whether the chip may be added again, each one narrowing further. Default false: one chip per property. */
  repeatable?: boolean
  /** Choice: the values to pick from. */
  options?: FilterOption[]
  /** Choice: whether more than one value may be picked. Default true. */
  multi?: boolean
  /** Choice: whether "is not" is offered. Default true. */
  negatable?: boolean
  /** Choice: whether a typed value not in the list may be used, for an id the list does not carry. */
  freeText?: boolean
  /** Id, search and text: the hint in the box. */
  placeholder?: string
  /**
   * Search: what the typed words find, as ids with names. A chip holds the id; its name is shown
   * from `options`, which the page keeps filled with the names it has learned.
   */
  search?: (words: string) => Promise<FilterOption[]>
  /** Search: told the name of an id picked from the results, so the chip can show it. */
  onPick?: (option: FilterOption) => void
}

export type FilterChip = { property: string; operator: FilterOperator; values: string[] }

/** The operators a property of this kind can take, in the order a menu offers them. */
export function operatorsFor(property: FilterProperty): FilterOperator[] {
  switch (property.kind) {
    case 'choice':
      return property.negatable === false ? ['is'] : ['is', 'is-not']
    case 'id':
    case 'search':
      return ['is']
    case 'text':
      return ['contains']
    case 'date':
      return ['after', 'before', 'between']
    case 'yesno':
      return ['yes', 'no']
    case 'moment':
      return ['at']
  }
}

/** The operator as a person reads it inside a chip. */
export function operatorWords(operator: FilterOperator, valueCount: number): string {
  switch (operator) {
    case 'is':
      return valueCount > 1 ? 'is any of' : 'is'
    case 'is-not':
      return valueCount > 1 ? 'is none of' : 'is not'
    case 'contains':
      return 'contains'
    case 'after':
      return 'on or after'
    case 'before':
      return 'before'
    case 'between':
      return 'between'
    case 'yes':
      return 'yes'
    case 'no':
      return 'no'
    case 'at':
      return ''
  }
}

/**
 * A moment as the value of a `datetime-local` box: the viewer's own clock to the minute, which is
 * what they will read the chip in. Empty for a value that is not an instant.
 */
export function toLocalInput(iso: string): string {
  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return ''
  const two = (n: number) => String(n).padStart(2, '0')
  return `${at.getFullYear()}-${two(at.getMonth() + 1)}-${two(at.getDate())}T${two(at.getHours())}:${two(at.getMinutes())}`
}

/** What a `datetime-local` box holds, back as an ISO instant; null for an empty or unreadable box. */
export function fromLocalInput(text: string): string | null {
  const at = new Date(text)
  return text && !Number.isNaN(at.getTime()) ? at.toISOString() : null
}

// ── The address ─────────────────────────────────────────────────────────────────────────────

/** The query parameter, repeated once per chip. */
export const PARAM = 'f'

/**
 * One chip as text: `property:operator:value,value`. Each value is percent-encoded on its own
 * before the join, so a comma inside a value (legacy ids are arbitrary text, spec 3.1.1) is
 * `%2C` and never a separator.
 */
export function encodeChip(chip: FilterChip): string {
  return `${chip.property}:${chip.operator}:${chip.values.map(encodeURIComponent).join(',')}`
}

export function decodeChip(text: string): FilterChip | null {
  const first = text.indexOf(':')
  const second = first < 0 ? -1 : text.indexOf(':', first + 1)
  if (first <= 0 || second < 0) return null

  const property = text.slice(0, first)
  const operator = text.slice(first + 1, second) as FilterOperator
  const rest = text.slice(second + 1)
  const values = rest === '' ? [] : rest.split(',').map(decodeURIComponent)

  return { property, operator, values }
}

/** The chips written into a query string. An empty list is written as one empty `f`, so "none" is distinguishable from "not said". */
export function writeChips(params: URLSearchParams, chips: FilterChip[]): void {
  params.delete(PARAM)
  if (chips.length === 0) params.append(PARAM, '')
  for (const chip of chips) params.append(PARAM, encodeChip(chip))
}

/** The chips a query string carries, or null when it says nothing about filters at all. */
export function readChips(params: URLSearchParams): FilterChip[] | null {
  const raw = params.getAll(PARAM)
  if (raw.length === 0) return null

  return raw.map(decodeChip).filter((c): c is FilterChip => c !== null)
}

/** True when the two lists say the same thing, in any order. */
export function sameChips(a: FilterChip[], b: FilterChip[]): boolean {
  if (a.length !== b.length) return false
  const key = (c: FilterChip) => encodeChip({ ...c, values: [...c.values].sort() })
  const left = a.map(key).sort()
  const right = b.map(key).sort()
  return left.every((k, i) => k === right[i])
}

// ── The page's start ────────────────────────────────────────────────────────────────────────

/**
 * Whether the chips are where the page starts. A page can have more than one start (People opens
 * bare, and as Members; the audit log opened at one entry opens with no chips), and chips equal to
 * any of them are at the start. Clear shows whenever they are not.
 */
export function atStart(chips: FilterChip[], starts: FilterChip[][]): boolean {
  return starts.some((start) => sameChips(chips, start))
}

/**
 * Whether the page's count should say "filtered": the chips are not where the page starts, and
 * there are some. No chips at all is never filtered, though it is not the start of the audit log
 * or the Discord list: it shows more than the start does, not less.
 */
export function isFiltered(chips: FilterChip[], starts: FilterChip[][]): boolean {
  return chips.length > 0 && !atStart(chips, starts)
}

/**
 * The chips a page shows, and how to change them.
 *
 * The address wins; then the page's defaults. Whichever it was, the answer is written into the
 * address at once, so what a moderator copies out is the view they are looking at and not a link
 * that opens differently for somebody else.
 *
 * *2026-09-28:* the last set used on a page is no longer remembered in the browser. The sidebar
 * opened a page already narrowed by filters from an earlier visit, and a count of twelve read as
 * a log with twelve entries in it. The address still carries the chips, so Back brings them back.
 */
export function useFilters(
  defaults: FilterChip[],
  /**
   * What an address with no filters in it opens with, in place of the defaults. A link to one
   * entry passes none at all: opened with the default filters, the entry it names could be
   * filtered out of the very page it opens.
   */
  linked?: FilterChip[],
): [FilterChip[], (next: FilterChip[]) => void] {
  const [location, navigate] = useLocation()

  const fromAddress = useMemo(() => readChips(location.search), [location.search])

  const chips = useMemo(() => fromAddress ?? linked ?? defaults, [fromAddress, linked, defaults])

  useEffect(() => {
    if (fromAddress !== null) return
    const params = new URLSearchParams(window.location.search)
    writeChips(params, chips)
    navigate(`${window.location.pathname}?${params.toString()}`, { replace: true })
  }, [fromAddress, chips, navigate])

  const set = useCallback(
    (next: FilterChip[]) => {
      const params = new URLSearchParams(window.location.search)
      writeChips(params, next)
      navigate(`${window.location.pathname}?${params.toString()}`, { replace: true })
    },
    [navigate],
  )

  return [chips, set]
}

// ── Reading chips back out ──────────────────────────────────────────────────────────────────

/** The chip for one property, if any. */
export function chipFor(chips: FilterChip[], property: string): FilterChip | undefined {
  return chips.find((c) => c.property === property)
}

/** The instants of every chip of a repeatable moment property, in the order they were added; values that are not instants are left out. */
export function moments(chips: FilterChip[], property: string): string[] {
  return chips
    .filter((c) => c.property === property)
    .flatMap((c) => c.values.slice(0, 1))
    .filter((v) => !Number.isNaN(Date.parse(v)))
    .map((v) => new Date(v).toISOString())
}

/** A yes/no chip as a boolean, or undefined when the property is not filtered. */
export function yesNo(chips: FilterChip[], property: string): boolean | undefined {
  const chip = chipFor(chips, property)
  if (!chip) return undefined
  return chip.operator === 'yes'
}

/** A date chip as a half-open stretch, as ISO instants: from the start of the first day to the start of the day after the last. */
export function dateRange(chips: FilterChip[], property: string): { from?: string; to?: string } {
  const chip = chipFor(chips, property)
  if (!chip) return {}

  const startOf = (day: string) => `${day}T00:00:00Z`
  const dayAfter = (day: string) => new Date(Date.parse(startOf(day)) + 86_400_000).toISOString()

  const [a, b] = chip.values
  switch (chip.operator) {
    case 'after':
      return a ? { from: startOf(a) } : {}
    case 'before':
      return a ? { to: startOf(a) } : {}
    case 'between':
      return { ...(a ? { from: startOf(a) } : {}), ...(b ? { to: dayAfter(b) } : {}) }
    default:
      return {}
  }
}
