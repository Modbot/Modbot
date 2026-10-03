import { useEffect, useState, type ReactNode } from 'react'
import { DiscordText, RoleMention } from '@/components/calendar/EventPreview'
import { Outcome } from '@/components/settings/fields'
import { ApiError } from '@/lib/api'
import { Badge } from '@/components/ui/badge'
import {
  postsApi,
  requestOf,
  vrchatAudience,
  type DiscordPostPreview,
  type PostInput,
  type PostPreview,
  type VRChatPostPreview,
} from '@/lib/posts'
import { cn } from '@/lib/utils'

/**
 * The composer's Preview (posts design §4.4): each ticked site's post drawn the way the site shows
 * it, from what the server would send, built by the sender's own code. Read once when it opens; the
 * fields are not on screen while it is, so nothing can change under it. What would stop the post
 * being scheduled is listed above.
 */
export function PostPreviewPanel({
  input,
  croppedPicture,
  vrChatPictures,
}: {
  input: PostInput
  croppedPicture: ReactNode
  /** VRChat picture uploads are on: VRChat is sent the picture it has, otherwise text only. */
  vrChatPictures: boolean
}) {
  const [preview, setPreview] = useState<PostPreview | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    postsApi
      .preview(requestOf(input, false, null, vrChatPictures))
      .then((p) => {
        if (!cancelled) setPreview(p)
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof ApiError ? e.message : 'Could not draw the preview.')
      })

    return () => {
      cancelled = true
    }
  }, [input, vrChatPictures])

  if (error) return <Outcome tone="problem">{error}</Outcome>
  if (!preview) return <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>Loading…</p>

  return (
    <div className="flex flex-col gap-4" style={{ fontSize: 'var(--text-small)' }}>
      {preview.problems.length > 0 && (
        <div role="alert" className="flex flex-col">
          {preview.problems.map((p) => (
            <Outcome key={p} tone="problem">
              {p}
            </Outcome>
          ))}
        </div>
      )}
      {preview.discord && <DiscordMessage message={preview.discord} picture={croppedPicture} />}
      {preview.vrChat && <VRChatPost post={preview.vrChat} />}
    </div>
  )
}

/**
 * A VRChat group post as the group's Posts page shows it: the title, who sees it, the picture when
 * VRChat is sent one, and the text. No picture is drawn when VRChat gets text only.
 */
export function VRChatPost({ post }: { post: VRChatPostPreview }) {
  return (
    <section className="flex flex-col gap-1.5">
      <h3 className="font-label text-muted-foreground">VRChat</h3>
      <article className="flex flex-col gap-2 rounded-sm border-(length:--hairline) bg-card p-3">
        <div className="flex flex-wrap items-center gap-2">
          <span className="font-medium [overflow-wrap:anywhere]" style={{ fontSize: 'calc(var(--text-base) * 1.15)' }}>
            {post.title ?? <span className="text-muted-foreground">Untitled</span>}
          </span>
          <Badge variant={post.visibility === 'public' ? 'outline' : 'secondary'}>{vrchatAudience(post.visibility, post.roleNames)}</Badge>
        </div>
        {post.pictureUrl && (
          <img src={post.pictureUrl} alt="" className="max-h-72 max-w-full self-start rounded-sm" loading="lazy" />
        )}
        {post.text && <p className="whitespace-pre-wrap [overflow-wrap:anywhere]">{post.text}</p>}
      </article>
      <span className="font-mono text-muted-foreground">{post.length}</span>
    </section>
  )
}

/** A Discord message as the channel shows it: the role line, the bold title, the text, the picture. */
export function DiscordMessage({ message, picture }: { message: DiscordPostPreview; picture?: ReactNode }) {
  const now = new Date()

  return (
    <section className="flex flex-col gap-1.5">
      <h3 className="font-label text-muted-foreground">
        Discord{message.channelName ? ` · #${message.channelName}` : ''}
      </h3>
      <div className="flex flex-col gap-1 rounded-sm border-(length:--hairline) bg-card p-3">
        {message.roleName && (
          <div>
            <RoleMention name={message.roleName} colour={message.roleColour} />
          </div>
        )}
        {message.title && <div className="font-semibold [overflow-wrap:anywhere]">{message.title}</div>}
        {message.text && (
          <p className="whitespace-pre-wrap [overflow-wrap:anywhere]">
            <DiscordText text={message.text} now={now} />
          </p>
        )}
        {picture ??
          (message.pictureUrl && (
            <img src={message.pictureUrl} alt="" className="mt-1 max-h-72 max-w-full self-start rounded-sm" loading="lazy" />
          ))}
      </div>
      <span className={cn('font-mono', message.length > message.limit ? 'text-destructive' : 'text-muted-foreground')}>
        {message.length} / {message.limit}
      </span>
    </section>
  )
}
