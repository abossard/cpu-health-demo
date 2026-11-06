# AKS CPU health demo: spec and interface contract

`azd up` deploys an AKS cluster with three stress nodes, a manager app and an Azure Monitor health model. The manager runs a repeating stress rhythm and annotates the health model at the start and end of every run. The VM demo in `infra/main.bicep` is separate.

## Decisions

| # | Decision |
|---|---|
| D1 | `azd up` from the repo root. `azure.yaml` sets `infra.path: infra/aks`. Resource group `rg-${AZURE_ENV_NAME}`, Sweden Central. |
| D2 | AKS with Node Auto Provisioning, one `Standard_B2s_v2` system node, node surge for upgrades. Node OS upgrades run Sundays 03:00-07:00 UTC. |
| D3 | Fixed pool `stress`: 3x `Standard_D2s_v5`, no autoscale, label and taint `workload=stress` (`NoSchedule`). |
| D4 | Public ingress via the app routing add-on (`webapprouting.kubernetes.azure.com`), plain HTTP. Every page and API except `/healthz`, `/login.html` and `app.css` needs the access code from `STRESS_ACCESS_CODE` (azd env, stored as Secret `stress-manager-access`). `POST /login` sets an HttpOnly, SameSite=Strict cookie holding an HMAC of the code, valid 30 days and invalid once the code changes, and allows 10 attempts per minute per client. |
| D5 | Microsoft Entra ID with Azure RBAC on the cluster; local accounts are disabled. |
| D6 | Two user-assigned identities. The manager identity runs the manager pod and is the health model's identity. The runner identity is only used by the annotator sidecar to write annotations. |
| D7 | One ASP.NET Core app `stress-manager` (.NET 10) serves the UI, manages the rhythm and builds the health model with `Azure.ResourceManager.CloudHealth` 1.0.0 (API 2026-11-01). |
| D8 | Signal `average-cpu`: `Percentage CPU` of each stress node's VMSS instance, Average over PT5M, refreshed every minute, Degraded > 90, Unhealthy > 98. |
| D9 | Images build in ACR (`docker.remoteBuild: true`), so local Docker is not needed. |
| D10 | Tests are xUnit `[Theory]` tests over the pure functions in `Calculations/`. |

## Infra contract

- `infra/aks/main.bicep` (subscription scope). Params: `environmentName` (max 34 characters, so the node RG name stays within 80), `location`, `principalId` (the deploying user).
- Resources:
  - ACR Basic. The kubelet identity has AcrPull.
  - AKS: OIDC issuer, workload identity, Azure CNI overlay with Cilium, NAP, app routing, managed Entra ID with Azure RBAC, `disableLocalAccounts`.
  - Node resource group `MC_rg-<env>_aks-<env>`.
  - Identities `id-<env>` (manager) and `id-<env>-runner`.
- Federated credentials (issuer: AKS OIDC; audience `api://AzureADTokenExchange`):
  - `id-<env>` trusts `system:serviceaccount:stress-manager:stress-manager`.
  - `id-<env>-runner` trusts `system:serviceaccount:stress-manager:stress-runner`.
- Roles:
  - Manager identity: custom role "Stress manager health model" on the RG (CloudHealth model, auth setting, signal definition, entity and relationship read/write/delete, entity history and annotation actions, operation status read, identity assign), plus Reader on the node RG for VMSS metrics.
  - Runner identity: custom role "Stress runner annotations" on the RG (entity read, `addDataAnnotation`, `getDataAnnotations`).
  - Deploying user: Azure Kubernetes Service RBAC Cluster Admin, so azd and kubectl work through kubelogin.
