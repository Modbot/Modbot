/**
 * The id of a named part of a page, from its title: "VRChat worlds" is `section-vrchat-worlds`.
 * One function for the heading and for anything that jumps to it, so the two cannot drift apart.
 */
export function sectionId(title: string): string {
  return `section-${title.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`
}
