# CPU health demo

This project provisions three `Standard_D2s_v5` virtual machines in Sweden
Central. Their CPU stress schedules align using the VMs' system clocks. The
health model measures CPU pressure.

The runners use the same `CPU_HEALTH_SCHEDULE_EPOCH` in
`/etc/default/cpu-health-stress` (Unix epoch by default). They repeat 1–6
minute bursts, each followed by six minutes idle, and skip bursts already in
progress after a restart. Alignment depends on the VM clocks. A runner that
misses a phase boundary by more than one second fails and restarts to rejoin a
later phase.

## Before deployment

The deployment target is subscription
`6fc552c5-06b6-43d7-ac70-2f1eda1be033`, tenant
`46693104-8c42-47b7-98e3-985ed39402f6`, and resource group
`rg-cpu-health-demo`. The deployment script checks the selected account,
regional SKU restrictions, core and DSv5 quotas, subscription policy
assignments, and a subscription deployment what-if. It is read-only unless
`--deploy` is explicitly supplied and the required monthly estimate and exact
confirmation are set.
Policy assignments are listed for review; that is not a compliance
determination, and Azure can still reject a deployment.

```bash
bash scripts/deploy.sh
```

The infrastructure does not expose public inbound ports. The three VMs, public
IP addresses, disks, and network resources can incur charges. A 730-hour public-retail estimate
checked on 2026-11-05 is about $238.94 USD/month for VM compute, three static
Standard IPv4 addresses, and three 32-GiB Standard HDD disks. It excludes
outbound bandwidth, disk operations, tax, and subscription-specific discounts.
Review the current monthly cost before approving deployment. SKU eligibility
and quota do not guarantee that Azure has allocation capacity.

The three VMs have been deployed. The local bootstrap baseline is recorded in
`tests/BASELINE.txt`; strict preflight against an existing Git HEAD was
unavailable because the approved branch has no commit.

## Tests and templates

Run the offline suite and compile the infrastructure templates:

```bash
python3 -m unittest discover -s tests -v
bicep build infra/main.bicep --stdout
bicep build infra/compute.bicep --stdout
bicep build infra/healthmodel.bicep --stdout
```

The health-model template is separate from the VM deployment. It binds one
`Percentage CPU` signal to each VM, averaging over five minutes and refreshing
every minute. The reviewed thresholds are Degraded above 90% and Unhealthy
above 98%. Its system-assigned identity receives Reader on each monitored VM.
The model was deployed after all three live metric streams were verified and
the exact model deployment and role assignments were approved.

For a new deployment, the model deployment script verifies live metrics,
shows a read-only what-if, and prints the exact confirmation string. It refuses
to overwrite an existing model or deploy without the reviewed monthly estimate
and exact confirmation:

```bash
bash scripts/deploy.sh --health-model
```

Only after reviewing and approving that output, provide the estimate and
matching confirmation and rerun with `--health-model --deploy`.

## Verify and operate

After the VMs are deployed, check that each VM returns recent `Percentage CPU`
Average data at PT1M and PT5M:

```bash
bash scripts/verify.sh
```

After the health model and stress runners are deployed, the full CPU/state
verification takes 81 minutes. It covers the 57-minute stress cycle and
evaluation lag, and only reads metrics and model state:

```bash
bash scripts/verify.sh --live
```

To stop the stress schedule, use the Azure VM Run Command from an identity
authorized to run commands on the VMs. This executes the local stop operation
through the Azure control plane and does not open a public administration port:

```bash
for vm in cpu-health-demo-1 cpu-health-demo-2 cpu-health-demo-3; do
  az vm run-command invoke \
    --subscription 6fc552c5-06b6-43d7-ac70-2f1eda1be033 \
    --resource-group rg-cpu-health-demo \
    --name "$vm" \
    --command-id RunShellScript \
    --scripts "sudo /usr/local/sbin/cpu-health-stress stop"
done
```

To start the schedules again:

```bash
for vm in cpu-health-demo-1 cpu-health-demo-2 cpu-health-demo-3; do
  az vm run-command invoke \
    --subscription 6fc552c5-06b6-43d7-ac70-2f1eda1be033 \
    --resource-group rg-cpu-health-demo \
    --name "$vm" \
    --command-id RunShellScript \
    --scripts "sudo systemctl enable --now cpu-health-stress.service"
done
```

No public administration port is provisioned. Do not delete the resource group
or other Azure resources without reviewing the exact target and obtaining
approval; cleanup is never automatic.
