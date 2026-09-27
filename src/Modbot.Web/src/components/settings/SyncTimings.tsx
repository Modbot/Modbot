import { useEffect, useState } from 'react'
import { api, ApiError, type SweepSettings, type SyncSettings } from '@/lib/api'
import { Notice } from '@/components/ui/notice'
import { Placeholder, Row } from './fields'
import { PanelGrid } from '@/components/PanelGrid'
import { SettingsCard } from './SettingsCard'
import { seconds } from './units'

/**
 * Sync poll rate — read-only, folded away at the foot of Server. A tab of its own until
 * 2026-09-27, whose first line had to say it could not be changed (settings review §1.4).
 *
 * Spec 4.2.1 calls for a slider per rate with the cap enforced server-side on write. There is no
 * slider because there is nowhere to write to: the intervals are process configuration fixed at
 * start-up, and no settings column holds them. A control that discarded what the operator typed
 * would be worse than none — they would believe they had dialled a rate down when they had not.
 */
export function SyncTimings() {
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
    <PanelGrid className="grid-cols-12">
      {error ? (
        <Placeholder tone="danger">{error}</Placeholder>
      ) : !settings ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <>
          {!settings.running && (
            <Notice tone="warn" title="Sync is not running." className="col-span-12" />
          )}

          <SettingsCard title="Group audit log">
            <div className="max-w-lg">
              <Row label="Shortest wait" value={seconds(settings.auditLog.minIntervalSeconds)} mono />
              <Row label="Longest wait" value={seconds(settings.auditLog.maxIntervalSeconds)} mono />
              <Row label="Least time between requests" value={seconds(settings.auditLog.pacingFloorSeconds)} mono />
              <Row label="Slows down when quiet by" value={`${settings.auditLog.quietBackoff}×`} mono />
              <Row
                label="Random spread"
                value={<>up to <span className="font-mono">+{Math.round(settings.auditLog.jitterFraction * 100)}%</span></>}
              />
              <Row label="Entries per page" value={String(settings.auditLog.pageSize)} mono />
              <Row label="Pages per check" value={String(settings.auditLog.maxPagesPerRun)} mono />
              <Row label="Reads back over" value={seconds(settings.auditLog.overlapSeconds)} mono />
              <Row
                label="Catch-up"
                value={
                  settings.auditLog.catchUp ? (
                    <>
                      On, up to <span className="font-mono">{settings.auditLog.maxCatchUpPages.toLocaleString()}</span> pages
                    </>
                  ) : (
                    'Off'
                  )
                }
              />
            </div>
          </SettingsCard>

          <SweepCard title="Member list" sweep={settings.memberSweep} />
          <SweepCard title="Ban list" sweep={settings.banSweep} />

          <SettingsCard title="Group info">
            <div className="max-w-lg">
              <Row label="Every" value={seconds(settings.groupInfo.intervalSeconds)} mono />
              <Row
                label="After a failure"
                value={seconds(settings.groupInfo.retryIntervalSeconds)}
                mono
              />
              <Row
                label="While VRChat slows us"
                value={seconds(settings.groupInfo.rateLimitedIntervalSeconds)}
                mono
              />
              <Row label="Least time between requests" value={seconds(settings.groupInfo.pacingFloorSeconds)} mono />
              <Row
                label="Random spread"
                value={<>up to <span className="font-mono">+{Math.round(settings.groupInfo.jitterFraction * 100)}%</span></>}
              />
            </div>
          </SettingsCard>
        </>
      )}
    </PanelGrid>
  )
}

function SweepCard({ title, sweep }: { title: string; sweep: SweepSettings }) {
  return (
    <SettingsCard title={title}>
      <div className="max-w-lg">
        <Row label="Time between pages" value={seconds(sweep.pageDelaySeconds)} mono />
        <Row label="Rest between rounds" value={seconds(sweep.restSeconds)} mono />
        <Row label="Entries per page" value={String(sweep.pageSize)} mono />
        <Row label="After a failure" value={seconds(sweep.retryIntervalSeconds)} mono />
        <Row label="While VRChat slows us" value={seconds(sweep.rateLimitedIntervalSeconds)} mono />
        <Row label="Least time between requests" value={seconds(sweep.pacingFloorSeconds)} mono />
        <Row label="Random spread" value={<>up to <span className="font-mono">±{Math.round(sweep.jitterFraction * 100)}%</span></>} />
      </div>
    </SettingsCard>
  )
}
