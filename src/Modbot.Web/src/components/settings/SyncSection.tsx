import { useEffect, useState } from 'react'
import { api, ApiError, type SyncSettings } from '@/lib/api'
import { Hint, Notice, Placeholder, Row } from './fields'
import { SettingsCard, SettingsSection } from './SettingsCard'
import { seconds } from './units'

/**
 * Sync poll rate — read-only, and saying so.
 *
 * Spec 4.2.1 calls for a slider per rate with the cap enforced server-side on write. There is no
 * slider because there is nowhere to write to: the intervals are process configuration fixed at
 * start-up, and no settings column holds them. A control that discarded what the operator typed
 * would be worse than none — they would believe they had dialled a rate down when they had not.
 */
export function SyncSection() {
  const [settings, setSettings] = useState<SyncSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .syncSettings()
      .then(setSettings)
      .catch((e: unknown) =>
        setError(e instanceof ApiError ? e.message : 'Could not load sync settings.'),
      )
  }, [])

  return (
    <SettingsSection id="sync" title="Sync" description="How often Modbot asks VRChat for changes.">
      {error ? (
        <Placeholder>{error}</Placeholder>
      ) : !settings ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <>
          <Notice tone="warn" title="These cannot be changed here yet." className="col-span-12">
            <p>{settings.editableExplanation}</p>
          </Notice>

          {!settings.running && (
            <Notice
              tone="neutral"
              title="The producers are not running in this process."
              className="col-span-12"
            >
              <p>The values below are what would be used rather than what is.</p>
            </Notice>
          )}

          <SettingsCard
            title="Group audit log"
            description="Adaptive: fast while entries are arriving, geometrically slower while they are not."
          >
            <Hint>
              The interval in force right now, and the producer's reason for it, are on the Sync
              health screen — they change every poll and are a diagnostic rather than a setting.
            </Hint>
            <div>
              <Row label="Fastest interval" value={seconds(settings.auditLog.minIntervalSeconds)} />
              <Row label="Slowest interval" value={seconds(settings.auditLog.maxIntervalSeconds)} />
              <Row
                label="Pacing floor"
                value={`${seconds(settings.auditLog.pacingFloorSeconds)} — configuration may only ever make this slower`}
              />
              <Row label="Back-off per quiet poll" value={`${settings.auditLog.quietBackoff}×`} />
              <Row
                label="Jitter"
                value={`up to +${Math.round(settings.auditLog.jitterFraction * 100)}%`}
              />
              <Row label="Entries per request" value={String(settings.auditLog.pageSize)} />
              <Row label="Requests per poll" value={String(settings.auditLog.maxPagesPerRun)} />
              <Row
                label="Re-read window"
                value={`${seconds(settings.auditLog.overlapSeconds)} behind the cursor, so a late entry is not missed`}
              />
              <Row
                label="Catch-up"
                value={
                  settings.auditLog.catchUp
                    ? `On, up to ${settings.auditLog.maxCatchUpPages.toLocaleString()} pages`
                    : 'Off — only entries from now on are recorded'
                }
              />
            </div>
          </SettingsCard>

          <SettingsCard
            title="Group info"
            description="A fixed interval, because the group record changes rarely."
          >
            <div>
              <Row label="Interval" value={seconds(settings.groupInfo.intervalSeconds)} />
              <Row
                label="After a failure"
                value={seconds(settings.groupInfo.retryIntervalSeconds)}
              />
              <Row
                label="While rate limited"
                value={seconds(settings.groupInfo.rateLimitedIntervalSeconds)}
              />
              <Row label="Pacing floor" value={seconds(settings.groupInfo.pacingFloorSeconds)} />
              <Row
                label="Jitter"
                value={`up to +${Math.round(settings.groupInfo.jitterFraction * 100)}%`}
              />
            </div>
          </SettingsCard>
        </>
      )}
    </SettingsSection>
  )
}
