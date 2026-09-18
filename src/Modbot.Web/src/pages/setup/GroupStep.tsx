import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { ApiError, api, type ConnectionDiagnosis, type GroupCandidates } from '@/lib/api'
import { cn } from '@/lib/utils'
import { DiagnosisNote } from './DiagnosisNote'
import { ErrorText, Note, WizardBody, WizardHeader } from './WizardChrome'
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
          <div
            className="overflow-hidden rounded-xl border bg-card"
            role="radiogroup"
            aria-label="Groups"
          >
            {candidates.groups.map((group, index) => (
              <label
                key={group.id}
                className={cn(
                  'flex cursor-pointer items-center gap-3 px-4 py-2.5',
                  index > 0 && 'border-t',
                  selected === group.id && 'bg-accent/40',
                )}
                style={{ borderTopWidth: index > 0 ? 'var(--hairline)' : undefined }}
              >
                <input
                  type="radio"
                  name="group"
                  className="accent-[var(--primary)]"
                  checked={selected === group.id}
                  onChange={() => setSelected(group.id)}
                />
                <div className="grid size-7 shrink-0 place-items-center overflow-hidden rounded-full bg-secondary text-[0.625rem] font-semibold text-muted-foreground">
                  {group.iconUrl ? (
                    <img src={vrchatMedia(group.iconUrl)} alt="" className="size-full object-cover" />
                  ) : (
                    initials(group.name)
                  )}
                </div>
                <div className="min-w-0 flex-1 leading-tight">
                  <div className="truncate font-medium">{group.name}</div>
                  <div
                    className="truncate font-mono text-muted-foreground/70"
                    style={{ fontSize: 'var(--text-small)' }}
                  >
                    {group.id}
                  </div>
                  {group.missingPermissions.length > 0 && (
                    <div className="mt-0.5 text-warn" style={{ fontSize: 'var(--text-small)' }}>
                      Missing {group.missingPermissions.join(', ')}
                    </div>
                  )}
                </div>
                <div className="shrink-0 font-mono text-muted-foreground">
                  {group.memberCount.toLocaleString()}
                </div>
              </label>
            ))}
          </div>
        )}

        {candidates && candidates.groups.length === 0 && (
          // Spec 7.1 step 4: say so explicitly and explain the required permissions. An empty
          // list on its own leaves the operator with nothing to act on.
          <Note
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
          </Note>
        )}

        {candidates && filteredOut > 0 && candidates.groups.length > 0 && (
          <p className="m-0 text-muted-foreground/70" style={{ fontSize: 'var(--text-small)' }}>
            {filteredOut} other group{filteredOut === 1 ? '' : 's'} hidden: no moderator permissions
          </p>
        )}

        <Button
          type="button"
          variant="outline"
          onClick={() => void load(true)}
          disabled={loading || busy}
          style={{ height: 'var(--control-h)' }}
        >
          {loading ? 'Checking…' : 'Check again'}
        </Button>
      </WizardBody>
    </form>
  )
}
