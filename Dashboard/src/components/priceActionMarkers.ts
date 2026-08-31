import type { PriceActionEvent, ReplayFrame } from '../types'

/**
 * A price-action marker bound to the index of the frame that produced it.
 *
 * Events carry `confirmedAt`, which the analyzer stamps with the source candle's CLOSE time
 * (`PriceActionAnalyzer.cs`: `ConfirmedAt = candle.CloseTime ?? candle.OpenTime`). For any
 * interval that close time is exactly the NEXT candle's open time, so resolving a marker's x
 * position by matching `confirmedAt` against candle open times placed every marker one bar to
 * the right of its own candle. Anchoring by frame index instead keeps a marker on the bar whose
 * geometry produced it.
 */
export interface PriceActionMarker {
  event: PriceActionEvent
  /** Index into the visible frame list — the bar that produced the event. */
  index: number
  bullish: boolean
  /** Price level the marker is drawn at. */
  reference: number
}

/** Events below this confidence are noise on the chart. */
const MINIMUM_MARKER_CONFIDENCE = 55

/** Measured context rather than actionable signals; they would swamp the chart. */
function isContextOnly(type: string): boolean {
  return type.endsWith('Impulse') || type.endsWith('Pullback')
}

/**
 * Collect the price-action markers for a visible frame window, each anchored to the index of
 * the frame that produced it. De-duplicates by event id, keeping the earliest frame that
 * reported the event — the analyzer repeats a still-live event on later frames, and the first
 * sighting is the bar it actually belongs to.
 */
export function collectPriceActionMarkers(frames: readonly ReplayFrame[]): PriceActionMarker[] {
  const markers = new Map<string, PriceActionMarker>()
  frames.forEach((frame, index) => {
    for (const event of frame.priceAction?.events ?? []) {
      if (event.confidence < MINIMUM_MARKER_CONFIDENCE) continue
      if (isContextOnly(event.type)) continue
      if (markers.has(event.eventId)) continue
      markers.set(event.eventId, {
        event,
        index,
        bullish: event.direction === 'Bullish',
        reference: event.referenceLevel ?? event.brokenLevel ?? frame.candle.close,
      })
    }
  })
  return [...markers.values()]
}
