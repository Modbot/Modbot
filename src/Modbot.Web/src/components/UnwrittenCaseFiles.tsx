import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { WriteCaseFile } from '@/components/CaseFileForm'
import { SubjectLink } from '@/components/facts'
import { api, ApiError, type CurrentUser, type UnwrittenBan, type UnwrittenBanList } from '@/lib/api'
import { formatDay } from '@/lib/format'
import { can } from '@/lib/permissions'

/**
 * Bans with nobody's account of why: the card on the Bans page, and the list the accountability
 * "bans without a report" signal reads from.
 *
 * It counts only what the audit log recorded, so it covers the window Modbot's sync covers and
 * says so — a group with three years of bans and a week-old deployment has a week here, and an
 * empty card is not proof that every ban was written up.
 */
export function UnwrittenCaseFiles({
  me,
  onOpenSubject,
  onOpenCase,
}: {
  me: CurrentUser
  onOpenSubject: (id: string) => void
  onOpenCase: (caseId: string) => void
}) {
  const [list, setList] = useState<UnwrittenBanList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [writing, setWriting] = useState<UnwrittenBan | null>(null)

  const load = useCallback(
    () =>
      api
        .unwrittenCases(30, 50)
        .then((next) => {
          setList(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(
            e instanceof ApiError && e.status === 403
              ? 'You do not have permission to see case files.'
              : 'Could not load the bans with no case file.',
          ),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error) return null
  if (!list) return null

  const canWrite = can(me, 'Ban')

  return (
    <Card>
      <CardContent className="flex flex-col gap-2 px-5">
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-medium">
            {list.total === 0
              ? 'Every recorded ban has a case file'
              : `${list.total} ${list.total === 1 ? 'ban has' : 'bans have'} no case file`}
          </span>
          <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            in the last {list.days} days
          </span>
        </div>

        {list.total > 0 && (
          <>
            <ul className="flex flex-col gap-1">
              {list.bans.map((ban) => (
                <li
                  key={`${ban.factId}`}
                  className="flex flex-wrap items-center gap-x-2 gap-y-1 border-b py-1 last:border-0"
                  style={{ borderBottomWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
                >
                  <SubjectLink id={ban.userId} name={ban.displayName} onOpen={onOpenSubject} />
                  <span className="text-muted-foreground">
                    banned {formatDay(ban.bannedAt)}
                    {ban.bannedBy ? ` by ${ban.bannedBy.name ?? ban.bannedBy.id}` : ''}
                    {ban.liftedAt ? ` · ban since lifted ${formatDay(ban.liftedAt)}` : ''}
                  </span>
                  <span className="flex-1" />
                  {canWrite && (
                    <Button size="xs" variant="outline" onClick={() => setWriting(ban)}>
                      Write the case file
                    </Button>
                  )}
                </li>
              ))}
            </ul>

            {list.total > list.bans.length && (
              <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                Showing the {list.bans.length} most recent of {list.total}.
              </p>
            )}
          </>
        )}

        <Dialog open={writing !== null} onOpenChange={(open) => !open && setWriting(null)}>
          <DialogContent title="Write the case file" className="max-w-[640px]">
            {writing && (
              <WriteCaseFile
                ban={writing}
                onCancel={() => setWriting(null)}
                onWritten={(caseId) => {
                  setWriting(null)
                  void load()
                  onOpenCase(caseId)
                }}
              />
            )}
          </DialogContent>
        </Dialog>
      </CardContent>
    </Card>
  )
}
