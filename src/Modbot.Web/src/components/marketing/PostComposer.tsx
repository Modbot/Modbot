import { useEffect, useMemo, useRef, useState } from 'react'
import { Check } from 'lucide-react'
import { ChannelPicker } from '@/components/discord/ChannelPicker'
import { RolePicker } from '@/components/discord/RolePicker'
import { CroppedPicture, PictureCrop } from '@/components/calendar/PictureCrop'
import { useCroppedPicture, useOpenGivenLink } from '@/components/calendar/useCroppedPicture'
import { Checkbox, Outcome } from '@/components/settings/fields'
import { Button } from '@/components/ui/button'
import { Chip } from '@/components/ui/chip'
import { Dialog, DialogContent, DialogFoot } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { SwitchBank } from '@/components/ui/switch-bank'
import { Tabs } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/lib/api'
import { useDiscordChannels } from '@/lib/discordLists'
import { useCanManageSettings } from '@/lib/manageSettings'
import {
  discordCount,
  pictureAddress,
  POST_PICTURE_MAX_BYTES,
  postsApi,
  requestOf,
  vrchatCount,
  vrchatNeedsTitle,
  vrchatPictureWanted,
  type Post,
  type PostInput,
  type PostRoleChoice,
  type PostSites,
  type VRChatInput,
} from '@/lib/posts'
import { cn } from '@/lib/utils'
import { PostPreviewPanel } from './PostPreview'

type Tab = 'write' | 'preview'

/** The picture's shapes (posts design §4.3): Discord shows any, so the choice is the writer's. */
type Shape = 'wide' | 'square' | 'whole'

const SHAPES: { value: Shape; label: string }[] = [
  { value: 'wide', label: '16:9' },
  { value: 'square', label: '1:1' },
  { value: 'whole', label: 'As it is' },
]

const ASPECT: Record<Shape, number | null> = { wide: 16 / 9, square: 1, whole: null }

/** A post's picture as VRChat has it. */
type VRChatPictureSent = { imageId: string; pictureId: string }

/**
 * Writing a post, or changing one before it goes (posts design §4.3).
 *
 * Where it goes is a row of chips, all off when the composer opens for a new post: nothing goes
 * anywhere unless the writer ticks it, and Preview is where they see what each site will get. Each
 * chip on opens its site's section. The buttons are pinned under the form, so a phone never scrolls
 * them away; the main one says Post now when Now is picked.
 */
