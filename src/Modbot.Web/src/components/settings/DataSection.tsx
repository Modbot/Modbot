import { useCallback, useEffect, useState } from 'react'
import { ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  api,
  ApiError,
  type CloudStatusView,
  type DataSettings,
  type LinkCodeView,
  type LogSettings,
  type PublicAddressView,
  type PublicInstancesView,
  type ServerSettings,
  type UpdateView,
} from '@/lib/api'
import { PanelGrid } from '@/components/PanelGrid'
import { ConfirmButton, Fact, Field, Hint, Outcome, Placeholder, Row, Switch } from './fields'
import { MachineUsageCard } from './MachineUsageCard'
import { shortensAny } from './retention'
import { SettingsCard, SettingsSection } from './SettingsCard'
import { StorageChart } from './StorageChart'
import { SyncTimings } from './SyncTimings'
import { GB, bytes, remember, remembered } from './units'
import { dateTime } from '@/components/charts/format'
import { lengthOfTime } from '@/lib/format'

/**
 * Server: what this install tells the world it is, how long it keeps things, and what it is.
 *
 * The settings an owner changes come first: the public address and how long records are kept. What
 * only whoever runs the machine reads (disk, processor, the sync timings) sits folded away below,
 * opened on request. It used to be eight panels in a fixed order, the storage chart first (settings
 * review 2026-09-27 §7).
 */
export function DataSection() {
  const [data, setData] = useState<DataSettings | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [cost, setCost] = useState(() => remembered('modbot.costPerGbMonth'))
  const [capacity, setCapacity] = useState(() => remembered('modbot.capacityGb'))

  // Only the disk size goes to the server, for the fill date. The cost is applied in the chart,
  // so typing a price does not refetch.
  const load = useCallback(() => {
    const budget = {
      capacityBytes: Number(capacity) ? Number(capacity) * GB : undefined,
    }
    return api
      .dataSettings(budget)
      .then((next) => {
        setData(next)
        setError(null)
      })
      .catch((e: unknown) =>
        setError(e instanceof ApiError ? e.message : 'Could not load settings.'),
      )
  }, [capacity])

  useEffect(() => void load(), [load])

  return (
    <div className="flex flex-col gap-4">
      <SettingsSection id="data" title="Server">
        {error ? (
          <Placeholder tone="danger" onTryAgain={load}>{error}</Placeholder>
        ) : !data ? (
          <Placeholder tone="loading" />
        ) : (
          <>
            <PublicAddressCard />
            <KeepForCard current={data.retention} onSaved={load} />
            <InstallCard deployment={data.deployment} />
          </>
        )}
      </SettingsSection>

      {data && (
        <Disclosure label="Storage and machine usage">
          <PanelGrid className="grid-cols-12">
            <StorageCard
              storage={data.storage}
              cost={cost}
              capacity={capacity}
              onCost={(v) => {
                setCost(v)
                remember('modbot.costPerGbMonth', v)
              }}
              onCapacity={(v) => {
                setCapacity(v)
                remember('modbot.capacityGb', v)
              }}
            />
            <MachineUsageCard />
          </PanelGrid>
        </Disclosure>
      )}

      <Disclosure label="Sync timings">
        <SyncTimings />
      </Disclosure>
    </div>
  )
}

/**
 * Folded away until asked for (console look §11.6). What is inside is not drawn, and so not
 * loaded, until it is opened.
 */
function Disclosure({ label, children }: { label: string; children: React.ReactNode }) {
  const [open, setOpen] = useState(false)

  return (
    <details className="group" onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary
        className="flex w-fit cursor-pointer list-none items-center gap-1 rounded-sm text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring [&::-webkit-details-marker]:hidden"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <ChevronRight
          className="size-3.5 shrink-0 transition-transform group-open:rotate-90 motion-reduce:transition-none"
          aria-hidden
        />
        {label}
      </summary>
      {open && <div className="mt-3">{children}</div>}
    </details>
  )
}

