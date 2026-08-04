import type { ReplayFrame } from '../types'

export interface CandleLayout {
  upBodies: string
  downBodies: string
  upWicks: string
  downWicks: string
  /** Pixel width of one candle's slot — callers use this to place their own overlays (anchor lines, highlight bands) in the same coordinate space. */
  slot: number
  /** Center-x of the candle at `index` within the rendered frame list. */
  centerX: (index: number) => number
}

/** Builds SVG path strings for a candle series, plus enough layout info for a caller to draw
 * aligned overlays on top (anchor markers, highlight bands) without recomputing the price scale. */
export function buildCandleLayout(
  frames: ReplayFrame[],
  width: number,
  height: number,
  padTop = 4,
  padBottom = 4,
): CandleLayout | null {
  if (frames.length === 0) return null

  const slot = width / frames.length
  const bodyWidth = Math.max(1.5, slot * 0.62)
  let min = Infinity
  let max = -Infinity
  for (const frame of frames) {
    min = Math.min(min, frame.candle.low)
    max = Math.max(max, frame.candle.high)
  }
  const span = (max - min) || 1
  const plotHeight = height - padTop - padBottom
  const y = (value: number) => padTop + (1 - (value - min) / span) * plotHeight

  let upBodies = ''
  let downBodies = ''
  let upWicks = ''
  let downWicks = ''

  frames.forEach((frame, i) => {
    const cx = i * slot + slot / 2
    const up = frame.candle.close >= frame.candle.open
    const yOpen = y(frame.candle.open)
    const yClose = y(frame.candle.close)
    const top = Math.min(yOpen, yClose)
    const bodyHeight = Math.max(1.2, Math.abs(yClose - yOpen))

    const bodyPath = `M${(cx - bodyWidth / 2).toFixed(1)} ${top.toFixed(1)} h${bodyWidth.toFixed(1)} v${bodyHeight.toFixed(1)} h${(-bodyWidth).toFixed(1)} Z `
    const wickPath = `M${cx.toFixed(1)} ${y(frame.candle.high).toFixed(1)} V${y(frame.candle.low).toFixed(1)} `
    if (up) {
      upBodies += bodyPath
      upWicks += wickPath
    } else {
      downBodies += bodyPath
      downWicks += wickPath
    }
  })

  return { upBodies, downBodies, upWicks, downWicks, slot, centerX: (i) => i * slot + slot / 2 }
}
