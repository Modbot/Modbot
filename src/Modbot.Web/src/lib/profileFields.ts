/**
 * VRChat's own field names, as the profile facts record them, said the way a person would.
 *
 * A name this build has not seen is shown as recorded: a field the sync started diffing after
 * this was written is still a real change.
 */
export function fieldName(field: string): string {
  const names: Record<string, string> = {
    displayName: 'name',
    bio: 'bio',
    statusDescription: 'status line',
    pronouns: 'pronouns',
    currentAvatarImageUrl: 'avatar picture',
    currentAvatarThumbnailImageUrl: 'avatar thumbnail',
    profilePicOverride: 'profile picture',
    ageVerificationStatus: 'age status',
    ageVerified: 'age verified',
    dateJoined: 'join date',
    tags: 'tags',
  }
  return names[field] ?? field
}
