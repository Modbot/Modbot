import { useCallback, useEffect, useState } from 'react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { Picker } from '@/components/discord/Picker'
import { RolePicker } from '@/components/discord/RolePicker'
import { EmptyRow } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { api, ApiError, type AgeCheckSettings } from '@/lib/api'
import { useDiscordRoles } from '@/lib/discordLists'
import { Outcome, Switch } from '../fields'
import { SettingsCard } from '../SettingsCard'
import { Chips } from '../automod/RuleScopeFields'

/**
 * Settings → Discord → 18+ check: what `/ageverified` and the profile's Mark 18+ verified button give
 * and take away, and whether the ticket is deleted afterwards. The check itself is on the Commands card.
 *
 * Its own card with its own Save, because it saves to its own endpoint. Roles are added one at a
 * time and removed from the chips, as on the rule scope fields. A role holding a dangerous
 * permission is not offered to give, and the server refuses one on save.
 */
export function AgeCheckCard() {
  const [data, setData] = useState<AgeCheckSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .ageCheckSettings()
        .then((next) => {
          setData(next)
          setError(null)
        })
        .catch((e: unknown) =>
          setError(e instanceof ApiError ? e.message : 'Could not load the 18+ check settings.'),
        ),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  if (error || !data) {
    return (
      <SettingsCard title="18+ check">
        <EmptyRow className="px-0" tone={error ? 'danger' : 'loading'} onTryAgain={load}>{error}</EmptyRow>
      </SettingsCard>
    )
  }

  return <AgeCheckForm settings={data} onSaved={setData} />
}

function AgeCheckForm({
  settings,
  onSaved,
}: {
  settings: AgeCheckSettings
  onSaved: (next: AgeCheckSettings) => void
}) {
  const { data: roles } = useDiscordRoles()

  const [give, setGive] = useState(settings.giveRoleIds)
  const [remove, setRemove] = useState(settings.removeRoleIds)
  const [groupRoleId, setGroupRoleId] = useState(settings.groupRoleId ?? '')
  const [deleteTicket, setDeleteTicket] = useState(settings.deleteTicket)
  const [categoryId, setCategoryId] = useState(settings.ticketCategoryId ?? '')

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  const roleName = (id: string) => roles?.roles.find((r) => r.id === id)?.name ?? id

  const groupRole = settings.groupRoles.find((r) => r.id === groupRoleId)

  const save = () => {
    setBusy(true)
    setSaved(false)
    setProblem(null)

    api
      .setAgeCheckSettings({
        giveRoleIds: give,
        removeRoleIds: remove,
        groupRoleId,
        deleteTicket,
        ticketCategoryId: categoryId,
      })
      .then((next) => {
        onSaved(next)
        setSaved(true)
      })
      .catch((e: unknown) => setProblem(e instanceof ApiError ? e.message : 'Could not save.'))
      .finally(() => setBusy(false))
  }

  return (
    <SettingsCard
      title="18+ check"
      footer={
        <>
          <Button type="button" size="xs" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save 18+ check'}
          </Button>
          <Outcome tone="ok">{saved && 'Saved.'}</Outcome>
          <Outcome tone="problem">{problem}</Outcome>
        </>
      }
    >
      <div className="flex max-w-lg flex-col gap-3">
        <RolePicker
          label="Discord roles to give"
          value=""
          allowNone={false}
          needsAssign
          hideDangerous
          exclude={give}
          onChange={(id) => id && !give.includes(id) && setGive([...give, id])}
        />
        <Chips ids={give} name={roleName} onRemove={(id) => setGive(give.filter((i) => i !== id))} />

        <RolePicker
          label="Discord roles to take away"
          value=""
          allowNone={false}
          needsAssign
          exclude={remove}
          onChange={(id) => id && !remove.includes(id) && setRemove([...remove, id])}
        />
        <Chips ids={remove} name={roleName} onRemove={(id) => setRemove(remove.filter((i) => i !== id))} />

        <Picker
          label="VRChat group role to give"
          value={groupRoleId}
          onChange={setGroupRoleId}
          groups={
            settings.groupRoles.length
              ? [{ heading: null, options: settings.groupRoles.map((r) => ({ id: r.id, label: r.name, keywords: r.id, marks: [] })) }]
              : []
          }
          current={groupRoleId ? { label: groupRole?.name ?? groupRoleId, marks: [] } : null}
          allowNone
          error={null}
        />

        <Switch checked={deleteTicket} onChange={setDeleteTicket}>
          Delete the ticket afterwards
        </Switch>
        <ChannelPicker
          label="Ticket category"
          value={categoryId}
          onChange={setCategoryId}
          types={['category']}
        />
      </div>
    </SettingsCard>
  )
}