- azd outputs: `AZURE_RESOURCE_GROUP`, `AZURE_AKS_CLUSTER_NAME`, `AZURE_CONTAINER_REGISTRY_ENDPOINT`, `AZURE_CONTAINER_REGISTRY_NAME`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_MANAGED_IDENTITY_CLIENT_ID`, `AZURE_MANAGED_IDENTITY_ID`, `AZURE_RUNNER_CLIENT_ID`, `AZURE_NODE_RESOURCE_GROUP`, `HEALTH_MODEL_NAME` (`hm-<env>`), `HEALTH_MODEL_LOCATION`.

## Kubernetes contract

- Namespace `stress-manager` with Pod Security `enforce: baseline`, `warn`/`audit: restricted`.
- ServiceAccounts:
  - `stress-manager`: client ID of `id-<env>`.
  - `stress-runner`: client ID of `id-<env>-runner`, no Kubernetes RBAC.
- Manager Role: pods `list`; CronJobs `list, create, delete, patch, update`; Jobs `list, delete`. ClusterRole: nodes `list`, `metrics.k8s.io` nodes `list`.
- Deployment `stress-manager`:
  - 1 replica, workload identity label, port 8080, probes on `/healthz`
  - non-root, seccomp `RuntimeDefault`, all capabilities dropped, read-only root filesystem with a `/tmp` emptyDir
  - env: the health model variables, `STRESS_NAMESPACE`, `STRESS_NODE_LABEL=workload=stress` (exactly one `key=value`), `MANAGER_IMAGE` (the annotator image)
- Service `stress-manager` (80 -> 8080), Ingress `stress-manager` on `/`.

## Health model

- Model `hm-<env>` with the manager identity and authentication setting `uami`.
- Entities: root (named after the model) -> `observe-cpu-pressure` (WorstOf) -> one entity per labelled stress node, named after the Kubernetes node. The ARM ID comes from `spec.providerID`.
- The reconciler writes on startup, when the node set changes, and one minute after a failed pass. It deletes entities and relationships of nodes that are gone. For the root it reads the entity and changes only `canvasPosition`.
- Canvas layout (`TreeLayout`): leaves in name order, 300 apart; each parent centred over its first and last child; 250 per level.
- Annotation details: `runId`, `phase` (`start`|`end`), `minutes`, `load`, `source`, `node`, and `backstop=true` when the manager writes a missing `end`. Description: `Stress test <phase>: <minutes> min @ <load>% (<source>)`.

## Rhythm

A rhythm is an ordered list of steps `{minutes, load}`. Load 1-100 is ON and 0 is a break. The rhythm repeats forever, anchored at 00:00 UTC, and is the only way to start stress.

### Normalization

1. Clamp minutes to 1-1440 and load to 0-100. Merge adjacent breaks and drop empty steps.
2. Cycle length `L` is the sum of minutes, truncated at 1440.
3. If `L` does not divide 1440, the final break grows (or a break is appended) until `L` divides 1440. The response reports the added minutes as `paddedMinutes`.
4. A rhythm without an ON step is rejected with 400.

### Compiler

- ON step `i` at offset `o` fires at day-minutes `o + k*L`. Minutes that share the same hours form one cron line (`m1,m2 <hours> * * *`, with `*`, `a-23/s` or `*/s` for regular hours). Each cron line is a CronJob `rhythm-s<i>-<j>`.
- CronJob: `timeZone: Etc/UTC`, `concurrencyPolicy: Forbid`, `startingDeadlineSeconds: 120`, history 1 succeeded and 3 failed. `spec.suspend` means paused.
- Labels `app=stress-schedule`, `stress/kind=rhythm`. Annotations:
  - `stress/rhythm`: the normalized steps as JSON, the same on every CronJob
  - `stress/spec-hash`: the CronJob spec without `suspend`
  - `stress/template-hash`: the embedded Job template (images, env, node count)

### Reducer

- No rhythm CronJobs means `off`. One `stress/rhythm` value whose plan matches the CronJob names and crons means `active`, or `paused` when every CronJob is suspended.
- Anything else is `drifted`. A different `stress/rhythm` value or plan needs a new apply. A different template hash is repaired by `RhythmReconciler`.
- `nextOn` is the next firing time and step while `active`.

### State changes

- Apply diffs the compiled CronJobs against the live ones. CronJobs with the same name and spec hash stay; changed ones are updated in place, so their running Job continues; removed ones are deleted.
- Pause and Clear also delete the running stress Jobs.
- Mutations run under one lock on the application lifetime, so a closed browser tab cannot stop them halfway.
- `RhythmReconciler` runs on startup, every 60 s, and within 15 s of a change in the number of schedulable stress nodes (Ready and not cordoned). It re-applies the current rhythm when only the template drifted, keeping the suspend state.

### Stress Job

- An Indexed Job with `completions` and `parallelism` equal to the schedulable node count, and one pod per node (required anti-affinity on `kubernetes.io/hostname`).
- `backoffLimitPerIndex: 0` and `maxFailedIndexes` equal to the node count, so one failed pod does not stop the others. A pod failure policy ignores `DisruptionTarget` (node drain).
- `activeDeadlineSeconds: minutes*60+60`, `ttlSecondsAfterFinished: 3600`.
- Pod:
  - ServiceAccount `stress-runner` with `automountServiceAccountToken: false`
  - workload identity skipped for the `stress` container
  - seccomp `RuntimeDefault`; both containers non-root, all capabilities dropped, read-only root filesystem
  - shared emptyDir `/run/stress`
- Container `stress`: stress-ng pinned by digest, UID 65534. It touches `/run/stress/started`, then runs `stress-ng --cpu 0 --cpu-load <load> --timeout <minutes*60-20>s` (at least 30 s), so a Job finishes before its CronJob's next firing.
- Native sidecar `annotator` (manager image, `annotate`):
  - waits for `/run/stress/started`, then writes `start`
  - on SIGTERM writes `end`, but only if `start` was written
  - retries a failed write up to 3 times; before each retry it checks for an existing annotation with the same `runId` and `phase`
  - always exits 0
- `EndAnnotationBackstop` (manager, every 2 min) writes a missing `end` for a run whose Job has finished or is gone and whose `start` is older than its length plus 5 min.

## API

| Method | Path | Behaviour |
|---|---|---|
| GET | `/` | UI: rhythm editor and preview, active run, live node CPU. |
| GET | `/timeline.html` | Health timeline, see `timeline.md`. |
| GET | `/healthz` | 200 |
| POST | `/login` | Form `code`, `next`. Sets the access cookie and redirects to `next` (same-site paths only). |
| GET | `/api/rhythm` | `{state, steps, cycleMinutes, paddedMinutes, cronJobs[{name, cron, step, minutes, load, suspended}], nextOn{at, step}, signal{timeGrain, degraded, unhealthy}}` |
| POST | `/api/rhythm/preview` | Body `{steps}`. Same shape with `state: "preview"`. Writes nothing. |
| PUT | `/api/rhythm` | Body `{steps}`. Applies the rhythm and returns the GET shape. |
| POST | `/api/rhythm/pause`, `/api/rhythm/resume` | Suspends or resumes all rhythm CronJobs. Pause also stops the running Job. |
| DELETE | `/api/rhythm` | Deletes all rhythm CronJobs and the running Job. |
| GET | `/api/runs/active` | `{runId, minutes, load, source, phase, startedAt, endsAt, nodes[]}` of the newest pending or running stress Job, or 204. `phase` is `pending` or `running`. |
| GET | `/api/metrics/nodes` | `[{node, cpuMillicores, cpuPercent, memoryBytes, timestamp}]` from metrics-server, CPU as % of allocatable. |
| GET | `/api/timeline`, `/api/timeline/stream` | See `timeline.md`. |
