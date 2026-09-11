export type DrawingSide = 'Buy' | 'Sell'
export type DrawingLevel = 'entry' | 'stop' | 'target'

export function drawnTrade(side: DrawingSide, entry: number, stop: number, target: number) {
  if (![entry, stop, target].every(Number.isFinite)) return null
  const risk = side === 'Buy' ? entry - stop : stop - entry
  const reward = side === 'Buy' ? target - entry : entry - target
  if (risk <= 0 || reward <= 0 || !Number.isFinite(reward / risk)) return null
  return { side, entry, stop, target, risk, reward, ratio: reward / risk }
}

/** Price-distance illustration only; excludes costs and does not place an order. */
export function threeRTrade(side: DrawingSide, entry: number, stop: number) {
  if (![entry, stop].every(Number.isFinite)) return null
  const risk = side === 'Buy' ? entry - stop : stop - entry
  if (risk <= 0) return null
  const target = entry + (side === 'Buy' ? 3 : -3) * risk
  if (!Number.isFinite(target)) return null
  return { side, entry, stop, target, risk }
}