export function PostComposer({
  post,
  initial,
  sites,
  roles,
  onClose,
  onSaved,
}: {
  /** The post being changed, or null for a new one. */
  post: Post | null
  initial: PostInput
  sites: PostSites
  /** The VRChat group's roles, for choosing who a VRChat post is for. */
  roles: readonly PostRoleChoice[]
  onClose: () => void
  onSaved: (saved: Post) => void
}) {
  const [input, setInput] = useState<PostInput>(initial)
  const [tab, setTab] = useState<Tab>('write')
  const [shown, setShown] = useState<PostInput | null>(null)
  const [shape, setShape] = useState<Shape>('wide')
  const [link, setLink] = useState('')
  const [busy, setBusy] = useState(false)
  const [problems, setProblems] = useState<string[] | null>(null)
  const channels = useDiscordChannels()
  const mayOpenSettings = useCanManageSettings()

  const set = <K extends keyof PostInput>(key: K, value: PostInput[K]) => setInput((current) => ({ ...current, [key]: value }))
  const setDiscord = <K extends keyof PostInput['discord']>(key: K, value: PostInput['discord'][K]) =>
    setInput((current) => ({ ...current, discord: { ...current.discord, [key]: value } }))
  const setVRChat = (change: Partial<VRChatInput>) =>
    setInput((current) => ({ ...current, vrChat: { ...current.vrChat, ...change } }))

  // The picture goes to VRChat when VRChat is ticked with one, while VRChat picture uploads are on
  // (decision 14): once per picture, since VRChat takes one upload a minute and none is sent again.
  // Each picture sent, by the send itself so a second ask waits on the first; and VRChat's words
  // for each one it refused.
  const vrchatSends = useRef(new Map<string, Promise<VRChatPictureSent | null>>())
  const [vrchatErrors, setVRChatErrors] = useState<Record<string, string>>({})
  const [vrchatUploading, setVRChatUploading] = useState(false)

  const sendPictureToVRChat = (pictureId: string): Promise<VRChatPictureSent | null> => {
    const sending = vrchatSends.current.get(pictureId)
    if (sending) return sending

    setVRChatUploading(true)

    const send = postsApi
      .uploadVRChatPicture(pictureId)
      .then((sent) => {
        setInput((current) => ({ ...current, vrChat: { ...current.vrChat, imageId: sent.imageId, imagePictureId: sent.pictureId } }))
        return sent
      })
      .catch((e: unknown) => {
        const said = e instanceof ApiError ? e.message : 'Could not send the picture to VRChat.'
        setVRChatErrors((current) => ({ ...current, [pictureId]: said }))
        return null
      })
      .finally(() => setVRChatUploading(false))

    vrchatSends.current.set(pictureId, send)
    return send
  }

  // Only when the tick, the picture or the switch changes; the function itself is new every render.
  const wantsVRChatPicture = vrchatPictureWanted(input, sites.vrChatPictures)
  useEffect(() => {
    if (wantsVRChatPicture && input.pictureId) void sendPictureToVRChat(input.pictureId)
  }, [wantsVRChatPicture, input.pictureId])

  // The error shown is the one for the picture the post has now.
  const vrchatPictureError = input.pictureId ? (vrchatErrors[input.pictureId] ?? null) : null

  const picture = useCroppedPicture({
    aspect: ASPECT[shape],
    maxBytes: POST_PICTURE_MAX_BYTES,
    send: (file) => postsApi.uploadPicture(file).then((r) => r.pictureId),
    onUploaded: (id) => set('pictureId', id),
    initialLink: null,
    fetchLink: postsApi.pictureFromLink,
  })

  useOpenGivenLink(picture, link.trim() || null, true)

  const zones = useMemo(() => {
    try {
      return Intl.supportedValuesOf('timeZone')
    } catch {
      return ['UTC']
    }
  }, [])

  // The channel's kind decides whether Publish to followers is offered.
  const channel = channels.data?.channels.find((c) => c.id === input.discord.channelId)
  const announcement = channel?.type === 'announcement'

  // A channel that is not an Announcement channel cannot publish, so the tick is not sent with it.
  const notAnnouncement = channel !== undefined && !announcement
  const sending: PostInput = notAnnouncement && input.discord.publish ? { ...input, discord: { ...input.discord, publish: false } } : input

  const count = discordCount(input)
  const vrchatCounter = vrchatCount(input)
  const needsTitle = vrchatNeedsTitle(input)
  const isDraft = !post || post.status === 'draft'

  const save = async (draft: boolean) => {
    setBusy(true)
    setProblems(null)

    let saving = sending

    // A crop still in its box goes up first; refused, nothing is saved and the field says why.
    if (picture.draft) {
      const id = await picture.upload()
      if (!id) {
        setBusy(false)
        setTab('write')
        return
      }
      saving = { ...saving, pictureId: id }

      // The crop went up just now, so VRChat has not had it yet: sent once, here. Refused, nothing
      // is saved and the VRChat section says why; saving again sends VRChat the text only.
      if (vrchatPictureWanted(saving, sites.vrChatPictures)) {
        const sent = await sendPictureToVRChat(id)
        if (!sent) {
          setBusy(false)
          setTab('write')
          return
        }
        saving = { ...saving, vrChat: { ...saving.vrChat, imageId: sent.imageId, imagePictureId: sent.pictureId } }
      }
    } else if (input.pictureId && vrchatPictureWanted(saving, sites.vrChatPictures) && vrchatSends.current.has(input.pictureId)) {
      // A send to VRChat still on its way is waited for, so the post goes with what it answers.
      const sent = await vrchatSends.current.get(input.pictureId)
      if (sent) saving = { ...saving, vrChat: { ...saving.vrChat, imageId: sent.imageId, imagePictureId: sent.pictureId } }
    }

    const body = requestOf(saving, draft, post?.version ?? null, sites.vrChatPictures)
    const request = post ? postsApi.update(post.id, body) : postsApi.create(body)

    request
      .then(onSaved)
      .catch((e: unknown) => setProblems(e instanceof ApiError ? problemsOf(e) : ['Could not save the post.']))
      .finally(() => setBusy(false))
  }

  const pickTab = (next: Tab) => {
    if (next === 'preview') setShown(sending)
    setTab(next)
  }

  const toggleRole = (id: string, on: boolean) =>
    setVRChat({ roleIds: on ? [...input.vrChat.roleIds, id] : input.vrChat.roleIds.filter((r) => r !== id) })

  const croppedPreview = picture.draft ? (
    <CroppedPicture picture={picture.draft.picture} box={picture.draft.box} aspect={picture.aspect} className="rounded-sm" />
  ) : null

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={post ? 'Edit post' : 'New post'}
        className="max-w-[720px]"
        foot={
          <DialogFoot>
            {problems && (
              <span role="alert" className="mr-auto flex min-w-0 basis-full flex-col sm:basis-auto">
                {problems.map((line) => (
                  <Outcome key={line} tone="problem">
                    {line}
                  </Outcome>
                ))}
              </span>
            )}
            <Button size="sm" variant="outline" disabled={busy} onClick={onClose}>
              Cancel
            </Button>
            {isDraft && (
              <Button size="sm" variant="outline" disabled={busy || picture.uploading || vrchatUploading} onClick={() => void save(true)}>
                Save draft
              </Button>
            )}
            <Button size="sm" disabled={busy || picture.uploading || vrchatUploading} onClick={() => void save(false)}>
              {input.when === 'now' ? 'Post now' : isDraft ? 'Schedule' : 'Save'}
            </Button>
          </DialogFoot>
        }
      >
        <div className="flex flex-col gap-4">
          <div role="group" aria-label="Where it goes" className="flex flex-wrap items-center gap-2">
            <span className="inline-flex items-center gap-1.5">
              <Chip on={input.discord.on} onClick={() => setDiscord('on', !input.discord.on)}>
                {input.discord.on && <Check className="size-3.5" />}
                Discord
              </Chip>
              {input.discord.on && !sites.discordOn ? (
                <SiteState label="Off" to="/settings#posts" link={mayOpenSettings} />
              ) : (
                input.discord.on &&
                !sites.discordSetUp && <SiteState label="Not set up" to="/settings#discord" link={mayOpenSettings} />
              )}
            </span>
            <span className="inline-flex items-center gap-1.5">
              <Chip on={input.vrChat.on} onClick={() => setVRChat({ on: !input.vrChat.on })}>
                {input.vrChat.on && <Check className="size-3.5" />}
                VRChat
              </Chip>
              {input.vrChat.on && !sites.vrChatOn ? (
                <SiteState label="Off" to="/settings#posts" link={mayOpenSettings} />
              ) : (
                input.vrChat.on &&
                !sites.vrChatSetUp && <SiteState label="Not set up" to="/settings#vrchat" link={mayOpenSettings} />
              )}
              {needsTitle && (
                <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
                  Needs a title
                </span>
              )}
            </span>
          </div>

          <Tabs
            value={tab}
            onChange={pickTab}
            tabs={[
              { value: 'write', label: 'Write' },
              { value: 'preview', label: 'Preview' },
            ]}
            panelClassName="overflow-visible pt-4"
          >
            {tab === 'preview' && shown ? (
              <PostPreviewPanel input={shown} croppedPicture={croppedPreview} vrChatPictures={sites.vrChatPictures} />
            ) : (
              <div className="flex flex-col gap-4">
                <Labelled label="Title">
                  <Input value={input.title} onChange={(e) => set('title', e.target.value)} />
                </Labelled>
                <div className="flex flex-col gap-1">
                  <Labelled label="Text">
                    <Textarea rows={6} value={input.text} onChange={(e) => set('text', e.target.value)} />
                  </Labelled>
                  <div className="flex flex-wrap gap-x-3">
                    {input.discord.on && !input.discord.ownText && <Counter count={count} />}
                    {input.vrChat.on && !input.vrChat.ownText && <Counter count={vrchatCounter} />}
                  </div>
                </div>

                <Section title="Picture">
                  <PictureField
                    picture={picture}
                    pictureId={input.pictureId}
                    shape={shape}
                    onShape={setShape}
                    link={link}
                    onLink={setLink}
                    onRemove={() => set('pictureId', null)}
                  />
                </Section>

                {input.discord.on && (
                  <Section title="Discord">
                    <ChannelPicker
                      label="Channel"
                      value={input.discord.channelId}
                      onChange={(id) => setDiscord('channelId', id)}
                      needs={
                        input.pictureId || picture.draft
                          ? ['viewChannel', 'sendMessages', 'readMessageHistory', 'attachFiles']
                          : ['viewChannel', 'sendMessages', 'readMessageHistory']
                      }
                      allowNone={false}
                    />
                    <RolePicker
                      label="Mention role"
                      value={input.discord.roleId}
                      onChange={(id) => setDiscord('roleId', id)}
                      needsMention
                    />
                    {announcement && (
                      <Checkbox checked={input.discord.publish} onChange={(v) => setDiscord('publish', v)}>
                        Publish to followers
                      </Checkbox>
                    )}
                    <Checkbox
                      checked={input.discord.ownText}
                      onChange={(v) =>
                        setInput((current) => ({
                          ...current,
                          discord: { ...current.discord, ownText: v, text: v && !current.discord.text ? current.text : current.discord.text },
                        }))
                      }
                    >
                      Own text
                    </Checkbox>
                    {input.discord.ownText && (
                      <div className="flex flex-col gap-1">
                        <Textarea
                          rows={5}
                          aria-label="Discord text"
                          value={input.discord.text}
                          onChange={(e) => setDiscord('text', e.target.value)}
                        />
                        <div className="flex items-center justify-between gap-2">
                          <Counter count={count} />
                          <Button size="xs" variant="outline" onClick={() => setDiscord('text', input.text)}>
                            Reset
                          </Button>
                        </div>
                      </div>
                    )}
                  </Section>
                )}

                {input.vrChat.on && (
                  <Section title="VRChat">
                    <Labelled label="Who sees it">
                      <SwitchBank
                        value={input.vrChat.visibility}
                        onChange={(visibility) => setVRChat({ visibility })}
                        label="Who sees it"
                        size="sm"
                        options={[
                          { value: 'group', label: 'Group' },
                          { value: 'public', label: 'Everyone' },
                        ]}
                      />
                    </Labelled>
                    {input.vrChat.visibility === 'group' && roles.length > 0 && (
                      <fieldset className="flex flex-col gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
                        <legend className="mb-1 text-muted-foreground">Roles</legend>
                        <div className="grid grid-cols-1 gap-x-4 gap-y-1.5 sm:grid-cols-2">
                          {roles.map((role) => (
                            <Checkbox
                              key={role.id}
                              checked={input.vrChat.roleIds.includes(role.id)}
                              onChange={(on) => toggleRole(role.id, on)}
                            >
                              {role.name}
                            </Checkbox>
                          ))}
                        </div>
                      </fieldset>
                    )}
                    <Checkbox checked={input.vrChat.notify} onChange={(notify) => setVRChat({ notify })}>
                      Notify members
                    </Checkbox>
                    <Checkbox
                      checked={input.vrChat.ownTitle}
                      onChange={(v) =>
                        setInput((current) => ({
                          ...current,
                          vrChat: { ...current.vrChat, ownTitle: v, title: v && !current.vrChat.title ? current.title : current.vrChat.title },
                        }))
                      }
                    >
                      Own title
                    </Checkbox>
                    {input.vrChat.ownTitle && (
                      <div className="flex items-center gap-2">
                        <Input
                          aria-label="VRChat title"
                          value={input.vrChat.title}
                          onChange={(e) => setVRChat({ title: e.target.value })}
                        />
                        <Button size="xs" variant="outline" onClick={() => setVRChat({ title: input.title })}>
                          Reset
                        </Button>
                      </div>
                    )}
                    <Checkbox
                      checked={input.vrChat.ownText}
                      onChange={(v) =>
                        setInput((current) => ({
                          ...current,
                          vrChat: { ...current.vrChat, ownText: v, text: v && !current.vrChat.text ? current.text : current.vrChat.text },
                        }))
                      }
                    >
                      Own text
                    </Checkbox>
                    {input.vrChat.ownText && (
                      <div className="flex flex-col gap-1">
                        <Textarea
                          rows={5}
                          aria-label="VRChat text"
                          value={input.vrChat.text}
                          onChange={(e) => setVRChat({ text: e.target.value })}
                        />
                        <div className="flex items-center justify-between gap-2">
                          <Counter count={vrchatCounter} />
                          <Button size="xs" variant="outline" onClick={() => setVRChat({ text: input.text })}>
                            Reset
                          </Button>
                        </div>
                      </div>
                    )}
                    {vrchatUploading && (
                      <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                        Sending the picture to VRChat…
                      </span>
                    )}
                    {vrchatPictureError && <Outcome tone="problem">{vrchatPictureError}</Outcome>}
                  </Section>
                )}

                <Section title="When">
                  <SwitchBank
                    value={input.when}
                    onChange={(v) => set('when', v)}
                    label="When"
                    options={[
                      { value: 'now', label: 'Now' },
                      { value: 'later', label: 'Later' },
                    ]}
                  />
                  {input.when === 'later' && (
                    <div className="grid gap-3 sm:grid-cols-2">
                      <Labelled label="Date and time">
                        <Input type="datetime-local" value={input.sendAt} onChange={(e) => set('sendAt', e.target.value)} />
                      </Labelled>
                      <Labelled label="Time zone">
                        <Select value={input.timeZone} onChange={(v) => set('timeZone', v)} aria-label="Time zone">
                          {!zones.includes(input.timeZone) && <option value={input.timeZone}>{input.timeZone}</option>}
                          {zones.map((z) => (
                            <option key={z} value={z}>
                              {z}
                            </option>
                          ))}
                        </Select>
                      </Labelled>
                    </div>
                  )}
                </Section>
              </div>
            )}
          </Tabs>
        </div>
      </DialogContent>
    </Dialog>
  )
}

