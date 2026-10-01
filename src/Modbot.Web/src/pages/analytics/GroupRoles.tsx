import { useEffect, useState } from 'react'
import { Pencil, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { ConfirmDialog } from '@/components/ConfirmDialog'
import { FieldRow, LongBox, SaveCancel, TextBox } from '@/components/group/ProfileEditors'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { VRChatPermissionMissing } from '@/components/VRChatPermissionMissing'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { api, ApiError, type CurrentUser, type GroupRoleRow, type MissingGroupPermission } from '@/lib/api'
import { settingsTab } from '@/lib/groupOverview'
import {
  emptyRoleDraft,
  permissionChoices,
  permissionCount,
  roleBody,
  roleDraftOf,
  roleProblem,
  type RoleDraft,
} from '@/lib/groupRoles'
import type { PageId } from '@/lib/nav'
import { useSave } from '@/lib/useSave'
import { missingPermissionOf } from '@/lib/vrchatPermissions'
import { useShortcuts } from '@/lib/shortcuts'
import { GroupHeaderFor } from './GroupHeader'
import { SettingsTabs } from './GroupSettings'

/**
 * Settings → Roles, as vrchat.com lists a group's roles: each role with its description and how
 * many permissions it has, a New role button, and per role an edit and a delete (after asking).
 * The editor names each permission in VRChat's own words.
 *
 * **Every request to VRChat here is one somebody asked for.** The list is read once when the tab
 * opens and once per Refresh; a Save and a Delete are one request each. A refusal is shown in
 * VRChat's words, or as the permission Modbot's VRChat account is missing, and nothing is retried.
 */
export function GroupRoles({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const [asked, setAsked] = useState(0)
  const [read, setRead] = useState<{
    key: number
    roles: GroupRoleRow[] | null
    error: string | null
    missing: MissingGroupPermission | null
  } | null>(null)
  const loading = read?.key !== asked
  const roles = read?.roles ?? null
  const error = read?.key === asked ? read.error : null
  const missing = read?.key === asked ? read.missing : null

  const [editing, setEditing] = useState<GroupRoleRow | 'new' | null>(null)

  useShortcuts([
    { label: 'Refresh roles', group: 'Page', page: true, run: () => setAsked((n) => n + 1) },
    ...(roles ? [{ label: 'New role', group: 'Page' as const, page: true, run: () => setEditing('new') }] : []),
  ])
  const [deleting, setDeleting] = useState<GroupRoleRow | null>(null)

  useEffect(() => {
    let cancelled = false
    const key = asked

    api
      .groupRoles()
      .then((next) => {
        if (!cancelled) setRead({ key, roles: next.roles, error: null, missing: null })
      })
      .catch((e: unknown) => {
        if (!cancelled)
          setRead((current) => ({
            key,
            roles: current?.roles ?? null,
            error: e instanceof ApiError ? e.message : 'Could not read the roles.',
            missing: e instanceof ApiError ? missingPermissionOf(e.detail) : null,
          }))
      })

    return () => {
      cancelled = true
    }
  }, [asked])

  const setRoles = (change: (roles: GroupRoleRow[]) => GroupRoleRow[]) =>
    setRead((current) => (current?.roles ? { ...current, roles: change(current.roles) } : current))

  // The list shows what VRChat answered a Save with; neither a Save nor a Delete reads it again.
  const saved = (role: GroupRoleRow, created: boolean) => {
    setEditing(null)
    setRoles((current) => (created ? [...current, role] : current.map((r) => (r.id === role.id ? role : r))))
  }

  return (
    <div className="flex flex-col gap-3">
      <GroupHeaderFor me={me} pathOf={pathOf} active={settingsTab(me)} />
      <SettingsTabs me={me} pathOf={pathOf} active="group-roles" />

      <PanelGrid className="grid-cols-1">
        <Card>
          <CardHeader>
            <CardTitle>Roles</CardTitle>
            <CardAction>
              <Button size="xs" variant="outline" onClick={() => setAsked((n) => n + 1)} disabled={loading} aria-label="Refresh roles">
                <RefreshCw className={loading ? 'animate-spin' : undefined} /> Refresh
              </Button>
              <Button size="xs" onClick={() => setEditing('new')} disabled={!roles}>
                <Plus /> New role
              </Button>
            </CardAction>
          </CardHeader>

          {missing ? (
            <EmptyRow tone="danger" onTryAgain={() => setAsked((n) => n + 1)}>
              <VRChatPermissionMissing missing={missing} />
            </EmptyRow>
          ) : error ? (
            <EmptyRow tone="danger" onTryAgain={() => setAsked((n) => n + 1)}>{error}</EmptyRow>
          ) : !roles ? (
            <EmptyRow tone="loading" />
          ) : roles.length === 0 ? (
            <EmptyRow>No roles</EmptyRow>
          ) : (
            <ul className="divide-y-(--hairline) divide-border">
              {roles.map((role) => (
                <li key={role.id}>
                  <RoleView role={role} onEdit={() => setEditing(role)} onDelete={role.isDefault ? undefined : () => setDeleting(role)} />
                </li>
              ))}
            </ul>
          )}
        </Card>
      </PanelGrid>

      <RoleEditor
        open={editing !== null}
        role={editing === 'new' ? null : editing}
        onOpenChange={(open) => !open && setEditing(null)}
        onSaved={saved}
      />

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={deleting?.name ? `Delete the role “${deleting.name}”?` : 'Delete this role?'}
        action="Delete"
        failed="Could not delete the role."
        onConfirm={() => api.deleteGroupRole(deleting!.id, deleting!.name)}
        onDone={() => deleting && setRoles((current) => current.filter((r) => r.id !== deleting.id))}
      />
    </div>
  )
}

function RoleView({ role, onEdit, onDelete }: { role: GroupRoleRow; onEdit: () => void; onDelete?: () => void }) {
  return (
    <div className="flex items-start gap-2 p-(--panel-pad)">
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium break-words">{role.name ?? <span className="font-mono text-muted-foreground">{role.id}</span>}</span>
          {role.isDefault && <Badge variant="secondary">Default</Badge>}
          {role.heldByModbot && <Badge variant="outline">Modbot</Badge>}
        </div>
        {role.description && (
          <p className="break-words whitespace-pre-wrap text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {role.description}
          </p>
        )}
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {permissionCount(role.permissions)}
        </span>
      </div>

      <div className="flex shrink-0 items-center gap-1">
        <Button variant="ghost" size="icon-sm" aria-label={`Edit ${role.name ?? 'role'}`} title="Edit" onClick={onEdit}>
          <Pencil />
        </Button>
        {onDelete && (
          <Button variant="ghost" size="icon-sm" aria-label={`Delete ${role.name ?? 'role'}`} title="Delete" onClick={onDelete}>
            <Trash2 />
          </Button>
        )}
      </div>
    </div>
  )
}

/** The new-role and edit form, in a dialog so the list stays where it was underneath. */
function RoleEditor({
  open,
  role,
  onOpenChange,
  onSaved,
}: {
  open: boolean
  /** The role being changed, or null for a new one. */
  role: GroupRoleRow | null
  onOpenChange: (open: boolean) => void
  onSaved: (role: GroupRoleRow, created: boolean) => void
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {/* Mounted only while open, so each opening starts from the role, not from the last draft. */}
      {open && (
        <DialogContent title={role ? 'Edit role' : 'New role'} className="max-w-[640px]">
          <RoleForm role={role} onCancel={() => onOpenChange(false)} onSaved={onSaved} />
        </DialogContent>
      )}
    </Dialog>
  )
}

function RoleForm({
  role,
  onCancel,
  onSaved,
}: {
  role: GroupRoleRow | null
  onCancel: () => void
  onSaved: (role: GroupRoleRow, created: boolean) => void
}) {
  const [draft, setDraft] = useState<RoleDraft>(() => (role ? roleDraftOf(role) : emptyRoleDraft()))
  const { saving, problem, missing, run } = useSave(role ? 'Could not save the role.' : 'Could not create the role.')
  const set = (change: Partial<RoleDraft>) => setDraft((d) => ({ ...d, ...change }))

  const body = roleBody(draft, role)
  const invalid = roleProblem(draft)
  const choices = permissionChoices(role?.permissions ?? [])

  const toggle = (id: string, on: boolean) =>
    set({ permissions: on ? [...draft.permissions, id] : draft.permissions.filter((p) => p !== id) })

  const send = () => {
    if (!body) return
    void run(async () => {
      const result = role ? await api.updateGroupRole(body) : await api.createGroupRole(body)
      onSaved(result.role, role === null)
    })
  }

  return (
    <div className="flex flex-col gap-3">
      <FieldRow label="Name" problem={draft.name && invalid}>
        {(id) => <TextBox id={id} value={draft.name} onChange={(name) => set({ name })} />}
      </FieldRow>

      <FieldRow label="Description">
        {(id) => <LongBox id={id} rows={3} value={draft.description} onChange={(description) => set({ description })} />}
      </FieldRow>

      <fieldset className="flex flex-col gap-1.5">
        <legend className="mb-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Permissions
        </legend>
        <div className="grid grid-cols-1 gap-x-4 gap-y-1.5 sm:grid-cols-2">
          {choices.map((choice) => (
            <Checkbox key={choice.id} checked={draft.permissions.includes(choice.id)} onChange={(on) => toggle(choice.id, on)}>
              {choice.label}
            </Checkbox>
          ))}
        </div>
      </fieldset>

      <SaveCancel
        saving={saving}
        disabled={invalid !== null || body === null}
        problem={problem}
        missing={missing}
        onCancel={onCancel}
        onSave={send}
        save={role ? 'Save' : 'Create'}
      />
    </div>
  )
}
