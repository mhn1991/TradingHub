import { readFile } from 'node:fs/promises'

const path = new URL('../public/data/sample-replay.json', import.meta.url)
const replay = JSON.parse(await readFile(path, 'utf8'))

assert(replay.schemaVersion === 1, 'Expected replay schema version 1.')
assert(Array.isArray(replay.series) && replay.series.length > 0, 'Expected at least one series.')

let totalFrames = 0
for (const series of replay.series) {
  assert(series.intervalSeconds > 0, `${series.interval}: intervalSeconds must be positive.`)
  assert(Array.isArray(series.frames) && series.frames.length > 0, `${series.interval}: frames are missing.`)
  let readyFrames = 0
  let channelFrames = 0

  series.frames.forEach((frame, index) => {
    assert(frame.index === index, `${series.interval}: frame index ${frame.index} is out of sequence.`)
    assert(frame.candle.high >= Math.max(frame.candle.open, frame.candle.close), `${series.interval}/${index}: high is invalid.`)
    assert(frame.candle.low <= Math.min(frame.candle.open, frame.candle.close), `${series.interval}/${index}: low is invalid.`)
    if (index > 0) {
      const elapsedSeconds = (
        Date.parse(frame.availableAt) - Date.parse(series.frames[index - 1].availableAt)
      ) / 1_000
      assert(
        Math.abs(elapsedSeconds - series.intervalSeconds) <= 1,
        `${series.interval}/${index}: expected a continuous candle interval.`,
      )
    }
    for (const swing of frame.swings) {
      assert(
        Date.parse(swing.confirmedAt) <= Date.parse(frame.availableAt),
        `${series.interval}/${index}: a swing appears before its confirmation time.`,
      )
    }
    if (frame.indicators.rsi != null && frame.indicators.bollingerMiddle != null) readyFrames++
    if (frame.channels.length > 0) channelFrames++
  })

  assert(readyFrames > 0, `${series.interval}: indicators never completed warm-up.`)
  totalFrames += series.frames.length
  console.log(
    `${series.interval}: ${series.frames.length} frames, ${readyFrames} ready, ` +
    `${channelFrames} with channels.`,
  )
}

console.log(`Replay validation passed for ${totalFrames} total frames.`)

function assert(condition, message) {
  if (!condition) throw new Error(message)
}