/** The picture: its shape, Choose picture or a link, the crop box while one is open, and the one set. */
function PictureField({
  picture,
  pictureId,
  shape,
  onShape,
  link,
  onLink,
  onRemove,
}: {
  picture: ReturnType<typeof useCroppedPicture>
  pictureId: string | null
  shape: Shape
  onShape: (shape: Shape) => void
  link: string
  onLink: (link: string) => void
  onRemove: () => void
}) {
  const { draft, opening, uploading, error } = picture

  return (
    <div className="flex flex-col gap-2" style={{ fontSize: 'var(--text-small)' }}>
      <SwitchBank value={shape} onChange={onShape} options={SHAPES} label="Shape" size="sm" />
      {draft ? (
        <>
          <PictureCrop picture={draft.picture} aspect={picture.aspect} box={draft.box} onBox={picture.setBox} label="Picture crop" />
          <div className="flex flex-wrap items-center gap-2">
            <Button size="sm" disabled={uploading} onClick={() => void picture.upload()}>
              {uploading ? 'Uploading…' : 'Upload'}
            </Button>
            <Button size="sm" variant="outline" disabled={uploading} onClick={picture.cancel}>
              Cancel
            </Button>
          </div>
        </>
      ) : (
        <div className="flex flex-col gap-2">
          {pictureId && (
            <div className="flex flex-wrap items-end gap-2">
              <img src={pictureAddress(pictureId)} alt="" className="max-h-40 max-w-full rounded-sm border border-(length:--hairline)" />
              <Button size="xs" variant="outline" onClick={onRemove}>
                Remove
              </Button>
            </div>
          )}
          <ChooseFile picture={picture} opening={opening} />
          <Labelled label="Picture link">
            <Input value={link} placeholder="https://" onChange={(e) => onLink(e.target.value)} />
          </Labelled>
        </div>
      )}
      {error && <Outcome tone="problem">{error}</Outcome>}
    </div>
  )
}

