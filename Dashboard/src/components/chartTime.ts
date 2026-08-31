/**
 * Resolve a *close-stamped* timestamp to the index of the candle that produced it.
 *
 * The annotator stamps zones, liquidity pools and liquidity events with their source candle's
 * CLOSE time (`LiquidityAnalyzer.cs:80`, `SupplyDemandAnalyzer.cs:60`:
 * `availableAt = currentCandle.CloseTime ?? currentCandle.OpenTime`). For contiguous candles a
 * close time is exactly the NEXT candle's open time, so matching such a stamp against candle
 * open times resolves to the bar *after* the one that produced it — drawing every overlay one
 * bar to the right.
 *
 * Bar `i` owns the half-open interval `(openTimes[i], closeTimes[i]]`, so the producing bar is
 * the last one whose open time is strictly before the stamp.
 *
 * @param openTimes Ascending candle open times, in epoch milliseconds.
 * @param value Close-stamped instant, in epoch milliseconds.
 * @returns Index into `openTimes`, clamped to the array; `-1` when there are no candles.
 */
export function indexForCloseStampedTime(openTimes: readonly number[], value: number): number {
  if (openTimes.length === 0) return -1

  // First index whose open time is at or after the stamp; the bar before it owns the stamp.
  let low = 0
  let high = openTimes.length
  while (low < high) {
    const middle = (low + high) >>> 1
    if (openTimes[middle] < value) low = middle + 1
    else high = middle
  }

  // Stamps at or before the first open time belong to the first visible bar: the producing
  // candle has scrolled out of the window, so the overlay starts at the window edge.
  return low === 0 ? 0 : low - 1
}
