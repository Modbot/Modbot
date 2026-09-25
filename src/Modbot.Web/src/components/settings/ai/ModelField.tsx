import { useEffect, useMemo, useRef, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { Chip } from '@/components/ui/chip'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import {
  api,
  ApiError,
  type AiCatalog,
  type AiCatalogModel,
  type AiConnectionInput,
  type AiModelFeature,
} from '@/lib/api'
import {
  chipsOf,
  contextText,
  filterModels,
  makersOf,
  noFilters,
  priceText,
  sortModels,
  type ModelFilters,
  type ModelSort,
} from '@/lib/aiCatalog'
import { ago } from '@/lib/format'
import { cn } from '@/lib/utils'
import { Outcome } from '../fields'

const failure = (e: unknown) =>
  e instanceof ApiError && e.status === 403
    ? 'You do not have permission to change AI settings.'
    : e instanceof ApiError
      ? e.message
      : 'Could not reach the Modbot server.'

/**
 * A model box with a picker beside it: OpenRouter's whole list with prices, what each model can do
 * and what a thousand calls would cost here (AI chat design §10.9); any other provider's plain
 * model list.
 *
 * The typed value is always kept as it is, so a model the provider does not list can still be set.
 */
export function ModelField({
  label = 'Model',
  feature,
  name = feature,
  value,
  placeholder,
  provider,
  endpoint,
  apiKey,
  onChange,
}: {
  label?: string
  feature: AiModelFeature
  /** Distinguishes two boxes for the same feature, such as the model and its fallback. */
  name?: string
  value: string
  placeholder?: string
  /** The values on the form, for Base. Left out elsewhere: the saved connection is used. */
  provider?: string
  endpoint?: string
  apiKey?: string
  onChange: (model: string) => void
}) {
  const [open, setOpen] = useState(false)

  return (
    <div className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground" id={`${name}-model-label`}>
        {label}
      </span>
      <div className="flex items-center gap-2">
        <Input
          aria-labelledby={`${name}-model-label`}
          value={value}
          placeholder={placeholder}
          autoComplete="off"
          onChange={(e) => onChange(e.target.value)}
        />
        <Button size="sm" variant="outline" type="button" onClick={() => setOpen(true)}>
          Choose
        </Button>
      </div>

      <Dialog open={open} onOpenChange={setOpen}>
        {open && (
          <ModelPicker
            feature={feature}
            value={value.trim()}
            provider={provider}
            endpoint={endpoint}
            apiKey={apiKey}
            onPick={(model) => {
              onChange(model)
              setOpen(false)
            }}
          />
        )}
      </Dialog>
    </div>
  )
}

function ModelPicker({
  feature,
  value,
  provider,
  endpoint,
  apiKey,
  onPick,
}: {
  feature: AiModelFeature
  value: string
  provider?: string
  endpoint?: string
  apiKey?: string
  onPick: (model: string) => void
}) {
  const [chosen, setChosen] = useState(provider ?? null)
  const [catalog, setCatalog] = useState<AiCatalog | null>(null)
  const [listed, setListed] = useState<string[] | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const [filters, setFilters] = useState<ModelFilters>(noFilters)
  const [sort, setSort] = useState<ModelSort>('best')
  const [descending, setDescending] = useState(false)

  const set = (patch: Partial<ModelFilters>) => setFilters((all) => ({ ...all, ...patch }))

  // Fetched at most once per picker: a deployment that has just chosen OpenRouter has nothing
  // stored yet, and a fetch that fails is not tried again.
  const fetchedOnce = useRef(false)

  useEffect(() => {
    let open = true

    const connectionOf = async (): Promise<AiConnectionInput> => {
      // Chat and Insights have no connection of their own; they use the saved one.
      if (provider !== undefined) return { provider, endpoint: endpoint ?? '', ...(apiKey ? { apiKey } : {}) }

      const settings = await api.aiSettings()
      return { provider: settings.provider, endpoint: settings.endpoint ?? '' }
    }

    const load = async () => {
      try {
        const connection = await connectionOf()
        if (!open) return
        setChosen(connection.provider)

        if (connection.provider !== 'openrouter') {
          const [list, models] = await Promise.all([api.aiCatalog(feature), api.aiModels(connection)])
          if (!open) return
          setCatalog(list)
          setListed(models.models)
          setProblem(models.error)
          return
        }

        let list = await api.aiCatalog(feature)

        if (list.fetchedAt === null && !fetchedOnce.current) {
          fetchedOnce.current = true
          await api.fetchAiPrices()
          list = await api.aiCatalog(feature)
        }

        if (open) setCatalog(list)
      } catch (e: unknown) {
        if (open) setProblem(failure(e))
      }
    }

    void load()
    return () => {
      open = false
    }
  }, [feature, provider, endpoint, apiKey])

  const openRouter = chosen === 'openrouter'

  const refresh = () => {
    setBusy(true)
    setProblem(null)

    api
      .fetchAiPrices()
      .then(() => api.aiCatalog(feature))
      .then(setCatalog)
      .catch((e: unknown) => setProblem(failure(e)))
      .finally(() => setBusy(false))
  }

  const models = useMemo(
    () => sortModels(filterModels(catalog?.models ?? [], filters), sort, descending),
    [catalog, filters, sort, descending],
  )

  const makers = useMemo(() => makersOf(catalog?.models ?? []), [catalog])
  const costs = Boolean(catalog?.average)
  const unlisted = value !== '' && !(catalog?.models ?? []).some((m) => m.id === value)

  // Up, then down, then back to the recommended order the list arrived in.
  const by = (next: ModelSort) => {
    if (sort !== next) {
      setSort(next)
      setDescending(false)
      return
    }

    setSort(descending ? 'best' : next)
    setDescending(!descending)
  }

  const head = (text: string, next: ModelSort, className?: string) => (
    <Th className={className}>
      <button type="button" className="hover:text-foreground" onClick={() => by(next)}>
        {text}
        {sort === next && (descending ? ' ↓' : ' ↑')}
      </button>
    </Th>
  )

  const loading = chosen === null || (openRouter ? catalog === null : listed === null)

  return (
    <DialogContent
      title="Model"
      className="max-w-[980px]"
      bodyClassName="flex max-h-[76vh] flex-col overflow-hidden p-0"
      actions={
        openRouter && (
          <div className="flex shrink-0 items-center gap-2">
            <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {catalog?.fetchedAt ? `Fetched ${ago(catalog.fetchedAt, catalog.now)}` : 'Not fetched'}
            </span>
            <Button size="sm" variant="outline" disabled={busy} onClick={refresh}>
              {busy ? 'Refreshing…' : 'Refresh'}
            </Button>
          </div>
        )
      }
    >
      {/* With nothing to list, a failure stands in for the list; over a list, it is said above it. */}
      {problem && !loading && (
        <div className="px-4 pt-3">
          <Outcome tone="problem">{problem}</Outcome>
        </div>
      )}

      {loading ? (
        <EmptyRow className="px-4" tone={problem ? 'danger' : 'neutral'}>
          {problem ?? 'Loading…'}
        </EmptyRow>
      ) : openRouter ? (
        <>
          <div
            className="flex shrink-0 flex-wrap items-center gap-2 border-b border-b-(length:--hairline) px-4 py-3"
          >
            <Input
              aria-label="Search"
              className="w-full sm:max-w-xs"
              placeholder="Search"
              value={filters.search}
              onChange={(e) => set({ search: e.target.value })}
            />
            <Select
              aria-label="Maker"
              value={filters.maker}
              onChange={(maker) => set({ maker })}
            >
              <option value="">All makers</option>
              {makers.map((m) => (
                <option key={m} value={m}>
                  {m}
                </option>
              ))}
            </Select>
            <Chip on={filters.tools} onClick={() => set({ tools: !filters.tools })}>
              Tools
            </Chip>
            <Chip
              on={filters.structuredOutput}
              onClick={() => set({ structuredOutput: !filters.structuredOutput })}
            >
              Structured output
            </Chip>
            <Chip on={filters.free} onClick={() => set({ free: !filters.free })}>
              Free
            </Chip>
            <Input
              aria-label="Max price"
              type="number"
              inputMode="decimal"
              min={0}
              step="any"
              className="w-28"
              placeholder="Max $/1M"
              value={filters.maxPrice}
              onChange={(e) => set({ maxPrice: e.target.value })}
            />
          </div>

          <div className="min-h-0 flex-1 overflow-y-auto">
            <Table
              head={
                <>
                  {head('Model', 'name')}
                  <Th className="hidden md:table-cell">Maker</Th>
                  {head('Input $/1M', 'input', 'text-right')}
                  {head('Output $/1M', 'output', 'text-right')}
                  <Th className="hidden text-right lg:table-cell">Cached $/1M</Th>
                  {head('Context', 'context', 'hidden text-right sm:table-cell')}
                  {costs && head('Per 1,000 calls', 'cost', 'text-right')}
                </>
              }
            >
              {unlisted && (
                <Tr aria-selected className="bg-accent/40">
                  <Td colSpan={costs ? 7 : 6}>
                    <span className="font-mono">{value}</span>
                    <Badge variant="outline" className="ml-2">
                      Not in list
                    </Badge>
                  </Td>
                </Tr>
              )}
              {models.map((m) => (
                <Row key={m.id} model={m} chosen={m.id === value} costs={costs} onPick={onPick} />
              ))}
            </Table>
          </div>
        </>
      ) : (
        <div className="min-h-0 flex-1 overflow-y-auto">
          <Table
            head={
              <>
                <Th>Model</Th>
                <Th className="text-right">Input $/1M</Th>
                <Th className="text-right">Output $/1M</Th>
              </>
            }
          >
            {(listed ?? []).map((id) => {
              const price = catalog?.prices.find((p) => p.model === id) ?? null

              return (
                <Tr
                  key={id}
                  aria-selected={id === value}
                  className={cn('cursor-pointer hover:bg-muted/40', id === value && 'bg-accent/40')}
                  onClick={() => onPick(id)}
                >
                  <Td className="font-mono">
                    <button type="button" className="text-left" onClick={() => onPick(id)}>
                      {id}
                    </button>
                  </Td>
                  <Td className="text-right font-mono" colSpan={price ? 1 : 2}>
                    {price ? priceText(price.inputPerMillion) : 'No price'}
                  </Td>
                  {price && <Td className="text-right font-mono">{priceText(price.outputPerMillion)}</Td>}
                </Tr>
              )
            })}
          </Table>
        </div>
      )}
    </DialogContent>
  )
}

function Row({
  model,
  chosen,
  costs,
  onPick,
}: {
  model: AiCatalogModel
  chosen: boolean
  costs: boolean
  onPick: (model: string) => void
}) {
  return (
    <Tr
      aria-selected={chosen}
      className={cn('cursor-pointer hover:bg-muted/40', chosen && 'bg-accent/40')}
      onClick={() => onPick(model.id)}
    >
      <Td className="max-w-[22rem] whitespace-normal">
        <button type="button" className="text-left font-medium" onClick={() => onPick(model.id)}>
          {model.name ?? model.id}
        </button>
        <div className="truncate font-mono text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
          {model.id}
        </div>
        <div className="mt-1 flex flex-wrap gap-1">
          {chipsOf(model).map((chip) => (
            <Badge
              key={chip.text}
              variant={chip.warn ? 'outline' : 'secondary'}
              className={cn(chip.warn && 'text-warn border-current')}
            >
              {chip.text}
            </Badge>
          ))}
        </div>
      </Td>
      <Td className="hidden text-muted-foreground md:table-cell">{model.maker}</Td>
      <Td className="text-right font-mono">{priceText(model.inputPerMillion)}</Td>
      <Td className="text-right font-mono">{priceText(model.outputPerMillion)}</Td>
      <Td className="hidden text-right font-mono lg:table-cell">{priceText(model.cachedInputPerMillion)}</Td>
      <Td className="hidden text-right font-mono sm:table-cell">{contextText(model.contextLength)}</Td>
      {costs && <Td className="text-right font-mono">{priceText(model.costPerThousandCalls)}</Td>}
    </Tr>
  )
}
