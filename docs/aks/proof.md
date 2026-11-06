# AKS health model proof

Generated 2026-11-05T20:53:55Z by `scripts/aks-proof.sh 2026-11-05T20:10:00Z` against `hm-aks-cpu-health` (API 2026-11-01, `getSignalHistory` + `getDataAnnotations`).

Run `20261105-201448`: 20 min, 100% load, started from the UI. Run `20261105-204502`: 10 min, from the `evening` CronJob (`45 20 * * *`).

## aks-stress-45060198-vmss000000

Signal history (average-cpu), state changes only:

- 2026-11-05T20:14:25Z  Healthy  4.6%
- 2026-11-05T20:22:29Z  Degraded  97.1%
- 2026-11-05T20:23:29Z  Unhealthy  99.6%
- 2026-11-05T20:38:34Z  Healthy  83.1%
- 2026-11-05T20:52:39Z  Degraded  94%
- 2026-11-05T20:53:39Z  Unhealthy  99.6%

Data annotations:

- 2026-11-05T20:14:49Z  start  run 20261105-201448  Stress test start: 20 min (ui)
- 2026-11-05T20:35:03Z  end  run 20261105-201448  Stress test end: 20 min (ui)
- 2026-11-05T20:45:03Z  start  run 20261105-204502  Stress test start: 10 min (schedule:evening)

## aks-stress-45060198-vmss000001

Signal history (average-cpu), state changes only:

- 2026-11-05T20:14:26Z  Healthy  4.6%
- 2026-11-05T20:21:29Z  Unhealthy  99.6%
- 2026-11-05T20:37:34Z  Degraded  97.9%
- 2026-11-05T20:38:34Z  Healthy  74.6%
- 2026-11-05T20:51:39Z  Degraded  97.1%
- 2026-11-05T20:52:39Z  Unhealthy  99.7%

Data annotations:

- 2026-11-05T20:14:49Z  start  run 20261105-201448  Stress test start: 20 min (ui)
- 2026-11-05T20:35:03Z  end  run 20261105-201448  Stress test end: 20 min (ui)
- 2026-11-05T20:45:03Z  start  run 20261105-204502  Stress test start: 10 min (schedule:evening)

## aks-stress-45060198-vmss000002

Signal history (average-cpu), state changes only:

- 2026-11-05T20:14:26Z  Healthy  4.6%
- 2026-11-05T20:21:29Z  Degraded  90.5%
- 2026-11-05T20:22:30Z  Unhealthy  99.7%
- 2026-11-05T20:39:35Z  Healthy  83.3%
- 2026-11-05T20:52:40Z  Unhealthy  99.7%

Data annotations:

- 2026-11-05T20:14:50Z  start  run 20261105-201448  Stress test start: 20 min (ui)
- 2026-11-05T20:35:04Z  end  run 20261105-201448  Stress test end: 20 min (ui)
- 2026-11-05T20:45:03Z  start  run 20261105-204502  Stress test start: 10 min (schedule:evening)

