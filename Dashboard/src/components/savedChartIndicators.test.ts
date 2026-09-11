import { test } from 'node:test'
import assert from 'node:assert/strict'
import { savedChartIndicators } from './savedChartIndicators.ts'

const candles = (closes: number[]) => closes.map(close => ({ high: close, low: close, close }))
const near = (actual: number | null | undefined, expected: number) => assert.ok(actual != null && Math.abs(actual - expected) < 1e-8, `${actual} != ${expected}`)

test('indicators wait for full windows; empty input is safe', () => {
  assert.deepEqual(savedChartIndicators([]), [])
  const values = savedChartIndicators(candles(Array.from({ length: 20 }, (_, i) => i + 1)))
  assert.ok(values.slice(0, 14).every(v => v.rsi === null))
  assert.ok(values.slice(0, 19).every(v => v.bollingerMiddle === null && v.cci === null))
  near(values[14]?.rsi, 100)
  near(values[19]?.bollingerMiddle, 10.5)
})

test('Bollinger uses a rolling 20-close population deviation; CCI uses mean deviation', () => {
  const values = savedChartIndicators(candles(Array.from({ length: 21 }, (_, i) => i + 1)))
  near(values[19]?.bollingerUpper, 10.5 + 2 * Math.sqrt(33.25))
  near(values[19]?.bollingerLower, 10.5 - 2 * Math.sqrt(33.25))
  near(values[20]?.bollingerMiddle, 11.5)
  near(values[20]?.cci, 9.5 / (0.015 * 5))
})

test('CCI uses high, low and close, not close alone', () => {
  const rows = candles(Array(20).fill(100))
  rows[19] = { high: 130, low: 100, close: 100 }
  const last = savedChartIndicators(rows).at(-1)!
  near(last.cci, 9.5 / (0.015 * 0.95))
  near(last.bollingerUpper, 100)
})

test('RSI uses Wilder smoothing after its initial 14 changes', () => {
  const values = savedChartIndicators(candles([44.34, 44.09, 44.15, 43.61, 44.33, 44.83, 45.10, 45.42, 45.84, 46.08, 45.89, 46.03, 45.61, 46.28, 46.28, 46.00]))
  near(values[14]?.rsi, 70.46413502109705)
  near(values[15]?.rsi, 66.24961855355505)
})

test('flat and falling histories remain finite', () => {
  const flat = savedChartIndicators(candles(Array(30).fill(100))).at(-1)!
  near(flat.rsi, 50)
  near(flat.cci, 0)
  near(flat.bollingerUpper, 100)
  near(flat.bollingerLower, 100)
  near(savedChartIndicators(candles(Array.from({ length: 30 }, (_, i) => 100 - i))).at(-1)?.rsi, 0)
})

test('future candles cannot change earlier values and separate timeframe calls do not share state', () => {
  const history = candles(Array.from({ length: 50 }, (_, i) => 100 + Math.sin(i) * 5))
  const before = savedChartIndicators(history)
  const withFuture = savedChartIndicators([...history, ...candles([500, 10, 200])])
  assert.deepEqual(withFuture.slice(0, history.length), before)
  savedChartIndicators(candles(Array(100).fill(900)))
  assert.deepEqual(savedChartIndicators(history), before)
})
