import { test } from 'node:test'
import assert from 'node:assert/strict'
import { drawnTrade, threeRTrade } from './tradeDrawing.ts'

test('manual target determines ratio without being moved to 3R', () => {
  assert.equal(drawnTrade('Sell', 100, 110, 80)?.ratio, 2)
  assert.equal(drawnTrade('Sell', 100, 110, 85)?.ratio, 1.5)
  assert.equal(drawnTrade('Buy', 100, 90, 125)?.ratio, 2.5)
})
test('editing entry or stop leaves target independent', () => {
  assert.equal(drawnTrade('Sell', 100, 120, 80)?.target, 80)
  assert.equal(drawnTrade('Sell', 100, 120, 80)?.ratio, 1)
  assert.equal(drawnTrade('Sell', 105, 110, 80)?.ratio, 5)
})
test('manual drawing rejects crossed levels and non-finite targets', () => {
  assert.equal(drawnTrade('Sell', 100, 110, 105), null)
  assert.equal(drawnTrade('Buy', 100, 90, 95), null)
  assert.equal(drawnTrade('Buy', 100, 100, 120), null)
  assert.equal(drawnTrade('Buy', 100, 90, Infinity), null)
})

test('sell projects three risk distances below entry', () => {
  const trade = threeRTrade('Sell', 4364.84, 4389.70)!
  assert.ok(Math.abs(trade.target - 4290.26) < 1e-8)
  assert.ok(Math.abs((trade.entry - trade.target) / trade.risk - 3) < 1e-8)
})
test('buy projects three risk distances above entry', () => {
  assert.deepEqual(threeRTrade('Buy', 100, 95), { side: 'Buy', entry: 100, stop: 95, target: 115, risk: 5 })
})
test('invalid, zero-risk and wrong-side stops cannot create a drawing', () => {
  for (const side of ['Buy', 'Sell'] as const) {
    assert.equal(threeRTrade(side, 100, 100), null)
    assert.equal(threeRTrade(side, NaN, 95), null)
    assert.equal(threeRTrade(side, 100, Infinity), null)
  }
  assert.equal(threeRTrade('Buy', 100, 105), null)
  assert.equal(threeRTrade('Sell', 100, 95), null)
})