function ChooseFile({ picture, opening }: { picture: ReturnType<typeof useCroppedPicture>; opening: boolean }) {
  return (
    <label className="w-fit">
      <span
        className={cn(
          'inline-flex h-[calc(var(--control-h)-0.125rem)] cursor-pointer items-center rounded-sm border border-(length:--hairline) border-input bg-card px-2.5 font-medium hover:bg-muted',
          opening && 'pointer-events-none opacity-50',
        )}
      >
        {opening ? 'Opening…' : 'Choose picture'}
      </span>
      <input
        type="file"
        accept="image/*"
        className="sr-only"
        aria-label="Choose picture"
        onChange={(e) => {
          const file = e.target.files?.[0]
          if (file) picture.openFile(file)
          // The same file can be chosen again after a refusal.
          e.target.value = ''
        }}
      />
    </label>
  )
}

/**
 * "Off" or "Not set up" beside a site's chip, linking to where it is set up when the person may open
 * it. In a new tab, so what was typed here stays.
 */
function SiteState({ label, to, link }: { label: string; to: string; link: boolean }) {
  if (!link) {
    return (
      <span className="text-destructive" style={{ fontSize: 'var(--text-small)' }}>
        {label}
      </span>
    )
  }

  return (
    <a
      href={to}
      target="_blank"
      rel="noreferrer"
      className="text-destructive underline underline-offset-2"
      style={{ fontSize: 'var(--text-small)' }}
    >
      {label}
    </a>
  )
}

/** One site's counter under the text: "Discord 212 / 2000", red once over. */
function Counter({ count }: { count: { label: string; over: boolean } }) {
  return (
    <span className={cn('font-mono', count.over ? 'text-destructive' : 'text-muted-foreground')} style={{ fontSize: 'var(--text-small)' }}>
      {count.label}
    </span>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-3 border-t border-t-(length:--hairline) pt-3">
      <legend className="pr-2 font-label">{title}</legend>
      {children}
    </fieldset>
  )
}

function Labelled({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
      <span className="text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

/** Every problem a refused save names, in the server's order: its `problems`, or its one sentence. */
function problemsOf(e: ApiError): string[] {
  const detail = e.detail as { problems?: unknown } | null
  const problems = Array.isArray(detail?.problems) ? detail.problems.filter((p): p is string => typeof p === 'string') : []
  return problems.length > 0 ? problems : [e.message]
}
