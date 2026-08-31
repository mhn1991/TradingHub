import { test } from 'node:test'
import assert from 'node:assert/strict'
import { indexForCloseStampedTime } from './chartTime.ts'

/**
 * Real OANDA XAU/USD 5m bars from 2026-08-27. Contiguous, so each bar's close time is exactly
 * the next bar's open time — the collision that put overlays one bar to the right.
 */
const bars = [
  '2026-08-27T01:55:00+00:00',
  '2026-08-27T02:00:00+00:00',
  '2026-08-27T02:05:00+00:00',
  '2026-08-27T02:10:00+00:00',
].map(value => new Date(value).getTime())

const at = (value: string) => new Date(value).getTime()

test('a close-stamped time resolves to the bar that produced it, not the next one', () => {
  // The 02:00 bar closes at 02:05, which is also the 02:05 bar's open time.
  assert.equal(indexForCloseStampedTime(bars, at('2026-08-27T02:05:00+00:00')), 1)
})

test('every bar close maps back to its own bar', () => {
  const closes = ['02:00', '02:05', '02:10', '02:15']
  closes.forEach((close, index) => {
    assert.equal(
      indexForCloseStampedTime(bars, at(`2026-08-27T${close}:00+00:00`)),
      index,
      `close ${close} should map to bar ${index}`,
    )
  })
})

test('a stamp inside a bar resolves to that bar', () => {
  assert.equal(indexForCloseStampedTime(bars, at('2026-08-27T02:03:30+00:00')), 1)
})

test('a stamp at or before the first open clamps to the first visible bar', () => {
  assert.equal(indexForCloseStampedTime(bars, at('2026-08-27T01:55:00+00:00')), 0)
  assert.equal(indexForCloseStampedTime(bars, at('2026-08-26T22:00:00+00:00')), 0)
})

test('a stamp past the last bar clamps to the last bar', () => {
  assert.equal(indexForCloseStampedTime(bars, at('2026-08-27T09:00:00+00:00')), bars.length - 1)
})

test('resolves across a weekend gap to the last bar before it', () => {
  // Friday 20:55 close, then Sunday 22:00 open — a stamp inside the gap belongs to the Friday bar.
  const gapped = [
    at('2026-08-21T20:50:00+00:00'),
    at('2026-08-21T20:55:00+00:00'),
    at('2026-08-23T22:00:00+00:00'),
  ]
  assert.equal(indexForCloseStampedTime(gapped, at('2026-08-21T21:00:00+00:00')), 1)
  assert.equal(indexForCloseStampedTime(gapped, at('2026-08-22T12:00:00+00:00')), 1)
})

test('reports -1 when there are no candles', () => {
  assert.equal(indexForCloseStampedTime([], at('2026-08-27T02:05:00+00:00')), -1)
})

test('is monotonic — a later stamp never resolves to an earlier bar', () => {
  let previous = -1
  for (let minute = 55; minute <= 75; minute += 1) {
    const stamp = at('2026-08-27T01:00:00+00:00') + minute * 60_000
    const index = indexForCloseStampedTime(bars, stamp)
    assert.ok(index >= previous, `index went backwards at minute ${minute}`)
    previous = index
  }
})
