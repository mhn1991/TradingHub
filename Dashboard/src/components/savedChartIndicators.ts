type Candle = { high: number; low: number; close: number }
type Indicators = {
  bollingerMiddle: number | null; bollingerUpper: number | null; bollingerLower: number | null
  rsi: number | null; cci: number | null
}

// Chart-only values, seeded from the saved history, never from future candles.
// Match ChartAnnotator defaults: BB(20, 2), Wilder RSI(14), Lambert CCI(20).
export function savedChartIndicators(candles: readonly Candle[]): Indicators[] {
  const closes: number[] = []
  const typicals: number[] = []
  let averageGain = 0
  let averageLoss = 0
  return candles.map((candle, index) => {
    const value: Indicators = { bollingerMiddle: null, bollingerUpper: null, bollingerLower: null, rsi: null, cci: null }
    if (index > 0) {
      const change = candle.close - candles[index - 1]!.close
      const gain = Math.max(change, 0)
      const loss = Math.max(-change, 0)
      if (index <= 14) {
        averageGain += gain / 14
        averageLoss += loss / 14
      } else {
        averageGain = (averageGain * 13 + gain) / 14
        averageLoss = (averageLoss * 13 + loss) / 14
      }
      if (index >= 14) value.rsi = averageLoss === 0
        ? (averageGain === 0 ? 50 : 100) : 100 - 100 / (1 + averageGain / averageLoss)
    }
    closes.push(candle.close)
    const typical = (candle.high + candle.low + candle.close) / 3
    typicals.push(typical)
    if (closes.length > 20) { closes.shift(); typicals.shift() }
    if (closes.length === 20) {
      const middle = closes.reduce((sum, price) => sum + price, 0) / 20
      const width = 2 * Math.sqrt(closes.reduce((sum, price) => sum + (price - middle) ** 2, 0) / 20)
      value.bollingerMiddle = middle
      value.bollingerUpper = middle + width
      value.bollingerLower = middle - width
      const mean = typicals.reduce((sum, price) => sum + price, 0) / 20
      const deviation = typicals.reduce((sum, price) => sum + Math.abs(price - mean), 0) / 20
      value.cci = deviation === 0 ? 0 : (typical - mean) / (0.015 * deviation)
    }
    return value
  })
}
