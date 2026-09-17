import assert from 'node:assert/strict'
import { test } from 'node:test'
import { EVENTS, PEOPLE, clock, duration, initialLive, step, type LiveState } from '../src/mock/simulation.ts'

const run = (ticks: number): LiveState[] => {
  const states = [initialLive()]
  for (let i = 0; i < ticks; i++) states.push(step(states[states.length - 1]))
  return states
}

test('the evening plays the same way every time, so the server and the browser agree', () => {
  assert.deepEqual(run(30), run(30))
})

test('after one loop every instance holds the people it started with', () => {
  const [first] = run(0)
  const afterLoop = run(EVENTS.length).at(-1)!

  for (const [index, instance] of first.instances.entries()) {
    const ids = (r: typeof instance) => r.people.map((p) => p.personId).sort()
    assert.deepEqual(ids(afterLoop.instances[index]), ids(instance))
    assert.equal(afterLoop.instances[index].headCount, instance.headCount)
  }
})

test('nobody is in two instances at once, and a watched instance counts exactly who is there', () => {
  for (const state of run(EVENTS.length * 3)) {
    const everyone = state.instances.flatMap((r) => r.people.map((p) => p.personId))
    assert.equal(new Set(everyone).size, everyone.length)

    for (const instance of state.instances) {
      assert.ok(instance.headCount >= 0)
      assert.ok(instance.peak >= instance.headCount)
      if (instance.watching.length > 0) assert.equal(instance.headCount, instance.people.length)
    }
  }
})

test('every event names a person who exists', () => {
  const ids = new Set(PEOPLE.map((p) => p.id))
  for (const event of EVENTS) if (event.kind !== 'count') assert.ok(ids.has(event.person), event.person)
})

test('the clock keeps moving forward', () => {
  const minutes = run(50).map((s) => s.minute)
  for (let i = 1; i < minutes.length; i++) assert.ok(minutes[i] > minutes[i - 1])
})

test('times read the way the app writes them', () => {
  assert.equal(clock(0), '21:00')
  assert.equal(clock(104), '22:44')
  assert.equal(clock(200), '00:20')
  assert.equal(duration(134), '2h 14m')
  assert.equal(duration(9), '9m')
})
