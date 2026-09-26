import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { ApiError, api, type ConnectionDiagnosis, type GroupCandidates } from '@/lib/api'
import { DiagnosisNote } from './DiagnosisNote'
import { ErrorText, WizardBody, WizardHeader } from './WizardChrome'
import { Notice } from '@/components/ui/notice'
import { SwitchBank } from '@/components/ui/switch-bank'
import { WIZARD_FORM_ID, type StepProps } from './types'
import { vrchatMedia } from '@/lib/vrchatMedia'

const initials = (name: string) =>
  (name.replace(/[^\p{L}\p{N}]/gu, '').slice(0, 2) || '··').toUpperCase()

/** Spec 7.1 step 4. */
export function GroupStep({ eyebrow, status, run, refresh, busy }: StepProps) {
  const [candidates, setCandidates] = useState<GroupCandidates | null>(null)
  const [diagnosis, setDiagnosis] = useState<ConnectionDiagnosis | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [selected, setSelected] = useState<string | null>(status.group?.id ?? null)

  // `retry` distinguishes the first load, whose state is already the initial state, from a press
  // of "Check again" -- so the effect below starts no synchronous render of its own.
  const load = async (retry: boolean) => {
    if (retry) {
      setLoading(true)
      setError(null)
      setDiagnosis(null)
    }

    try {
      const result = await api.listGroups()
      setCandidates(result)
      setSelected((current) => current ?? result.groups[0]?.id ?? null)
    } catch (e) {
      // "You have no groups" and "Cloudflare is blocking this host" must not look the same, or
      // the operator goes off to audit their group roles for a network problem.
      if (e instanceof ApiError && e.diagnosis) setDiagnosis(e.diagnosis)
      else setError(e instanceof ApiError ? e.message : 'Could not load your groups.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void load(false)
    // Deliberately once, on arrival: this fires a VRChat request, and re-running it
    // whenever a dependency identity changes would spend rate-limit budget on nothing.
  }, [])

  const submit = (event: React.FormEvent) => {
    event.preventDefault()

    run(async () => {
      if (!selected) {
        setError('Choose a group to continue.')
        return false
      }

      try {
        const group = candidates?.groups.find((g) => g.id === selected)
        await api.selectGroup({ groupId: selected, name: group?.name ?? null })
        await refresh()
        return true
      } catch (e) {
        setError(e instanceof ApiError ? e.message : 'Could not save that choice.')
        return false
      }
    })
  }

  const filteredOut = candidates ? candidates.totalGroups - candidates.groups.length : 0

  return (
    <form id={WIZARD_FORM_ID} onSubmit={submit}>
      <WizardHeader eyebrow={eyebrow} title="Choose the group to manage" />
      <WizardBody>
        {loading && (
          <p className="m-0 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Loading groups…
          </p>
        )}

        {diagnosis && <DiagnosisNote diagnosis={diagnosis} />}
        <ErrorText>{error}</ErrorText>

        {candidates && candidates.groups.length > 0 && (
          <SwitchBank
            layout="list"
            label="Groups"
            value={selected ?? ''}
            onChange={setSelected}
            options={candidates.groups.map((group) => ({
              value: group.id,
              label: (
                <span className="flex min-w-0 flex-1 items-center gap-3">
                  <span
                    className="grid size-7 shrink-0 place-items-center overflow-hidden rounded-full bg-secondary font-semibold text-muted-foreground"
                    style={{ fontSize: 'var(--text-tiny)' }}
                  >
                    {group.iconUrl ? (
                      <img src={vrchatMedia(group.iconUrl)} alt="" className="size-full object-cover" />
                    ) : (
                      initials(group.name)
                    )}
                  </span>
                  <span className="block min-w-0 flex-1 leading-tight">
                    <span className="block truncate font-medium">{group.name}</span>
                    <span
                      className="block truncate font-mono text-muted-foreground"
                      style={{ fontSize: 'var(--text-tiny)' }}
                    >
                      {group.id}
                    </span>
                    {group.missingPermissions.length > 0 && (
                      <span className="mt-0.5 block text-warn" style={{ fontSize: 'var(--text-small)' }}>
                        Missing {group.missingPermissions.join(', ')}
                      </span>
                    )}
                  </span>
                  <span className="shrink-0 font-mono text-muted-foreground">
                    {group.memberCount.toLocaleString()}
                  </span>
                </span>
              ),
            }))}
          />
        )}

        {candidates && candidates.groups.length === 0 && (
          // Spec 7.1 step 4: say so explicitly and explain the required permissions. An empty
          // list on its own leaves the operator with nothing to act on.
          <Notice
            tone="warn"
            title={
              candidates.totalGroups === 0
                ? 'This account is not in any group.'
                : 'None of your groups qualify.'
            }
          >
            Needs one of these permissions:
            <ul className="mt-1 ml-4 list-disc font-mono">
              {candidates.requiredPermissions.map((permission) => (
                <li key={permission}>{permission}</li>
              ))}
            </ul>
          </Notice>
        )}

        {candidates && filteredOut > 0 && candidates.groups.length > 0 && (
          <p className="m-0 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {filteredOut} other group{filteredOut === 1 ? '' : 's'} hidden: no moderator permissions
          </p>
        )}

        <Button
          type="button"
          variant="outline"
          onClick={() => void load(true)}
          disabled={loading || busy}
        >
          {loading ? 'Checking…' : 'Check again'}
        </Button>
      </WizardBody>
    </form>
  )
}
