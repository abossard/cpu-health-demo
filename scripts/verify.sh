#!/usr/bin/env bash
set -Eeuo pipefail

# Subscription and tenant come from the environment or, for unset keys, from the repo's .env (see .env.example).
load_env_file() {
    local file="$1" key value
    [[ -f "$file" ]] || return 0
    while IFS='=' read -r key value; do
        [[ "$key" =~ ^[A-Z_][A-Z0-9_]*$ ]] || continue
        [[ -n "${!key:-}" ]] || export "$key=${value//\"/}"
    done < "$file"
}
load_env_file "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/.env"

readonly DEFAULT_SUBSCRIPTION="${AZURE_SUBSCRIPTION_ID:-}"
readonly DEFAULT_TENANT="${AZURE_TENANT_ID:-}"
readonly DEFAULT_RESOURCE_GROUP="rg-cpu-health-demo"
readonly HEALTH_MODEL_NAME="cpu-health-demo"
readonly API_VERSION="2026-11-01"
readonly VM_COUNT=3
readonly REQUIRED_RUNTIME_MINUTES=81

subscription="$DEFAULT_SUBSCRIPTION"
resource_group="$DEFAULT_RESOURCE_GROUP"
live=false

usage() {
    cat <<EOF
Usage: bash $0 [--subscription ID] [--resource-group NAME] [--live]

Default mode verifies each VM's Percentage CPU metric definition and recent
Average values at PT1M and PT5M.
--live additionally samples CPU signal values and health states for 81 minutes
and checks that each VM becomes degraded and later recovers to Healthy.
All operations are read-only. CPU stress must already be running.
EOF
}

while (($#)); do
    case "$1" in
        --subscription)
            subscription="${2:?--subscription requires an ID}"
            shift 2
            ;;
        --resource-group)
            resource_group="${2:?--resource-group requires a name}"
            shift 2
            ;;
        --live)
            live=true
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            printf 'Unknown argument: %s\n' "$1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ -z "$subscription" || -z "$DEFAULT_TENANT" ]]; then
    printf 'Set AZURE_SUBSCRIPTION_ID and AZURE_TENANT_ID in the environment or in .env (see .env.example).\n' >&2
    exit 2
fi

account_json="$(az account show --subscription "$subscription" --output json)"
python3 -c '
import json, sys
account = json.load(sys.stdin)
expected = (sys.argv[1].lower(), sys.argv[2].lower())
actual = (account.get("id", "").lower(), account.get("tenantId", "").lower())
if actual != expected:
    raise SystemExit(f"Azure context mismatch: expected {expected}, got {actual}")
' "$subscription" "$DEFAULT_TENANT" <<< "$account_json"

vm_json="$(az vm list --show-details --resource-group "$resource_group" \
    --subscription "$subscription" --output json)"
expected_names='["cpu-health-demo-1","cpu-health-demo-2","cpu-health-demo-3"]'
jq -e --argjson expected "$expected_names" \
    '[.[].name] | sort == ($expected | sort)' >/dev/null <<< "$vm_json" || {
    printf 'Expected exactly these three deployed VMs in %s: %s\n' \
        "$resource_group" "$expected_names" >&2
    exit 1
}

start_time="$(python3 -c 'from datetime import datetime, timedelta, timezone; print((datetime.now(timezone.utc) - timedelta(days=1)).isoformat())')"
end_time="$(python3 -c 'from datetime import datetime, timezone; print(datetime.now(timezone.utc).isoformat())')"
names=()
resource_ids=()
ever_degraded=()
recovered=()

