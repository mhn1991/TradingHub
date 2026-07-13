export const price = (value: number | null | undefined): string => {
  if (value == null) return '—'
  const absolute = Math.abs(value)
  const decimals = absolute >= 1_000 ? 2 : absolute >= 10 ? 3 : 5
  return value.toFixed(decimals)
}

export const compact = (value: number): string =>
  new Intl.NumberFormat('en-GB', {
    notation: 'compact',
    maximumFractionDigits: 1,
  }).format(value)

export const timestamp = (value: string): string =>
  new Intl.DateTimeFormat('en-GB', {
    day: '2-digit',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
    timeZone: 'UTC',
  }).format(new Date(value))

export const shortTime = (value: string): string =>
  new Intl.DateTimeFormat('en-GB', {
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
    timeZone: 'UTC',
  }).format(new Date(value))

export const duration = (microseconds: number): string => {
  if (microseconds < 1_000) return `${microseconds.toFixed(0)} µs`
  return `${(microseconds / 1_000).toFixed(2)} ms`
}
