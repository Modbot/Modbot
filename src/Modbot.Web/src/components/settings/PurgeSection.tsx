import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { api, ApiError, type PurgePreview, type PurgeReceipt } from '@/lib/api'
import { Outcome, Row } from './fields'
import { SettingsCard, SettingsSection } from './SettingsCard'

type Platform = 'VRChat' | 'Discord'

/**
 * Purge a person: everything Modbot stores about one person, removed on request
 * (foundation §5.5, purging a person design).
 *
 * Three steps in one card, in order, each drawn only once the one before it has an answer: look
 * the person up, read what would go and what would stay, then type their id to confirm. The
 * screen never says "are you sure" — it shows the counts and asks for the id back, which is the
 * same question asked in a way that cannot be answered by reflex.
 *
 * Everything shown here is counted by the server from the tables the purge itself writes to, so
 * there is no number on this screen that the purge can contradict.
 */
export function PurgeSection() {
  return (
    <SettingsSection id="purge" title="Purge a person">
      <PurgeCard />
    </SettingsSection>
  )
}

function PurgeCard() {
  const [platform, setPlatform] = useState<Platform>('VRChat')
  const [id, setId] = useState('')
  const [typed, setTyped] = useState('')
  const [preview, setPreview] = useState<PurgePreview | null>(null)
  const [receipt, setReceipt] = useState<PurgeReceipt | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // A change to either half of "who" invalidates everything downstream of it.
  const reset = () => {
    setPreview(null)
    setReceipt(null)
    setTyped('')
    setError(null)
  }

  const look = async () => {
    setBusy(true)
    setError(null)
    setReceipt(null)
    try {
      setPreview(await api.purgePreview(platform, id.trim()))
    } catch (e: unknown) {
      setPreview(null)
      setError(e instanceof ApiError ? e.message : 'Could not look that id up.')
    } finally {
      setBusy(false)
    }
  }

  const purge = async () => {
    if (!preview) return
    setBusy(true)
    setError(null)
    try {
      setReceipt(
        await api.purgePerson({
          platform: preview.platform,
          subjectId: preview.subjectId,
          confirmation: typed,
        }),
      )
      setPreview(null)
      setTyped('')
      setId('')
    } catch (e: unknown) {
      setError(e instanceof ApiError ? e.message : 'The purge failed.')
    } finally {
      setBusy(false)
    }
  }

  const matches = preview !== null && typed === preview.subjectId

  return (
    <SettingsCard
      span={12}
      title="Purge a person"
      footer={
        <>
          <Button
            variant="destructive"
            size="sm"
            disabled={!matches || busy}
            onClick={() => void purge()}
          >
            Purge
          </Button>
          {error && <Outcome tone="problem">{error}</Outcome>}
          {receipt && <Outcome tone="ok">Purged.</Outcome>}
        </>
      }
    >
      <div className="grid max-w-3xl gap-3 sm:grid-cols-[10rem_minmax(0,1fr)_auto] sm:items-end">
        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Account</span>
          <Select
            value={platform}
            onChange={(v) => {
              setPlatform(v as Platform)
              reset()
            }}
          >
            <option value="VRChat">VRChat</option>
            <option value="Discord">Discord</option>
          </Select>
        </label>

        <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
          <span className="text-muted-foreground">Id</span>
          <Input
            value={id}
            onChange={(e) => {
              setId(e.target.value)
              reset()
            }}
          />
        </label>

        <Button
          variant="outline"
          size="sm"
          disabled={busy || id.trim().length === 0}
          onClick={() => void look()}
        >
          Look up
        </Button>
      </div>

      {preview && <Counts preview={preview} />}

      {preview && (
        <label
          className="flex max-w-md flex-col gap-1"
          style={{ fontSize: 'var(--text-small)' }}
        >
          <span className="text-muted-foreground">Type the id to confirm</span>
          <Input value={typed} onChange={(e) => setTyped(e.target.value)} />
        </label>
      )}

      {receipt && <Receipt receipt={receipt} />}
    </SettingsCard>
  )
}

function Counts({ preview }: { preview: PurgePreview }) {
  return (
    <div className="grid gap-x-8 gap-y-1 lg:grid-cols-2">
      <div>
        <Row label="Name" value={preview.name ?? 'Not known'} />
        <Row
          label={preview.platform === 'Discord' ? 'In the server' : 'In the group'}
          value={member(preview.isMember)}
        />
        {preview.isBanned !== null && <Row label="Banned" value={preview.isBanned ? 'Yes' : 'No'} />}
        {preview.linkedAccount && (
          <Row
            label={`Linked ${preview.linkedAccount.platform} account`}
            value={preview.linkedAccount.name ?? preview.linkedAccount.subjectId}
            title={preview.linkedAccount.subjectId}
          />
        )}
      </div>

      <div>
        <h3 className="pt-2 font-medium lg:pt-0" style={{ fontSize: 'var(--text-small)' }}>
          Removed
        </h3>
        <Row label="Facts" value={preview.facts.toLocaleString()} />
        <Row label="Discord messages" value={preview.messages.toLocaleString()} />
        <Row label="Daily totals" value={preview.countedDailyTotals.toLocaleString()} />
        <Row label="Days worked out again" value={preview.days.toLocaleString()} />
        <Row label="Giveaway entries" value={preview.giveawayEntries.toLocaleString()} />
        <Row label="Places in past draws" value={preview.giveawayPlaces.toLocaleString()} />
        <Row label="Imported records" value={preview.importRecords.toLocaleString()} />

        <h3 className="pt-3 font-medium" style={{ fontSize: 'var(--text-small)' }}>
          Kept
        </h3>
        <Row label="Case files" value={preview.caseFilesKept.toLocaleString()} />
        <Row label="Evidence files" value={preview.evidenceFilesKept.toLocaleString()} />
      </div>
    </div>
  )
}

function Receipt({ receipt }: { receipt: PurgeReceipt }) {
  return (
    <div className="grid max-w-3xl gap-x-8 gap-y-1 lg:grid-cols-2">
      <div>
        <h3 className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
          Removed
        </h3>
        <Row label="Facts" value={receipt.facts.toLocaleString()} />
        <Row label="Discord messages" value={receipt.messages.toLocaleString()} />
        <Row label="Daily totals" value={receipt.countedDailyTotals.toLocaleString()} />
        <Row label="Days worked out again" value={receipt.days.toLocaleString()} />
        <Row label="Giveaway entries" value={receipt.giveawayEntries.toLocaleString()} />
        <Row label="Places in past draws" value={receipt.giveawayPlaces.toLocaleString()} />
      </div>

      <div>
        <h3 className="pt-2 font-medium lg:pt-0" style={{ fontSize: 'var(--text-small)' }}>
          Kept
        </h3>
        <Row label="Case files" value={receipt.caseFilesKept.toLocaleString()} />
        <Row label="Evidence files" value={receipt.evidenceFilesKept.toLocaleString()} />
      </div>
    </div>
  )
}

/** Null is "Modbot never saw a membership row", which is not the same as having left. */
function member(is: boolean | null): string {
  if (is === null) return 'Never seen'
  return is ? 'Yes' : 'Left'
}
