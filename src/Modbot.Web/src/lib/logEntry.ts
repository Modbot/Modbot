import type { LogLine } from './api'

/**
 * One log line as a single record, ready to be shown and copied as JSON.
 *
 * The page used to show the time, the source, the area and the template as a little list and then
 * the property bag as JSON beside it, so copying the JSON gave a fragment: the properties without
 * the line they belonged to. Somebody pasting a line into a bug report wants the whole thing, so
 * the whole thing is one record.
 *
 * The keys are the words the screen uses rather than the ones the API sends (`time`, not `at`),
 * and they come in the order a person reads them: when, how bad, what it said, then where it came
 * from, then the detail.
 *
 * The row id is left out. It numbers a row in one deployment's table and means nothing in the
 * place a copied line ends up.
 */
export function wholeEntry(line: LogLine): Record<string, unknown> {
  const entry: Record<string, unknown> = {
    time: line.at,
    level: line.level,
    message: line.message,
  }

  // The template is what the message was written from. When it is the message word for word it
  // adds nothing, and a record that repeats itself is a record people stop reading.
  if (line.template && line.template !== line.message) entry.template = line.template

  if (line.source) entry.source = line.source
  if (line.area) entry.area = line.area

  const carried = properties(line.properties)
  if (carried !== null) entry.properties = carried

  if (line.exception) entry.exception = line.exception

  return entry
}

/**
 * The stored properties as a record, or nothing when the line carried none.
 *
 * A document that will not parse is kept as the text it arrived as rather than dropped: what is on
 * the screen is what the server answered, and a line nobody can read is still evidence.
 */
function properties(json: string): unknown {
  if (!json) return null

  let parsed: unknown
  try {
    parsed = JSON.parse(json)
  } catch {
    return json
  }

  if (parsed === null) return null
  if (typeof parsed === 'object' && !Array.isArray(parsed) && Object.keys(parsed).length === 0) {
    return null
  }

  return parsed
}
