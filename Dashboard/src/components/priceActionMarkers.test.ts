import { test } from 'node:test'
import assert from 'node:assert/strict'
import { collectPriceActionMarkers } from './priceActionMarkers.ts'
import type { PriceActionEvent, ReplayFrame } from '../types'

/**
 * Regression fixture: real OANDA XAU/USD 5m bars from 2026-08-27, taken from
 * GET /api/workspaces/oanda/window. The 02:00 bar is red (4640.03 -> 4638.82) and produced a
 * BearishRejection; the 02:05 bar that follows is green (4638.90 -> 4642.30) and produced
 * nothing. The event is stamped confirmedAt=02:05 — the 02:05 bar's OPEN time — so any lookup
 * that matches confirmedAt against candle open times lands the bearish marker on the green bar.
 */
function candle(openTime: string, closeTime: string, open: number, high: number, low: number, close: number) {
  return { openTime, closeTime, open, high, low, close, volume: 0 }
}

function event(overrides: Partial<PriceActionEvent> = {}): PriceActionEvent {
  return {
    eventId: 'METAL:XAU/USD:5 Minute:538:BearishRejection',
    type: 'BearishRejection',
    direction: 'Bearish',
    confirmedAt: '2026-08-27T02:05:00+00:00',
    confirmedSequence: 538,
    referenceLevel: 4643.49,
    brokenLevel: null,
    retestLevel: null,
    atr: 6.42,
    strength: 65.98,
    confidence: 86.39,
    sourceSwingKey: null,
    sourceZoneKey: 'High:2026-08-26T04:30:00.0000000+00:00:4643.490',
    reasonCode: 'BearishStructuralRejection',
    explanation: 'An upper-wick rejection closed in the lower 90% of its range near confirmed resistance.',
    ...overrides,
  } as PriceActionEvent
}

function frame(candleValue: ReturnType<typeof candle>, events: PriceActionEvent[]): ReplayFrame {
  return {
    candle: candleValue,
    priceAction: { events },
  } as unknown as ReplayFrame
}

const bar0200 = candle('2026-08-27T02:00:00+00:00', '2026-08-27T02:05:00+00:00', 4640.03, 4643.21, 4638.32, 4638.82)
const bar0205 = candle('2026-08-27T02:05:00+00:00', '2026-08-27T02:10:00+00:00', 4638.90, 4642.66, 4638.32, 4642.30)

test('anchors an event to its own bar, not the bar its close time names', () => {
  const frames = [frame(bar0200, [event()]), frame(bar0205, [])]

  const markers = collectPriceActionMarkers(frames)

  assert.equal(markers.length, 1)
  // Index 0 is the 02:00 bar that produced the rejection. Index 1 would be the off-by-one bug.
  assert.equal(markers[0].index, 0)
  assert.equal(frames[markers[0].index].candle.openTime, '2026-08-27T02:00:00+00:00')
})

test('a bearish marker does not land on the following green candle', () => {
  const frames = [frame(bar0200, [event()]), frame(bar0205, [])]

  const [marker] = collectPriceActionMarkers(frames)
  const anchored = frames[marker.index].candle

  assert.equal(marker.bullish, false)
  // The bar the marker sits on must itself be bearish; the 02:05 bar is green.
  assert.ok(anchored.close < anchored.open, 'bearish marker must sit on a down candle')
})

test('every marker anchors to a frame that actually reported it', () => {
  const bullish = event({
    eventId: 'bullish-displacement',
    type: 'BullishDisplacement',
    direction: 'Bullish',
    referenceLevel: 4642.30,
    confirmedAt: bar0205.closeTime,
  })
  const frames = [frame(bar0200, [event()]), frame(bar0205, [bullish])]

  for (const marker of collectPriceActionMarkers(frames)) {
    const owning = frames[marker.index].priceAction!.events
    assert.ok(
      owning.some(candidate => candidate.eventId === marker.event.eventId),
      `${marker.event.eventId} anchored to a frame that did not report it`,
    )
  }
})

test('falls back to the owning bar close when the event carries no level', () => {
  const levelless = event({ referenceLevel: null, brokenLevel: null })
  const frames = [frame(bar0200, [levelless]), frame(bar0205, [])]

  const [marker] = collectPriceActionMarkers(frames)

  assert.equal(marker.reference, bar0200.close)
})

test('drops low-confidence and context-only events', () => {
  const frames = [
    frame(bar0200, [
      event({ eventId: 'weak', confidence: 54 }),
      event({ eventId: 'impulse', type: 'BullishImpulse', direction: 'Bullish' }),
      event({ eventId: 'pullback', type: 'BearishPullback' }),
      event({ eventId: 'kept' }),
    ]),
  ]

  const markers = collectPriceActionMarkers(frames)

  assert.deepEqual(markers.map(marker => marker.event.eventId), ['kept'])
})

test('keeps the first bar that reported a repeated event', () => {
  const repeated = event({ eventId: 'repeated' })
  const frames = [frame(bar0200, [repeated]), frame(bar0205, [repeated])]

  const markers = collectPriceActionMarkers(frames)

  assert.equal(markers.length, 1)
  assert.equal(markers[0].index, 0)
})
