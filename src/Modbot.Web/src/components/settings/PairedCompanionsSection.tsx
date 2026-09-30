import { useCallback, useEffect, useState } from 'react'
import { EmptyRow } from '@/components/PanelGrid'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { api, type PairedCompanion } from '@/lib/api'
import { ConfirmButton, Outcome, Placeholder } from './fields'
import { SettingsCard, SettingsSection } from './SettingsCard'
import { failure, when } from './api/shared'

/**
 * Settings → People → Paired companions: the Windows companions paired to this server.
 *
 * The server decides whose: somebody holding Pair a companion sees their own, somebody holding
 * Manage users sees everybody's, with whose each one is. Remove revokes one; the companion is
 * refused from its next request and a new pairing is the only way back. Removed ones are left out
 * of the list, which is of what can still report, not of everything that ever could.
 */
export function PairedCompanionsSection() {
  const [companions, setCompanions] = useState<PairedCompanion[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .pairedCompanions()
        .then((all) => {
          setCompanions(all.filter((c) => c.revokedAt === null))
          setError(null)
        })
        .catch((e: unknown) => setError(failure(e, 'Could not load paired companions.'))),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  return (
    <SettingsSection id="companions" title="Paired companions">
      {error ? (
        <Placeholder tone="danger">{error}</Placeholder>
      ) : !companions ? (
        <Placeholder>Loading…</Placeholder>
      ) : (
        <SettingsCard title="Paired companions" span={12} flush>
          <CompanionList companions={companions} onChanged={() => void load()} />
        </SettingsCard>
      )}
    </SettingsSection>
  )
}

function CompanionList({ companions, onChanged }: { companions: PairedCompanion[]; onChanged: () => void }) {
  const [problem, setProblem] = useState<string | null>(null)

  if (companions.length === 0) return <EmptyRow>No paired companions.</EmptyRow>

  const remove = (id: string) => {
    setProblem(null)
    api
      .removePairedCompanion(id)
      .then(onChanged)
      .catch((e: unknown) => setProblem(failure(e, 'Could not remove the companion.')))
  }

  return (
    <>
      <Table
        pinFirst
        head={
          <>
            <Th>Person</Th>
            <Th>Platform</Th>
            <Th>Version</Th>
            <Th>Last seen</Th>
            <Th />
          </>
        }
      >
        {companions.map((c) => (
          <Tr key={c.id}>
            <Td className="font-medium">
              <div className="max-w-[16rem] truncate" title={c.ownerName ?? undefined}>
                {c.ownerName ?? '—'}
              </div>
            </Td>
            <Td>{c.platform}</Td>
            <Td className="font-mono">{c.companionVersion}</Td>
            <Td className={c.lastSeenAt ? 'font-mono' : undefined}>{c.lastSeenAt ? when(c.lastSeenAt) : 'Never'}</Td>
            <Td className="text-right">
              <ConfirmButton onConfirm={() => remove(c.id)}>Remove</ConfirmButton>
            </Td>
          </Tr>
        ))}
      </Table>
      {problem && (
        <div className="border-t border-t-(length:--hairline) p-(--panel-pad)">
          <Outcome tone="problem">{problem}</Outcome>
        </div>
      )}
    </>
  )
}
