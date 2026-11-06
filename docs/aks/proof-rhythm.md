# AKS health model proof: rhythm runs

Generated 2026-11-05T21:53:23Z by `scripts/aks-proof.sh 2026-11-05T21:10:00Z`. Annotations come from the annotator sidecar in each rhythm Job pod (workload identity, ServiceAccount `stress-runner`).

Rhythm: 3m@100 / 2 break / 4m@100 / 2 break / 5m@100 / 2 break / 6m@100 / 8 break, 32-min cycle, 32 CronJobs.

## aks-stress-45060198-vmss000000

Signal history (average-cpu), state changes only:

- 2026-11-05T21:10:45Z  Healthy  4.9%
- 2026-11-05T21:38:54Z  Degraded  94.9%
- 2026-11-05T21:39:54Z  Healthy  85.4%
- 2026-11-05T21:45:56Z  Degraded  95%
- 2026-11-05T21:46:57Z  Unhealthy  99.6%
- 2026-11-05T21:47:57Z  Healthy  85.6%

Data annotations:

- 2026-11-05T21:15:06Z  start  run rhythm-s0-0-29898555  Stress test start: 5 min @ 70% (rhythm:0)
- 2026-11-05T21:17:26Z  end  run rhythm-s0-0-29898555  Stress test end: 5 min @ 70% (rhythm:0)
- 2026-11-05T21:20:05Z  start  run rhythm-s0-5-29898560  Stress test start: 3 min @ 100% (rhythm:0)
- 2026-11-05T21:23:02Z  end  run rhythm-s0-5-29898560  Stress test end: 3 min @ 100% (rhythm:0)
- 2026-11-05T21:25:04Z  start  run rhythm-s2-6-29898565  Stress test start: 4 min @ 100% (rhythm:2)
- 2026-11-05T21:29:02Z  end  run rhythm-s2-6-29898565  Stress test end: 4 min @ 100% (rhythm:2)
- 2026-11-05T21:31:03Z  start  run rhythm-s4-7-29898571  Stress test start: 5 min @ 100% (rhythm:4)
- 2026-11-05T21:36:02Z  end  run rhythm-s4-7-29898571  Stress test end: 5 min @ 100% (rhythm:4)
- 2026-11-05T21:38:03Z  start  run rhythm-s6-1-29898578  Stress test start: 6 min @ 100% (rhythm:6)
- 2026-11-05T21:44:02Z  end  run rhythm-s6-1-29898578  Stress test end: 6 min @ 100% (rhythm:6)

## aks-stress-45060198-vmss000001

Signal history (average-cpu), state changes only:

- 2026-11-05T21:10:45Z  Healthy  5.4%
- 2026-11-05T21:37:54Z  Unhealthy  98.2%
- 2026-11-05T21:39:54Z  Healthy  77.3%
- 2026-11-05T21:44:56Z  Unhealthy  98.3%
- 2026-11-05T21:47:57Z  Healthy  77.6%

Data annotations:

- 2026-11-05T21:15:07Z  start  run rhythm-s0-0-29898555  Stress test start: 5 min @ 70% (rhythm:0)
- 2026-11-05T21:17:26Z  end  run rhythm-s0-0-29898555  Stress test end: 5 min @ 70% (rhythm:0)
- 2026-11-05T21:20:04Z  start  run rhythm-s0-5-29898560  Stress test start: 3 min @ 100% (rhythm:0)
- 2026-11-05T21:23:02Z  end  run rhythm-s0-5-29898560  Stress test end: 3 min @ 100% (rhythm:0)
- 2026-11-05T21:25:04Z  start  run rhythm-s2-6-29898565  Stress test start: 4 min @ 100% (rhythm:2)
- 2026-11-05T21:29:02Z  end  run rhythm-s2-6-29898565  Stress test end: 4 min @ 100% (rhythm:2)
- 2026-11-05T21:31:03Z  start  run rhythm-s4-7-29898571  Stress test start: 5 min @ 100% (rhythm:4)
- 2026-11-05T21:36:02Z  end  run rhythm-s4-7-29898571  Stress test end: 5 min @ 100% (rhythm:4)
- 2026-11-05T21:38:04Z  start  run rhythm-s6-1-29898578  Stress test start: 6 min @ 100% (rhythm:6)
- 2026-11-05T21:44:02Z  end  run rhythm-s6-1-29898578  Stress test end: 6 min @ 100% (rhythm:6)

## aks-stress-45060198-vmss000002

Signal history (average-cpu), state changes only:

- 2026-11-05T21:10:45Z  Healthy  5.2%
- 2026-11-05T21:32:52Z  Unhealthy  99.6%
- 2026-11-05T21:33:52Z  Healthy  71.4%
- 2026-11-05T21:38:55Z  Unhealthy  99.6%
- 2026-11-05T21:39:56Z  Healthy  86.1%
- 2026-11-05T21:45:58Z  Unhealthy  99.6%
- 2026-11-05T21:48:59Z  Healthy  82.1%

Data annotations:

- 2026-11-05T21:15:07Z  start  run rhythm-s0-0-29898555  Stress test start: 5 min @ 70% (rhythm:0)
- 2026-11-05T21:17:26Z  end  run rhythm-s0-0-29898555  Stress test end: 5 min @ 70% (rhythm:0)
- 2026-11-05T21:20:05Z  start  run rhythm-s0-5-29898560  Stress test start: 3 min @ 100% (rhythm:0)
- 2026-11-05T21:23:02Z  end  run rhythm-s0-5-29898560  Stress test end: 3 min @ 100% (rhythm:0)
- 2026-11-05T21:25:04Z  start  run rhythm-s2-6-29898565  Stress test start: 4 min @ 100% (rhythm:2)
- 2026-11-05T21:29:02Z  end  run rhythm-s2-6-29898565  Stress test end: 4 min @ 100% (rhythm:2)
- 2026-11-05T21:31:03Z  start  run rhythm-s4-7-29898571  Stress test start: 5 min @ 100% (rhythm:4)
- 2026-11-05T21:36:01Z  end  run rhythm-s4-7-29898571  Stress test end: 5 min @ 100% (rhythm:4)
- 2026-11-05T21:38:04Z  start  run rhythm-s6-1-29898578  Stress test start: 6 min @ 100% (rhythm:6)
- 2026-11-05T21:44:02Z  end  run rhythm-s6-1-29898578  Stress test end: 6 min @ 100% (rhythm:6)

