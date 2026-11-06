# Health timeline view

A stress-manager page that redraws the portal's entity/signal timeline for all entities on one shared time axis, updates in near real time, and highlights where nodes disagree. Goal: see whether Degraded and Unhealthy hit all nodes at about the same time.

## Decisions

| # | Decision |
|---|---|
| T1 | Separate page `wwwroot/timeline.html` + `wwwroot/timeline.js`, linked from the main page header. Vanilla JS, no CDN, canvas rendering. |
| T2 | The server owns one in-memory cache of health model history (singleton). The browser never talks to ARM. |
| T3 | The server refreshes incrementally every 30 s: per entity, `GetSignalHistory`, `GetHistory` and `GetDataAnnotations` from `lastSeen - 2 min` (overlap absorbs late points), following `nextMarker`. Merge by `(entity, occurredAt)`. Retention and backfill: 24 h. |
| T4 | Clients get deltas: `GET /api/timeline?since=<cursor>` returns only points newer than the cursor. Server-sent events push the same delta shape when a refresh finds new data. |
| T5 | The browser caches everything in IndexedDB, renders from the cache immediately, then asks for `since=<cached cursor>`. Cache entries older than 24 h are pruned. |
| T6 | Run analysis (onset skew) and disagreement intervals are pure C# calculations with xUnit `[Theory]` tests, sent with every response. |

## API

`GET /api/timeline?since=<ISO 8601, optional>`

```json
{
  "cursor": "2026-11-05T21:52:00Z",
  "thresholds": { "degraded": 90, "unhealthy": 98 },
  "entities": [ { "name": "hm-aks-cpu-health", "displayName": "hm-aks-cpu-health", "kind": "root", "parent": null } ],
  "entityStates": [ { "entity": "aks-stress-...-vmss000000", "points": [[1762378800000, 1]] } ],
  "signals": [ { "entity": "aks-stress-...-vmss000000", "signal": "average-cpu", "points": [[1762378800000, 4.9, 1]] } ],
  "annotations": [ { "entity": "...", "id": "...", "t": 1762378800000, "phase": "start", "runId": "...", "minutes": 5, "load": 100, "source": "rhythm:0", "description": "..." } ],
  "runs": [ { "runId": "...", "startedAt": 1762378800000, "endedAt": 1762379100000, "nodes": [ { "entity": "...", "degradedAt": 1762379000000, "unhealthyAt": null, "recoveredAt": 1762379300000 } ], "spreadDegradedSec": 60, "spreadUnhealthySec": null } ],
  "disagreements": [ [1762379000000, 1762379060000] ]
}
```

- Times are Unix epoch milliseconds (UTC); the cursor is ISO 8601 UTC.
- State codes: `0` Unknown, `1` Healthy, `2` Degraded, `3` Unhealthy.
- `entities` are always complete. `kind` is `root`, `flow` or `node`; `parent` comes from the relationships.
- `entityStates`, `signals` and `annotations` contain only points with `occurredAt > since`. Without `since`, they contain the full 24 h.
- `runs` (last 24 h, grouped by `runId` from the annotations): for each node, the first Degraded-or-worse, the first Unhealthy, and the first Healthy after that. `spread*Sec` = max - min over the nodes that reached the state, or `null` if fewer than 2 nodes did.
- `disagreements`: intervals in the last 24 h where the node signal states differ (computed on a 1-minute grid), merged when adjacent.
- Responses are gzip-compressed.

`GET /api/timeline/stream`: `text/event-stream`. Event `delta` carries the JSON above, holding only what the last refresh added. Event `ping` comes every 30 s. The client reconnects with `?since=<cursor>` on the plain endpoint after an SSE drop, then re-opens the stream.

## Page

- Header: window 1 h / 3 h / 6 h / 12 h / 24 h (default 3 h), a Live toggle (keeps "now" at the right edge), the last update time, and a link back to `/`.
- Rows, in model order (root, flow, then nodes by name). Each entity gets:
  - a state band (entityStates)
  - for nodes, a signal state band and a signal value chart with dashed threshold lines at 90 and 98, red shading where Unhealthy and orange where Degraded
  - annotation markers on the state band: start/end markers, with pairs clustered into a count bubble when closer than 8 px, and a tooltip listing the runs
- Shared crosshair: hovering any row shows a vertical line across all rows, plus a tooltip with every node's value and state at that time.
- Difference options (toggles):
  - Disagreement lane: a top lane coloured where node states differ.
  - Highlight differences: dims the time where all nodes agree.
  - Overlay: one chart with all node CPU lines in distinct colours, plus the thresholds.
  - Onset skew table: per run, the start time, each node's time to Degraded and to Unhealthy (seconds after the run start), and the spread. A spread over 120 s is flagged.
- Drag to zoom on the time axis; double-click resets. Must work at phone width (rows stack, the chart width follows the viewport) and in light and dark mode.

## Raw CPU from the Azure Monitor Metrics API

The health model signal is a 5-minute average. The timeline also shows the raw 1-minute `Percentage CPU` of each node's VMSS instance, so the averaging delay is visible.

- Source: `GET https://management.azure.com{resourceId}/providers/Microsoft.Insights/metrics?api-version=2023-10-01&metricnames=Percentage%20CPU&interval=PT1M&aggregation=Average,Maximum&timespan={from}/{to}`, called with the manager identity (Reader on the node resource group covers `Microsoft.Insights/metrics/read`). `resourceId` is the node entity's `azureResourceId`.
- The cache refreshes every 60 s from `lastSeen - 5 min`, because the newest minutes arrive late and can change. Points with a null average are skipped. A later value for the same minute replaces the earlier one. Backfill and retention: 24 h.
- Response field `rawCpu`: `[{ "entity": "...", "points": [[t, average, maximum]] }]` with the values exactly as the Metrics API returns them. It follows the same `since` delta rules as `signals`. SSE deltas include it.
- Page:
  - each node chart draws every raw point as a gold dot (never downsampled, at any zoom) next to the 5-min signal, toggled by "Raw 1-min CPU (Metrics API)" (on by default)
  - the legend names both lines
  - the crosshair tooltip lists raw average and max per node
  - Overlay mode keeps showing the signal lines only
