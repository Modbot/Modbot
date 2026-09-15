import type { AiCatalogModel } from '@/lib/api'

/**
 * The model picker's own working out: how a price, a context length and a model's chips are
 * written, and how the list is filtered and sorted.
 *
 * The order the list arrives in is the recommended-first order the server worked out (AI chat
 * design §10.9); everything here only ever narrows or re-sorts it.
 */

const usd = new Intl.NumberFormat(undefined, {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 4,
})

/** A price per million tokens, or a dash when there is none. */
export const priceText = (n: number | null | undefined): string =>
  n === null || n === undefined ? '—' : usd.format(n)

/** 200000 → "200K", 1048576 → "1M". */
export function contextText(tokens: number | null | undefined): string {
  if (!tokens) return '—'
  if (tokens >= 1_000_000) return `${Math.round(tokens / 100_000) / 10}M`
  if (tokens >= 1000) return `${Math.round(tokens / 1000)}K`
  return String(tokens)
}

export type ModelChip = { text: string; warn: boolean }

/** What a row says about a model beside its name. */
export function chipsOf(model: AiCatalogModel): ModelChip[] {
  const chips: ModelChip[] = []

  if (model.missing.includes('tools')) chips.push({ text: 'No tools', warn: true })
  if (model.missing.includes('structuredOutput')) chips.push({ text: 'No structured output', warn: true })
  if (model.priceVaries) chips.push({ text: 'Price varies', warn: true })
  if (model.tools && !model.missing.includes('tools')) chips.push({ text: 'Tools', warn: false })
  if (model.structuredOutput && !model.missing.includes('structuredOutput'))
    chips.push({ text: 'Structured output', warn: false })
  if (model.imagesIn) chips.push({ text: 'Images in', warn: false })
  if (model.free) chips.push({ text: 'Free', warn: false })

  return chips
}

export type ModelFilters = {
  search: string
  maker: string
  tools: boolean
  structuredOutput: boolean
  free: boolean
  /** The most a model may cost per million input or output tokens. Empty for no ceiling. */
  maxPrice: string
}

export const noFilters: ModelFilters = {
  search: '',
  maker: '',
  tools: false,
  structuredOutput: false,
  free: false,
  maxPrice: '',
}

export function filterModels(models: AiCatalogModel[], filters: ModelFilters): AiCatalogModel[] {
  const search = filters.search.trim().toLowerCase()
  const max = filters.maxPrice.trim() === '' ? null : Number(filters.maxPrice)
  const ceiling = max === null || Number.isNaN(max) ? null : max

  return models.filter((m) => {
    if (search && !`${m.id} ${m.name ?? ''}`.toLowerCase().includes(search)) return false
    if (filters.maker && m.maker !== filters.maker) return false
    if (filters.tools && !m.tools) return false
    if (filters.structuredOutput && !m.structuredOutput) return false
    if (filters.free && !m.free) return false
    if (ceiling !== null) {
      if (m.inputPerMillion === null || m.outputPerMillion === null) return false
      if (m.inputPerMillion > ceiling || m.outputPerMillion > ceiling) return false
    }
    return true
  })
}

/** `best` is the order the server sent, which puts the models recommended for the feature first. */
export type ModelSort = 'best' | 'name' | 'input' | 'output' | 'context' | 'cost'

/** Sorts a filtered list. Models with nothing to sort by go last, whichever way round it is. */
export function sortModels(models: AiCatalogModel[], sort: ModelSort, descending: boolean): AiCatalogModel[] {
  if (sort === 'best') return models

  const rows = [...models]
  const way = descending ? -1 : 1

  const number = (m: AiCatalogModel): number | null =>
    sort === 'input'
      ? m.inputPerMillion
      : sort === 'output'
        ? m.outputPerMillion
        : sort === 'context'
          ? m.contextLength
          : m.costPerThousandCalls

  rows.sort((a, b) => {
    if (sort === 'name') return way * (a.name ?? a.id).localeCompare(b.name ?? b.id)

    const left = number(a)
    const right = number(b)
    if (left === null && right === null) return (a.name ?? a.id).localeCompare(b.name ?? b.id)
    if (left === null) return 1
    if (right === null) return -1
    return way * (left - right) || (a.name ?? a.id).localeCompare(b.name ?? b.id)
  })

  return rows
}

/** Every maker in the list, in alphabetical order. */
export const makersOf = (models: AiCatalogModel[]): string[] =>
  [...new Set(models.map((m) => m.maker).filter((m) => m !== ''))].sort((a, b) => a.localeCompare(b))
