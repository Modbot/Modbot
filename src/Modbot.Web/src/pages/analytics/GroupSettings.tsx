import { useState } from 'react'
import { FieldRow, LanguagePicker, LinkListEditor, LongBox, SaveCancel, TextBox } from '@/components/group/ProfileEditors'
import { PanelGrid } from '@/components/PanelGrid'
import { useSave } from '@/lib/useSave'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Select } from '@/components/ui/select'
import { api, type CurrentUser, type GroupInfo, type JoinState } from '@/lib/api'
import {
  JOIN_STATES,
  LIMITS,
  descriptionProblem,
  draftFrom,
  draftProblem,
  isEmptyEdit,
  languagesProblem,
  linksProblem,
  nameProblem,
  profileChanges,
  type ProfileDraft,
} from '@/lib/groupProfile'
import { groupTabHref } from '@/lib/groupOverview'
import { mayOpen, type PageId } from '@/lib/nav'
import { followLink } from '@/lib/router'
import { cn } from '@/lib/utils'
import { useGroupInfo } from '@/lib/useGroupInfo'
import { GroupHeader } from './GroupHeader'
import { PageMessage } from './shared'

/**
 * The VRChat page's Settings tab, as vrchat.com lays out a group's settings: a row of its own tabs
 * (General, Roles, then Logs), and under General every part of the group's profile VRChat lets an admin
 * change from its website — name, description, rules, languages, links and who can join.
 *
 * One Save sends one request with only the fields that changed, and nothing at all when none did.
 * The page then shows the group as VRChat answered. Cancel puts the form back as stored.
 *
 * Roles is its own page (GroupRoles.tsx), at its own address, reached from the same row. The icon
 * and banner are not here: changing them means VRChat's file upload, which Modbot does not do yet.
 */
export function GroupSettings({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const { info, error, setInfo, reload } = useGroupInfo()

  if (error) return <PageMessage tone="danger" onTryAgain={reload}>{error}</PageMessage>
  if (!info) return <PageMessage tone="loading" />

  return (
    <div className="flex flex-col gap-3">
      <GroupHeader info={info} me={me} pathOf={pathOf} active="group-settings" />
      <SettingsTabs me={me} pathOf={pathOf} active="group-settings" />
      {/* Keyed on what was read, so a Save that comes back starts the form from VRChat's answer. */}
      <General key={info.generatedAt} info={info} onSaved={setInfo} />
    </div>
  )
}

/**
 * General · Roles · Logs, the way VRChat's settings split. Logs is Modbot's audit log, opened with
 * the group's header and this row above it (`groupTabHref`). Each is offered only to somebody who
 * may open it.
 */
export function SettingsTabs({
  me,
  pathOf,
  active,
}: {
  me: CurrentUser
  pathOf: (id: PageId) => string
  active: 'group-settings' | 'group-roles' | 'audit'
}) {
  const tabs = (
    [
      { id: 'group-settings', label: 'General' },
      { id: 'group-roles', label: 'Roles' },
      { id: 'audit', label: 'Logs' },
    ] as const
  )
    .filter((tab) => mayOpen(me, tab.id))
    .map((tab) => ({ label: tab.label, href: groupTabHref(tab.id, pathOf(tab.id)), current: tab.id === active }))

  return (
    <nav aria-label="Settings" className="flex items-stretch gap-1 overflow-x-auto border-b border-b-(length:--hairline)">
      {tabs.map((tab) => (
        <a
          key={tab.label}
          href={tab.href}
          onClick={followLink(tab.href)}
          aria-current={tab.current ? 'page' : undefined}
          className={cn(
            'relative flex shrink-0 items-center px-3 font-medium whitespace-nowrap transition-colors focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring',
            tab.current ? 'text-foreground' : 'text-muted-foreground hover:text-foreground',
          )}
          style={{ fontSize: 'var(--text-small)', minHeight: 'var(--control-h)' }}
        >
          {tab.label}
          {tab.current && <span aria-hidden className="absolute inset-x-0 bottom-0 h-[calc(var(--hairline)*2)] bg-primary" />}
        </a>
      ))}
    </nav>
  )
}

function General({ info, onSaved }: { info: GroupInfo; onSaved: (info: GroupInfo) => void }) {
  const [draft, setDraft] = useState<ProfileDraft>(() => draftFrom(info))
  const { saving, problem, missing, run, clear } = useSave()
  const set = (change: Partial<ProfileDraft>) => setDraft((d) => ({ ...d, ...change }))

  const changes = profileChanges(info, draft)
  const invalid = draftProblem(draft)

  return (
    <PanelGrid className="grid-cols-1">
      <Card>
        <CardHeader>
          <CardTitle>Profile</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          <FieldRow label="Name" problem={nameProblem(draft.name)} count={`${draft.name.trim().length}/${LIMITS.nameMax}`}>
            {(id) => <TextBox id={id} value={draft.name} invalid={nameProblem(draft.name) !== null} onChange={(name) => set({ name })} />}
          </FieldRow>

          <FieldRow
            label="Description"
            problem={descriptionProblem(draft.description)}
            count={`${draft.description.trim().length}/${LIMITS.descriptionMax}`}
          >
            {(id) => (
              <LongBox
                id={id}
                value={draft.description}
                invalid={descriptionProblem(draft.description) !== null}
                onChange={(description) => set({ description })}
              />
            )}
          </FieldRow>

          <FieldRow label="Rules">
            {(id) => <LongBox id={id} rows={8} value={draft.rules} onChange={(rules) => set({ rules })} />}
          </FieldRow>
        </CardContent>
      </Card>

      <PanelGrid className="md:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Languages</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-1">
            <LanguagePicker value={draft.languages} onChange={(languages) => set({ languages })} />
            <Problem>{languagesProblem(draft.languages)}</Problem>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Links</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col gap-1">
            <LinkListEditor value={draft.links} onChange={(links) => set({ links })} />
            <Problem>{linksProblem(draft.links)}</Problem>
          </CardContent>
        </Card>
      </PanelGrid>

      <Card>
        <CardHeader>
          <CardTitle>Who can join</CardTitle>
        </CardHeader>
        <CardContent>
          <Select
            aria-label="Who can join"
            value={draft.joinState ?? ''}
            className="w-full sm:w-72"
            onChange={(v) => set({ joinState: (v || null) as JoinState | null })}
          >
            {draft.joinState === null && <option value="">—</option>}
            {JOIN_STATES.map((s) => (
              <option key={s.value} value={s.value}>
                {s.label}
              </option>
            ))}
          </Select>
        </CardContent>
      </Card>

      <Card>
        <CardContent>
          <SaveCancel
            saving={saving}
            missing={missing}
            disabled={isEmptyEdit(changes) || invalid !== null}
            problem={problem}
            onCancel={() => {
              setDraft(draftFrom(info))
              clear()
            }}
            onSave={() => void run(() => api.updateGroupProfile(changes).then(onSaved))}
          />
        </CardContent>
      </Card>
    </PanelGrid>
  )
}

function Problem({ children }: { children: string | null }) {
  if (!children) return null
  return (
    <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
      {children}
    </span>
  )
}
