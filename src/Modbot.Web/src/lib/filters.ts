import { useCallback, useEffect, useMemo } from 'react'
import { useLocation } from './router.ts'

/**
 * Filters the way Linear does them (research 2026-09-16): add a filter, pick a property, pick
 * an operator, pick values. One chip per property; chips combine with AND; the values inside a
 * chip are "any of". The chips live in the address, so a link reproduces the view, and the last
 * set used on a page is remembered in the browser so the page opens the way it was left.
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
  /** A word or phrase, matched anywhere. */
  | 'text'
  /** A day, or a stretch of days. */
  | 'date'
  /** Yes or no. */
  | 'yesno'

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

export type FilterOption = {
  value: string
  label: string
  /** How many rows the value would show, where the server knows. */
  count?: number | null
  /** A swatch beside the label, as a CSS colour. */
  color?: string | null
}

export type FilterProperty = {
  id: string
  label: string
  kind: FilterKind
  /** Choice: the values to pick from. */
  options?: FilterOption[]
  /** Choice: whether more than one value may be picked. Default true. */
  multi?: boolean
  /** Choice: whether "is not" is offered. Default true. */
  negatable?: boolean
  /** Choice: whether a typed value not in the list may be used, for an id the list does not carry. */
  freeText?: boolean
  /** Id and text: the hint in the box. */
  placeholder?: string
}

export type FilterChip = { property: string; operator: FilterOperator; values: string[] }

/** The operators a property of this kind can take, in the order a menu offers them. */
export function operatorsFor(property: FilterProperty): FilterOperator[] {
  switch (property.kind) {
    case 'choice':
      return property.negatable === false ? ['is'] : ['is', 'is-not']
    case 'id':
      return ['is']
    case 'text':
      return ['contains']
    case 'date':
      return ['after', 'before', 'between']
    case 'yesno':
      return ['yes', 'no']
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
  }
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

// ── Remembered per page ─────────────────────────────────────────────────────────────────────

const STORE = 'modbot.filters.'

export function rememberChips(page: string, chips: FilterChip[]): void {
  try {
    localStorage.setItem(STORE + page, JSON.stringify(chips))
  } catch {
    // A blocked store forgets; nothing else changes.
  }
}

export function recallChips(page: string): FilterChip[] | null {
  try {
    const raw = localStorage.getItem(STORE + page)
    if (!raw) return null
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return null
    return parsed.filter(
      (c): c is FilterChip =>
        typeof c === 'object' && c !== null && typeof c.property === 'string' && Array.isArray(c.values),
    )
  } catch {
    return null
  }
}

/**
 * The chips a page shows, and how to change them.
 *
 * The address wins; then what the browser remembers for this page; then the page's defaults.
 * Whichever it was, the answer is written into the address at once, so what a moderator copies
 * out is the view they are looking at and not a link that opens differently for somebody else.
 */
export function useFilters(page: string, defaults: FilterChip[]): [FilterChip[], (next: FilterChip[]) => void] {
  const [location, navigate] = useLocation()

  const fromAddress = useMemo(() => readChips(location.search), [location.search])

  const chips = useMemo(() => fromAddress ?? recallChips(page) ?? defaults, [fromAddress, page, defaults])

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
      rememberChips(page, next)
      navigate(`${window.location.pathname}?${params.toString()}`, { replace: true })
    },
    [page, navigate],
  )

  return [chips, set]
}

// ── Reading chips back out ──────────────────────────────────────────────────────────────────

/** The chip for one property, if any. */
export function chipFor(chips: FilterChip[], property: string): FilterChip | undefined {
  return chips.find((c) => c.property === property)
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
