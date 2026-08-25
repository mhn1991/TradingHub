<script setup lang="ts">
import { computed } from 'vue'
import type { IndicatorSnapshot } from '../../types'

const props = defineProps<{
  indicators: IndicatorSnapshot | null
  /** Resampled rows have no valid per-interval indicators yet — show a placeholder instead of a wrong number. */
  unavailable?: boolean
}>()

function rsiClass(value: number | null | undefined): string {
  if (value == null) return 'neutral'
  if (value >= 70 || value <= 30) return 'hot'
  return value >= 50 ? 'up' : 'down'
}

function cciClass(value: number | null | undefined): string {
  if (value == null) return 'neutral'
  if (Math.abs(value) >= 100) return 'hot'
  return value >= 0 ? 'up' : 'down'
}

const rsi = computed(() => props.indicators?.rsi ?? null)
const cci = computed(() => props.indicators?.cci ?? null)
const atr = computed(() => props.indicators?.atr ?? null)
</script>

<template>
  <div class="indicator-badges" :title="unavailable ? 'Resampled timeframe — indicators need a per-interval analysis export' : undefined">
    <div class="ind-badge">
      <span class="k">RSI</span>
      <span class="v mono" :class="rsiClass(rsi)">{{ rsi != null ? rsi.toFixed(1) : '—' }}</span>
    </div>
    <div class="ind-badge">
      <span class="k">CCI</span>
      <span class="v mono" :class="cciClass(cci)">{{ cci != null ? Math.round(cci) : '—' }}</span>
    </div>
    <div class="ind-badge">
      <span class="k">ATR</span>
      <span class="v mono neutral">{{ atr != null ? atr.toFixed(4) : '—' }}</span>
    </div>
  </div>
</template>

<style scoped>
.indicator-badges {
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: 5px;
  padding: 8px 10px;
  border-left: 1px solid var(--line-soft);
  background: var(--ink-1);
  flex: none;
  width: 108px;
}
.ind-badge {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 6px;
  font-size: 10.5px;
}
.ind-badge .k {
  color: var(--muted-2);
  font-weight: 600;
  letter-spacing: 0.02em;
}
.ind-badge .v {
  font-size: 11.5px;
  font-weight: 700;
}
.ind-badge .v.neutral { color: var(--text); }
.ind-badge .v.hot { color: var(--amber); }
.ind-badge .v.up { color: var(--mint); }
.ind-badge .v.down { color: var(--coral); }
</style>
