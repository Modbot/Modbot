/** Who may reply to Bluesky's post, and which languages it is written in: the choices of the New post box. */

/** The most languages one post names. The server's `BlueskyLanguages.Most`, and Bluesky's own limit. */
export const BLUESKY_LANGUAGES_MAX = 3

/** The groups a mix of repliers is made of, in the order the box shows them. */
export const REPLY_GROUPS = [
  { id: 'followers', label: 'Your followers' },
  { id: 'following', label: 'People you follow' },
  { id: 'mentioned', label: 'People you mention' },
] as const

/** Common languages by their code, with English names, sorted by name. */
export const BLUESKY_LANGUAGES: { code: string; name: string }[] = [
  { code: 'ar', name: 'Arabic' },
  { code: 'bn', name: 'Bengali' },
  { code: 'bg', name: 'Bulgarian' },
  { code: 'ca', name: 'Catalan' },
  { code: 'zh', name: 'Chinese' },
  { code: 'hr', name: 'Croatian' },
  { code: 'cs', name: 'Czech' },
  { code: 'da', name: 'Danish' },
  { code: 'nl', name: 'Dutch' },
  { code: 'en', name: 'English' },
  { code: 'fi', name: 'Finnish' },
  { code: 'fr', name: 'French' },
  { code: 'de', name: 'German' },
  { code: 'el', name: 'Greek' },
  { code: 'he', name: 'Hebrew' },
  { code: 'hi', name: 'Hindi' },
  { code: 'hu', name: 'Hungarian' },
  { code: 'id', name: 'Indonesian' },
  { code: 'it', name: 'Italian' },
  { code: 'ja', name: 'Japanese' },
  { code: 'ko', name: 'Korean' },
  { code: 'ms', name: 'Malay' },
  { code: 'no', name: 'Norwegian' },
  { code: 'fa', name: 'Persian' },
  { code: 'pl', name: 'Polish' },
  { code: 'pt', name: 'Portuguese' },
  { code: 'ro', name: 'Romanian' },
  { code: 'ru', name: 'Russian' },
  { code: 'sr', name: 'Serbian' },
  { code: 'sk', name: 'Slovak' },
  { code: 'es', name: 'Spanish' },
  { code: 'sv', name: 'Swedish' },
  { code: 'tl', name: 'Tagalog' },
  { code: 'th', name: 'Thai' },
  { code: 'tr', name: 'Turkish' },
  { code: 'uk', name: 'Ukrainian' },
  { code: 'vi', name: 'Vietnamese' },
]

/** A language's English name; a code not in the list shows as the code. */
export function languageName(code: string): string {
  return BLUESKY_LANGUAGES.find((l) => l.code === code)?.name ?? code
}

/** `picked` with `code` taken out when it is in, put last when it is not and there is room; full, nothing changes. */
export function withLanguageToggled(picked: string[], code: string): string[] {
  if (picked.includes(code)) return picked.filter((c) => c !== code)
  return picked.length >= BLUESKY_LANGUAGES_MAX ? picked : [...picked, code]
}

/** The button's text: the names picked, or "Language" with none. */
export function languagesLabel(picked: string[]): string {
  return picked.length === 0 ? 'Language' : picked.map(languageName).join(', ')
}

/** Anyone, nobody, or a mix of groups. */
export type ReplyKind = 'anyone' | 'nobody' | 'some'

export function replyKind(choice: string[]): ReplyKind {
  if (choice.includes('nobody')) return 'nobody'
  return REPLY_GROUPS.some((g) => choice.includes(g.id)) ? 'some' : 'anyone'
}

/** The groups the choice names, in the box's order. */
export function replyGroupsOf(choice: string[]): string[] {
  return REPLY_GROUPS.map((g) => g.id).filter((id) => choice.includes(id))
}

/** `choice` after the group is ticked or unticked; unticking the last group goes back to anyone. */
export function withReplyGroupToggled(choice: string[], group: string): string[] {
  const groups = replyGroupsOf(choice)
  const next = groups.includes(group) ? groups.filter((g) => g !== group) : [...groups, group]
  return next.length === 0 ? ['anyone'] : REPLY_GROUPS.map((g) => g.id).filter((id) => next.includes(id))
}

/** The chip's text: "Anyone can interact", or what is limited. */
export function interactLabel(choice: string[], quotesOff: boolean): string {
  const kind = replyKind(choice)
  if (kind === 'anyone' && !quotesOff) return 'Anyone can interact'

  let replies: string
  if (kind === 'anyone') {
    replies = 'Anyone can reply'
  } else if (kind === 'nobody') {
    replies = 'Nobody can reply'
  } else {
    const names = replyGroupsOf(choice).map((id, i) => {
      const label = REPLY_GROUPS.find((g) => g.id === id)!.label
      return i === 0 ? label : label.charAt(0).toLowerCase() + label.slice(1)
    })
    replies = `${names.length > 1 ? `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}` : names[0]} can reply`
  }

  return quotesOff ? `${replies}, no quote posts` : replies
}
