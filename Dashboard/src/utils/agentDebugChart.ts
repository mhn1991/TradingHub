export interface DebugCandle {
  time: string
  open: number
  high: number
  low: number
  close: number
}

export interface CandleLayout {
  upBodies: string
  downBodies: string
  upWicks: string
  downWicks: string
  slot: number
  centerX: (index: number) => number
  /** Maps a price to a y-pixel using the same scale as the candle bodies/wicks — for drawing indicator overlays (Bollinger bands). */
  y: (value: number) => number
}

/** Minimal, self-contained SVG candle layout builder — deliberately not shared with the main
 * dashboard's candleGeometry.ts, so this page has no coupling to the ReplayFrame contract. */
export function buildCandleLayout(
  candles: DebugCandle[],
  width: number,
  height: number,
  padTop = 4,
  padBottom = 4,
): CandleLayout | null {
  if (candles.length === 0) return null

  const slot = width / candles.length
  const bodyWidth = Math.max(1.5, slot * 0.62)
  let min = Infinity
  let max = -Infinity
  for (const candle of candles) {
    min = Math.min(min, candle.low)
    max = Math.max(max, candle.high)
  }
  const span = max - min || 1
  const plotHeight = height - padTop - padBottom
  const y = (value: number) => padTop + (1 - (value - min) / span) * plotHeight

  let upBodies = ''
  let downBodies = ''
  let upWicks = ''
  let downWicks = ''

  candles.forEach((candle, i) => {
    const cx = i * slot + slot / 2
    const up = candle.close >= candle.open
    const yOpen = y(candle.open)
    const yClose = y(candle.close)
    const top = Math.min(yOpen, yClose)
    const bodyHeight = Math.max(1.2, Math.abs(yClose - yOpen))

    const bodyPath = `M${(cx - bodyWidth / 2).toFixed(1)} ${top.toFixed(1)} h${bodyWidth.toFixed(1)} v${bodyHeight.toFixed(1)} h${(-bodyWidth).toFixed(1)} Z `
    const wickPath = `M${cx.toFixed(1)} ${y(candle.high).toFixed(1)} V${y(candle.low).toFixed(1)} `
    if (up) {
      upBodies += bodyPath
      upWicks += wickPath
    } else {
      downBodies += bodyPath
      downWicks += wickPath
    }
  })

  return { upBodies, downBodies, upWicks, downWicks, slot, centerX: (i) => i * slot + slot / 2, y }
}