while IFS=$'\t' read -r vm_name resource_id; do
    if [[ -z "$vm_name" || -z "$resource_id" ]]; then
        printf 'VM inventory is missing a name or resource ID.\n' >&2
        exit 1
    fi
    names+=("$vm_name")
    resource_ids+=("$resource_id")
    ever_degraded+=(false)
    recovered+=(false)

    definitions="$(az monitor metrics list-definitions --resource "$resource_id" \
        --subscription "$subscription" --output json)"
    jq -e '
        [.[] | select(.name.value == "Percentage CPU")
         | select(.unit == "Percent")
         | select((.supportedAggregationTypes // []) | index("Average"))
         | select([.metricAvailabilities[]?.timeGrain] | index("PT1M"))
         | select([.metricAvailabilities[]?.timeGrain] | index("PT5M"))]
        | length == 1
    ' >/dev/null <<< "$definitions" || {
        printf '%s: Percentage CPU lacks Percent, Average, PT1M, or PT5M support.\n' \
            "$vm_name" >&2
        exit 1
    }

    for interval in PT1M PT5M; do
        metric_json="$(az monitor metrics list --resource "$resource_id" \
            --subscription "$subscription" --metric "Percentage CPU" \
            --aggregation Average --interval "$interval" \
            --start-time "$start_time" --end-time "$end_time" --output json)"
        jq -e '[.value[]?.timeseries[]?.data[]? | select(.average != null)] | length > 0' \
            >/dev/null <<< "$metric_json" || {
            printf '%s: Percentage CPU returned no Average data for %s in the last 24 hours.\n' \
                "$vm_name" "$interval" >&2
            exit 1
        }
    done

    printf '%s: Percentage CPU verified at PT1M/PT5M.\n' "$vm_name"
done < <(jq -r '.[] | [.name, .id] | @tsv' <<< "$vm_json")

if [[ "$live" != true ]]; then
    printf 'Metric preflight passed. Deploy the health model only after reviewing the exact change and approval.\n'
    exit 0
fi

printf 'Sampling health-model signals for %s minutes; this command does not start or stop stress.\n' \
    "$REQUIRED_RUNTIME_MINUTES"
for ((minute = 0; minute < REQUIRED_RUNTIME_MINUTES; minute++)); do
    for i in "${!names[@]}"; do
        entity_url="https://management.azure.com/subscriptions/${subscription}/resourceGroups/${resource_group}/providers/Microsoft.CloudHealth/healthmodels/${HEALTH_MODEL_NAME}/entities/${names[$i]}?api-version=${API_VERSION}"
        entity_json="$(az rest --method get --url "$entity_url" --output json)"
        status_json="$(jq -ce '
            .properties.signalGroups.azureResource.signals[]
            | select(.name == "average-cpu")
            | .status
        ' <<< "$entity_json")"
        health_state="$(jq -r '.healthState // "Unknown"' <<< "$status_json")"
        value="$(jq -r '.value // "null"' <<< "$status_json")"
        reported_at="$(jq -r '.reportedAt // "missing"' <<< "$status_json")"
        printf '%02d:%02d %s value=%s state=%s reportedAt=%s\n' \
            "$((minute / 60))" "$((minute % 60))" "${names[$i]}" \
            "$value" "$health_state" "$reported_at"

        if python3 -c 'import sys; raise SystemExit(0 if float(sys.argv[1]) > 90 else 1)' \
            "$value" 2>/dev/null; then
            if [[ "$health_state" != "Degraded" && "$health_state" != "Unhealthy" ]]; then
                printf '%s: CPU exceeded 90%% without a matching degraded/unhealthy state.\n' \
                    "${names[$i]}" >&2
                exit 1
            fi
            ever_degraded[$i]=true
        elif [[ "${ever_degraded[$i]}" == true && "$health_state" == "Healthy" ]]; then
            recovered[$i]=true
        fi
    done

    if (( minute + 1 < REQUIRED_RUNTIME_MINUTES )); then
        sleep 60
    fi
done

for i in "${!names[@]}"; do
    if [[ "${ever_degraded[$i]}" != true || "${recovered[$i]}" != true ]]; then
        printf '%s: did not show a degraded transition followed by Healthy recovery.\n' \
            "${names[$i]}" >&2
        exit 1
    fi

done

printf 'Live verification passed for all three VMs.\n'