function StorageCard({
  storage,
  cost,
  capacity,
  onCost,
  onCapacity,
}: {
  storage: DataSettings['storage']
  cost: string
  capacity: string
  onCost: (v: string) => void
  onCapacity: (v: string) => void
}) {
  const capacityBytes = Number(capacity) ? Number(capacity) * GB : null

  return (
    <SettingsCard span={12} title="Storage">
      <div className="grid gap-6 @3xl:grid-cols-[minmax(0,18rem)_minmax(0,1fr)]">
        <div className="flex flex-col gap-4">
          <div className="grid grid-cols-2 gap-x-4 gap-y-3">
            <Fact label="Database" value={bytes(storage.bytes)} mono />
            <Fact label="Records" value={storage.facts.toLocaleString()} mono />
            <Fact
              label="Per record"
              value={storage.facts > 0 ? `${Math.round(storage.bytesPerFact)} bytes` : '—'}
              mono={storage.facts > 0}
            />
            <Fact
              label="Arriving"
              value={
                storage.observedDays >= 1
                  ? `${Math.round(storage.factsPerDay).toLocaleString()} a day`
                  : 'Not measurable yet'
              }
              mono={storage.observedDays >= 1}
            />
          </div>

          {/* items-end: a label that wraps grows upward, and the two inputs stay on one line. */}
          <div className="grid max-w-sm grid-cols-2 items-end gap-3">
            <Field label="Cost per GB a month" placeholder="0.25" value={cost} onChange={onCost} />
            <Field label="Disk size (GB)" placeholder="500" value={capacity} onChange={onCapacity} />
          </div>
        </div>

        <div className="flex min-w-0 flex-col gap-3">
          <StorageChart
            storage={storage}
            capacityBytes={capacityBytes}
            costPerGbMonth={Number(cost) || null}
          />
        </div>
      </div>
    </SettingsCard>
  )
}

/**
 * Every "how long" in one panel with one Save: the three record windows and Modbot's own log. They
 * are two settings on the server (spec 5.5 and the log settings), saved together here, because an
 * owner thinks of them as one question.
 *
 * A save that shortens any window asks first: the records past the new end are destroyed for good
 * (settings review 2026-09-27 §4).
 */
