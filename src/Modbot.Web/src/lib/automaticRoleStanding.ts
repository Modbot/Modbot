import type { AutomaticRoleGiven, RuleCondition } from './automaticRoles.ts'

/** The settings page calls this condition "Missing clips"; the server's list says "Missed clips". */
export function conditionName(condition: RuleCondition): string {
  return condition.label === 'Missed clips' ? 'Missing clips' : condition.label
}

/**
 * The numbers behind a counted condition: "1 of at most 0, 30 days", the daily ban cap without
 * the days. Nothing for a yes-or-no condition, and nothing when the rule's number is missing.
 */
export function conditionNumbers(condition: RuleCondition): string | null {
  if (condition.count === null || condition.max === null) return null

  const days = condition.days === null ? '' : `, ${condition.days} ${condition.days === 1 ? 'day' : 'days'}`

  return `${condition.count} of at most ${condition.max}${days}`
}

/** A condition as one short chip in the Holders list: the name, and "1/2" for a counted one. */
export function conditionChipText(condition: RuleCondition): string {
  const name = conditionName(condition)

  return condition.count === null || condition.max === null ? name : `${name} ${condition.count}/${condition.max}`
}

const givenLabels: Record<AutomaticRoleGiven, string> = {
  ByModbot: 'By Modbot',
  ByHand: 'By hand',
  NotHeld: 'Not held',
}

/** Who put the role on the person, in the words the Holders list uses. */
export function givenLabel(given: AutomaticRoleGiven): string {
  return givenLabels[given]
}
