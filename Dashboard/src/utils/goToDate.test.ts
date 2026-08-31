import { test } from 'node:test'
import assert from 'node:assert/strict'
import { resolveGoToDate } from './timeframeSeries.ts'
import type { ReplayFrame } from '../types'

/** Minimal frames — resolveGoToDate only reads availableAt. */
const frames = [
  '2026-01-05T00:00:00+00:00',
  '2026-01-05T00:05:00+00:00',
  '2026-01-05T00:10:00+00:00',
  '2026-01-05T00:15:00+00:00',
].map(availableAt => ({ availableAt }) as unknown as ReplayFrame)

test('lands on the exact frame when the time matches one', () => {
  const result = resolveGoToDate(frames, '2026-01-05T00:10')
  assert.equal(result.index, 2)
})

test('falls back to the nearest frame at or before the target', () => {
  // 00:12 sits between frames; the candle in force at that instant is the 00:10 one.
  const result = resolveGoToDate(frames, '2026-01-05T00:12')
  assert.equal(result.index, 2)
})

test('reads the input as UTC, not the viewer local time', () => {
  // The decisive case: frames are UTC-stamped, and a local reading would shift the target by the
  // viewer's offset — silently landing on the wrong candle for anyone outside UTC.
  const result = resolveGoToDate(frames, '2026-01-05T00:05')
  assert.equal(result.index, 1)
})

test('accepts an explicit zone offset without double-suffixing', () => {
  const result = resolveGoToDate(frames, '2026-01-05T00:10:00Z')
  assert.equal(result.index, 2)
})

test('reports a date before the run rather than clamping to the first candle', () => {
  const result = resolveGoToDate(frames, '2026-01-04T23:00')
  assert.equal(result.index, undefined)
  assert.match(result.error!, /before this run starts/)
})

test('reports a date after the run rather than clamping to the last candle', () => {
  const result = resolveGoToDate(frames, '2026-01-05T09:00')
  assert.equal(result.index, undefined)
  assert.match(result.error!, /after this run ends/)
})

test('rejects unparseable input', () => {
  assert.match(resolveGoToDate(frames, 'not-a-date').error!, /not a valid date/)
})

test('rejects empty input', () => {
  assert.match(resolveGoToDate(frames, '   ').error!, /Enter a date/)
})

test('reports no frames rather than throwing', () => {
  assert.match(resolveGoToDate([], '2026-01-05T00:00').error!, /No frames/)
})