function KeepForCard({
  current,
  onSaved,
}: {
  current: DataSettings['retention']
  onSaved: () => void
}) {
  const [moderation, setModeration] = useState(String(current.moderationFactRetentionDays))
  const [presence, setPresence] = useState(String(current.presenceFactRetentionDays))
  const [messages, setMessages] = useState(String(current.discordMessageRetentionDays))
  const [logs, setLogs] = useState<LogSettings | null>(null)
  const [logDays, setLogDays] = useState('')
  const [sendToCloud, setSendToCloud] = useState(true)
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const loadLogs = useCallback(
    () =>
      api
        .logSettings()
        .then((next) => {
          setLogs(next)
          setLogDays(String(next.keepDays))
          setSendToCloud(next.sendToCloud)
        })
        .catch((e: unknown) =>
          setProblem(e instanceof ApiError ? e.message : 'Could not load the log settings.'),
        ),
    [],
  )

  useEffect(() => void loadLogs(), [loadLogs])

  const recordsDirty =
    moderation !== String(current.moderationFactRetentionDays) ||
    presence !== String(current.presenceFactRetentionDays) ||
    messages !== String(current.discordMessageRetentionDays)

  const logsDirty =
    logs !== null && (logDays !== String(logs.keepDays) || sendToCloud !== logs.sendToCloud)

  const dirty = recordsDirty || logsDirty

  const shortens = shortensAny(
    [
      current.moderationFactRetentionDays,
      current.presenceFactRetentionDays,
      current.discordMessageRetentionDays,
      logs?.keepDays ?? 0,
    ],
    [Number(moderation) || 0, Number(presence) || 0, Number(messages) || 0, logs ? Number(logDays) || 0 : 0],
  )

  const save = () => {
    setSaving(true)
    setSaved(false)
    setProblem(null)

    const writes: Promise<unknown>[] = []
    if (recordsDirty)
      writes.push(
        api
          .setRetention({
            moderationFactRetentionDays: Number(moderation) || 0,
            presenceFactRetentionDays: Number(presence) || 0,
            discordMessageRetentionDays: Number(messages) || 0,
          })
          .then(onSaved),
      )
    if (logsDirty)
      writes.push(api.setLogSettings({ keepDays: Number(logDays) || 0, sendToCloud }).then(loadLogs))

    Promise.all(writes)
      .then(() => setSaved(true))
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setSaving(false))
  }

  return (
    <SettingsCard
      title="Keep for (days)"
      footer={
        <>
          {shortens ? (
            <ConfirmButton
              variant="default"
              confirm="Delete older records"
              disabled={!dirty || saving}
              onConfirm={save}
            >
              Save
            </ConfirmButton>
          ) : (
            <Button size="xs" disabled={!dirty || saving} onClick={save}>
              {saving ? 'Saving…' : 'Save'}
            </Button>
          )}
          <Outcome tone="ok">{saved && !dirty && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      {/* items-end: a label that wraps grows upward, and the inputs stay on one line. */}
      <div className="grid max-w-lg items-end gap-3 sm:grid-cols-2">
        <Field label="Moderation" placeholder="0" value={moderation} onChange={setModeration} />
        <Field label="Who was where" placeholder="0" value={presence} onChange={setPresence} />
        <Field label="Discord messages" placeholder="0" value={messages} onChange={setMessages} />
        <Field label="Modbot's log" placeholder="180" value={logDays} onChange={setLogDays} />
      </div>
      <Hint>0 keeps forever.</Hint>
      <Switch
        checked={sendToCloud}
        disabled={logs === null || !logs.cloudAllowed}
        onChange={setSendToCloud}
      >
        Send logs to Modbot Cloud
      </Switch>
    </SettingsCard>
  )
}

/**
 * What this install is and how it talks to Modbot Cloud: the version and whether a newer one is
 * out, whether it is linked to a Cloud account, and whether the group's public instances are listed
 * on modbot.co. Three panels until 2026-09-27, one of them on another tab under the same name.
 *
 * Modbot never updates itself and never pulls an image: the panel names the version and what to
 * pull, and the operator decides. MODBOT_CLOUD_DISABLED does not reach the update check — the
 * question sends nothing about the deployment — so its switch is the only thing that turns it off.
 *
 * The link code is made by this server and typed into Cloud, never the other way round (Cloud
 * accounts and registry spec 3.3). Only somebody who can already sign in here and change settings
 * sees it, which is the proof of ownership.
 *
 * The three switches save at once: each is one yes or no with nothing to save beside it. The
 * usage report's switch is greyed out, like the listing's, when MODBOT_CLOUD_DISABLED is set. Sending
 * Modbot's log has its own switch on the Keep for card.
 */
function InstallCard({ deployment }: { deployment: DataSettings['deployment'] }) {
  const [update, setUpdate] = useState<UpdateView | null>(null)
  const [cloud, setCloud] = useState<CloudStatusView | null>(null)
  const [listing, setListing] = useState<PublicInstancesView | null>(null)
  const [code, setCode] = useState<LinkCodeView | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const fail = (fallback: string) => (e: unknown) =>
    setError(e instanceof ApiError ? e.message : fallback)

  useEffect(() => {
    api.updateCheck().then(setUpdate).catch(fail('Could not load.'))
    api.cloudStatus().then(setCloud).catch(fail('Could not load.'))
    api.publicInstances().then(setListing).catch(fail('Could not load.'))
  }, [])

  const act = (work: Promise<void>) => {
    setBusy(true)
    setError(null)
    work.finally(() => setBusy(false))
  }

  const checkUpdates = (on: boolean) =>
    act(api.setUpdateCheck(on).then(setUpdate).catch(fail('Could not save.')))

  const list = (shared: boolean) =>
    act(api.setPublicInstances(shared).then(setListing).catch(fail('Could not save.')))

  const askCode = () => act(api.cloudLinkCode().then(setCode).catch(fail('Could not get a code.')))

  const newest = !update ? '…' : !update.on ? 'Not checked' : (update.newest ?? '—')

  const report = (reportOn: boolean) =>
    act(api.setCloudReport(reportOn).then(setCloud).catch(fail('Could not save.')))

  const reported = !cloud
    ? '…'
    : !cloud.reportOn
      ? 'Off'
      : cloud.lastReportAt === null
        ? 'Not sent yet'
        : cloud.lastReportOk
          ? dateTime(cloud.lastReportAt)
          : 'Failed'

  const linked = !cloud ? '…' : cloud.disabled ? 'Turned off' : cloud.registered ? 'Linked' : 'Not linked'

  return (
    <SettingsCard
      span={12}
      title="This install"
      footer={
        <>
          <Button
            type="button"
            size="xs"
            variant="outline"
            onClick={askCode}
            disabled={busy || !cloud || cloud.disabled}
          >
            Get link code
          </Button>
          {update?.newerAvailable && update.notesUrl && (
            <Button asChild size="xs" variant="outline">
              <a href={update.notesUrl} target="_blank" rel="noreferrer noopener">
                Release notes
              </a>
            </Button>
          )}
          <Outcome tone="problem">{error}</Outcome>
        </>
      }
    >
      <div className="grid gap-x-8 gap-y-3 @3xl:grid-cols-2">
        <div className="flex flex-col gap-1">
          <div className="max-w-lg">
            <Row
              label="Version"
              value={deployment.version}
              title={deployment.commit ? `${deployment.commit}${deployment.branch ? ` · ${deployment.branch}` : ''}` : undefined}
              mono
            />
            <Row label="Install" value={deployment.platform} />
            <Row label="Newest" value={newest} mono={!!update?.on && !!update.newest} />
            {update?.newerAvailable && update.image && (
              <Row label="Pull" value={`${update.image}:${update.tag ?? update.newest ?? ''}`} mono />
            )}
          </div>
          <Switch checked={update?.on ?? false} disabled={busy || !update} onChange={checkUpdates}>
            Check for updates
          </Switch>
          {update?.on && update.problem && (
            <span className="text-destructive" title={update.problem} style={{ fontSize: 'var(--text-small)' }}>
              Could not check for updates.
            </span>
          )}
        </div>

        <div className="flex flex-col gap-1">
          <div className="max-w-lg">
            <Row label="Modbot Cloud" value={linked} />
            <Row
              label="Last report"
              value={reported}
              title={
                cloud?.reportOn && cloud.lastReportOk === false ? (cloud.lastReportProblem ?? undefined) : undefined
              }
              mono={!!cloud?.reportOn && !!cloud.lastReportAt && !!cloud.lastReportOk}
            />
            {code && <Row label="Link code" value={`${code.code} · ${lengthOfTime(code.expiresInMinutes)}`} mono />}
            {listing && (
              <Row
                label="Instances last sent"
                value={listing.lastSentAt ? dateTime(listing.lastSentAt) : '—'}
                mono={!!listing.lastSentAt}
              />
            )}
          </div>
          <Switch
            checked={listing?.shared ?? false}
            disabled={busy || !listing || listing.cloudDisabled}
            onChange={list}
          >
            List this group&rsquo;s public instances on modbot.co
          </Switch>
          <Switch
            checked={cloud?.reportOn ?? false}
            disabled={busy || !cloud || cloud.disabled}
            onChange={report}
          >
            Send usage report to Modbot Cloud
          </Switch>
        </div>
      </div>
    </SettingsCard>
  )
}

/**
 * The address people use to reach this Modbot, used to build links sent by email or Discord
 * (accounts and access design §4.2). Saved through its own endpoint, which records the change in
 * the audit log, rather than with the email settings it used to share a form with.
 */
function PublicAddressCard() {
  const [view, setView] = useState<PublicAddressView | null>(null)
  const [value, setValue] = useState('')
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .publicAddress()
      .then((next) => {
        setView(next)
        setValue(next.publicAddress ?? '')
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the public address.'))
  }, [])

  const save = (event: React.FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setSaved(false)
    setError(null)

    api
      .setPublicAddress(value)
      .then((next) => {
        setView((current) => ({ publicAddress: next.publicAddress, suggestion: current?.suggestion ?? null }))
        setValue(next.publicAddress ?? '')
        setSaved(true)
      })
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setSaving(false))
  }

  const suggestion = view?.suggestion ?? null

  return (
    <SettingsCard
      title="Public address"
      footer={
        <>
          <Button type="submit" form="public-address-form" size="xs" disabled={saving || !view}>
            {saving ? 'Saving…' : 'Save'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{error}</Outcome>
        </>
      }
    >
      <form id="public-address-form" onSubmit={save} className="flex flex-col gap-3">
        <div className="flex max-w-lg flex-col gap-3">
          <Field
            label="Address"
            mono
            value={value}
            onChange={setValue}
            placeholder={suggestion ?? window.location.origin}
          />
        </div>
        {suggestion && !value && (
          <button
            type="button"
            className="self-start text-link underline-offset-2 hover:underline"
            style={{ fontSize: 'var(--text-small)' }}
            onClick={() => setValue(suggestion)}
          >
            Use {suggestion}
          </button>
        )}
      </form>
      <OwnerEmailSwitch />
    </SettingsCard>
  )
}

/**
 * Whether GET /api/server gives out the owner's address (server info and account email design
 * §2.3). On this card because both are about what this server tells the outside world it is.
 *
 * Saves on the switch: there is nothing else here to press Save with, and the public address's own
 * Save button belongs to its form.
 */
function OwnerEmailSwitch() {
  const [view, setView] = useState<ServerSettings | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .serverSettings()
      .then(setView)
      .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not load the setting.'))
  }, [])

  return (
    <>
      <Switch
        checked={view?.showOwnerEmail ?? false}
        disabled={saving || !view}
        onChange={(next) => {
          setSaving(true)
          setError(null)
          api
            .setServerSettings(next)
            .then(setView)
            .catch((e: unknown) => setError(e instanceof ApiError ? e.message : 'Could not save.'))
            .finally(() => setSaving(false))
        }}
      >
        Show the owner&rsquo;s email address
      </Switch>
      <Outcome tone="problem">{error}</Outcome>
    </>
  )
}
