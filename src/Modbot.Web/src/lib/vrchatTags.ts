/**
 * What a VRChat profile's `tags` and `last_platform` say, in words.
 *
 * The tag list is VRChat's grab-bag: trust rank, subscription, languages, staff, early adopter,
 * feature access, and whatever they add next. A moderator should read the ones that mean something
 * as a word and a colour, and never have to decode `language_jpn` or `system_supporter`. Everything
 * this file does not know stays as the raw tag, shown behind a toggle, so nothing is hidden and
 * nothing is guessed.
 *
 * Tag names come from `.agent/research/2026-09-16-vrchat-trust-ranks.md` §1 and the community tag
 * list it cites. `last_platform` is free text on the wire (the entity says so), so an unknown value
 * is shown as sent, never treated as an error.
 */

export type Platform = {
  /** The word on the badge. */
  word: string
  /** Which picture goes beside it. `other` is a value this build has no word for. */
  icon: 'pc' | 'phone' | 'other'
  /** False when `word` is the raw value rather than a name. */
  known: boolean
}

const PLATFORMS: Record<string, Platform> = {
  standalonewindows: { word: 'PC', icon: 'pc', known: true },
  android: { word: 'Android', icon: 'phone', known: true },
  ios: { word: 'iOS', icon: 'phone', known: true },
}

/** The platform a `last_platform` value names, or the value itself when this build has no word for it. */
export function platformOf(lastPlatform: string | null | undefined): Platform | null {
  if (!lastPlatform) return null
  const trimmed = lastPlatform.trim()
  if (trimmed === '') return null
  return PLATFORMS[trimmed.toLowerCase()] ?? { word: trimmed, icon: 'other', known: false }
}

export type TagBadge =
  | { kind: 'vrcplus' }
  | { kind: 'language'; code: string; name: string }
  | { kind: 'early-adopter' }
  | { kind: 'staff' }
  | { kind: 'nuisance' }

export type ParsedTags = {
  /** The tags with a badge of their own, in the order they are drawn. */
  badges: TagBadge[]
  /** Every tag that neither has a badge nor is shown somewhere else on the profile. */
  rest: string[]
}

/**
 * The trust-rank tags. Shown as the trust rank badge, which the server computes from these, so
 * they are not repeated as raw tags. `system_trust_intermediate` and `_advanced` are old
 * subdivisions that are not ranks (research §1) and stay raw.
 */
const RANK_TAGS = new Set([
  'system_trust_basic',
  'system_trust_known',
  'system_trust_trusted',
  'system_trust_veteran',
  'system_trust_legend',
])

/** Shown by the 18+ card, from the profile's own age fields, so not repeated here. */
const AGE_TAGS = new Set(['age_verified', 'system_age_verified'])

const NUISANCE_TAGS = new Set(['system_troll', 'system_probable_troll'])

/** Draws one badge per kind, however many tags say the same thing. */
export function parseTags(tags: readonly string[] | null | undefined): ParsedTags {
  const badges: TagBadge[] = []
  const rest: string[] = []
  const seen = new Set<string>()

  let vrcplus = false
  let early = false
  let staff = false
  let nuisance = false
  const languages: string[] = []

  for (const raw of tags ?? []) {
    if (typeof raw !== 'string') continue
    const tag = raw.trim()
    if (tag === '' || seen.has(tag)) continue
    seen.add(tag)

    if (tag === 'system_supporter') vrcplus = true
    else if (tag === 'system_early_adopter') early = true
    else if (tag.startsWith('admin_')) staff = true
    else if (NUISANCE_TAGS.has(tag)) nuisance = true
    else if (tag.startsWith('language_') && tag.length > 'language_'.length) languages.push(tag.slice('language_'.length))
    else if (RANK_TAGS.has(tag) || AGE_TAGS.has(tag)) continue
    else rest.push(tag)
  }

  // The order the row reads in: what the account is, then what it can do, then how it speaks.
  if (staff) badges.push({ kind: 'staff' })
  if (nuisance) badges.push({ kind: 'nuisance' })
  if (vrcplus) badges.push({ kind: 'vrcplus' })
  if (early) badges.push({ kind: 'early-adopter' })
  for (const code of languages) badges.push({ kind: 'language', code, name: languageName(code) })

  return { badges, rest }
}

/**
 * The languages VRChat lets a profile list, by the three-letter code it stores them under. Mostly
 * ISO 639-3; the sign languages are the codes VRChat uses for them. A code missing here is shown as
 * the code.
 */
const LANGUAGES: Record<string, string> = {
  eng: 'English',
  kor: 'Korean',
  rus: 'Russian',
  spa: 'Spanish',
  por: 'Portuguese',
  zho: 'Chinese',
  cmn: 'Mandarin',
  yue: 'Cantonese',
  deu: 'German',
  jpn: 'Japanese',
  fra: 'French',
  swe: 'Swedish',
  nld: 'Dutch',
  pol: 'Polish',
  dan: 'Danish',
  nor: 'Norwegian',
  ita: 'Italian',
  tha: 'Thai',
  fin: 'Finnish',
  hun: 'Hungarian',
  ces: 'Czech',
  tur: 'Turkish',
  ara: 'Arabic',
  ron: 'Romanian',
  vie: 'Vietnamese',
  ukr: 'Ukrainian',
  ind: 'Indonesian',
  msa: 'Malay',
  tgl: 'Filipino',
  fil: 'Filipino',
  hrv: 'Croatian',
  srp: 'Serbian',
  bos: 'Bosnian',
  slv: 'Slovenian',
  slk: 'Slovak',
  bul: 'Bulgarian',
  ell: 'Greek',
  heb: 'Hebrew',
  hin: 'Hindi',
  ben: 'Bengali',
  urd: 'Urdu',
  fas: 'Persian',
  est: 'Estonian',
  lav: 'Latvian',
  lit: 'Lithuanian',
  cat: 'Catalan',
  eus: 'Basque',
  glg: 'Galician',
  afr: 'Afrikaans',
  swa: 'Swahili',
  tam: 'Tamil',
  tel: 'Telugu',
  isl: 'Icelandic',
  gle: 'Irish',
  cym: 'Welsh',
  lat: 'Latin',
  epo: 'Esperanto',
  tok: 'Toki Pona',
  ase: 'American Sign Language',
  bfi: 'British Sign Language',
  dse: 'Dutch Sign Language',
  fsl: 'French Sign Language',
  jsl: 'Japanese Sign Language',
  kvk: 'Korean Sign Language',
}

/** The name of a language VRChat's `language_*` tag names, or the code when this build has none. */
export function languageName(code: string): string {
  return LANGUAGES[code.toLowerCase()] ?? code
}

/** The words the collapsed-tags control shows for a count of hidden tags. */
export function moreTagsLabel(count: number): string {
  return count === 1 ? '1 more tag' : `${count} more tags`
}
