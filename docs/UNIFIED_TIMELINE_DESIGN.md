# Unified MR/MESH timeline contract (Web PR-4)

Status: `NOT_STARTED`; PR-1 only reserves the public component boundary.

The future `TimelineWorkspace` will own one UTC-millisecond time state:

```ts
interface TimelineState {
  fullStartMs: number
  fullEndMs: number
  viewStartMs: number
  viewEndMs: number
  hoverMs?: number
  pinnedMs?: number
  revision: number
  sourceOffsetsMs: Record<string, number>
  precision: 'seconds' | 'milliseconds'
}
```

Panels receive immutable series with explicit gaps; display downsampling never changes Raw evidence. A shared controller publishes viewport, hover, pinned time and revision. Panel-originated updates carry a source/revision guard to prevent feedback loops. Data adapters must preserve AP identity, repeated timestamps, mixed second/millisecond precision, clock offsets, missing rows and unlinked log lines.

The planned panel registry includes main RSSI, trackside AP, MESH link state, switch events, channel busy, Ping quality, MR-CT/MR-CW, link detail, anomaly summary and raw log. Each panel supports resize, maximize, synchronized cursor and source-location detail. Real analysis data will be connected only after the Core parser contract and deterministic fixtures exist.

