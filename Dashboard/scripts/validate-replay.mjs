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
  let atrContextFrames = 0
  let bollingerContextFrames = 0
  let rsiRelationshipFrames = 0

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
    if (frame.indicators.atrAnalysis?.normalizedPercent != null) atrContextFrames++
    if (frame.indicators.bollingerAnalysis?.bandwidthPercent != null) bollingerContextFrames++
    if (frame.indicators.rsiAnalysis?.latestRelationship != null) rsiRelationshipFrames++
    if (frame.channels.length > 0) channelFrames++
  })

  assert(readyFrames > 0, `${series.interval}: indicators never completed warm-up.`)
  assert(atrContextFrames > 0, `${series.interval}: ATR context was never produced.`)
  assert(bollingerContextFrames > 0, `${series.interval}: Bollinger context was never produced.`)
  assert(rsiRelationshipFrames > 0, `${series.interval}: RSI swing relationships were never produced.`)
  totalFrames += series.frames.length
  console.log(
    `${series.interval}: ${series.frames.length} frames, ${readyFrames} ready, ` +
    `${channelFrames} with channels, ${rsiRelationshipFrames} with RSI relationships.`,
  )
}

console.log(`Replay validation passed for ${totalFrames} total frames.`)

function assert(condition, message) {
  if (!condition) throw new Error(message)
}
