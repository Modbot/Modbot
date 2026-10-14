import { useCallback, useEffect, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { api, ApiError, type ReportReply, type ReportReplySettings } from '@/lib/api'
import { Field, LongField, Outcome } from '../fields'
import { SettingsCard } from '../SettingsCard'

/** Each row keeps its own key, so removing a middle one does not hand its text to the next. */
let lastKey = 0
const nextKey = () => ++lastKey
const withKeys = (list: ReportReply[]) => list.map((r) => ({ ...r, key: nextKey() }))

/** The server's limits, the same as the API's. */
const MOST_KEPT = 10
const NAME_MAX = 40
const TEXT_MAX = 1000

/**
 * Settings → Discord → Report replies: the ready-made messages staff can send the reporter when they
 * close a report. Its own card with its own Save, because it saves to its own endpoint.
 */
export function ReportRepliesCard() {
  const [data, setData] = useState<ReportReplySettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .reportReplySettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError ? e.message : 'Could not load the report replies.'),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="Report replies">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</EmptyRow>
      </SettingsCard>
    )
  }

  return <ReportRepliesForm settings={data} onSaved={setData} />
}

function ReportRepliesForm({
  settings,
  onSaved,
}: {
  settings: ReportReplySettings
  onSaved: (next: ReportReplySettings) => void
}) {
  const [replies, setReplies] = useState<(ReportReply & { key: number })[]>(() => withKeys(settings.replies))

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const change = (index: number, next: Partial<ReportReply>) => {
    setSaved(false)
    setReplies((all) => all.map((r, i) => (i === index ? { ...r, ...next } : r)))
  }

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setReportReplySettings({ replies: replies.map(({ name, text }) => ({ name, text })) })
      .then((next) => {
        onSaved(next)
        setReplies(withKeys(next.replies))
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="Report replies"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save report replies'}
          </Button>
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={replies.length >= MOST_KEPT}
            onClick={() => {
              setSaved(false)
              setReplies((all) => [...all, { name: '', text: '', key: nextKey() }])
            }}
          >
            Add a reply
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-lg flex-col gap-4">
        {replies.map((reply, index) => (
          <div key={reply.key} className="flex flex-col gap-2">
            <Field
              label="Name"
              value={reply.name}
              maxLength={NAME_MAX}
              onChange={(name) => change(index, { name })}
            />
            <LongField
              label="Message"
              value={reply.text}
              placeholder=""
              maxLength={TEXT_MAX}
              onChange={(text) => change(index, { text })}
            />
            <div>
              <Button
                type="button"
                size="xs"
                variant="outline"
                onClick={() => {
                  setSaved(false)
                  setReplies((all) => all.filter((_, i) => i !== index))
                }}
              >
                Remove
              </Button>
            </div>
          </div>
        ))}
      </div>
    </SettingsCard>
  )
}
