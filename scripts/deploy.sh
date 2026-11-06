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
readonly DEFAULT_LOCATION="swedencentral"
readonly VM_SIZE="Standard_D2s_v5"
readonly VM_COUNT=3
readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly TEMPLATE="${SCRIPT_DIR}/../infra/main.bicep"
readonly HEALTHMODEL_TEMPLATE="${SCRIPT_DIR}/../infra/healthmodel.bicep"
readonly HEALTH_MODEL_NAME="cpu-health-demo"

subscription="$DEFAULT_SUBSCRIPTION"
resource_group="$DEFAULT_RESOURCE_GROUP"
location="$DEFAULT_LOCATION"
deploy=false
health_model=false

usage() {
    cat <<EOF
Usage: $0 [--subscription ID] [--resource-group NAME] [--location REGION] [--health-model] [--deploy]

Default mode performs read-only subscription, SKU, quota, policy, and what-if checks.
--deploy writes only after CPU_HEALTH_MONTHLY_COST_ESTIMATE is set and
CPU_HEALTH_DEPLOY_CONFIRMATION exactly matches the printed confirmation text.
--health-model verifies live VM signals before a read-only model what-if.
Its write mode requires a reviewed estimate and exact model confirmation.
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
        --location)
            location="${2:?--location requires a region}"
            shift 2
            ;;
        --health-model)
            health_model=true
            shift
            ;;
        --deploy)
            deploy=true
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

if [[ "$health_model" == true ]]; then
    bash "${SCRIPT_DIR}/verify.sh" \
        --subscription "$subscription" \
        --resource-group "$resource_group"

    account_json="$(az account show --subscription "$subscription" --output json)"
    python3 -c '
import json, sys
account = json.load(sys.stdin)
expected = (sys.argv[1].lower(), sys.argv[2].lower())
actual = (account.get("id", "").lower(), account.get("tenantId", "").lower())
if actual != expected:
    raise SystemExit(f"Azure context mismatch: expected subscription/tenant {expected}, got {actual}")
' "$subscription" "$DEFAULT_TENANT" <<< "$account_json"

    if [[ "$(az group exists --name "$resource_group" --subscription "$subscription")" != "true" ]]; then
        printf 'Resource group %s does not exist.\n' "$resource_group" >&2
        exit 1
    fi

    existing_model="$(az resource list --subscription "$subscription" \
        --resource-group "$resource_group" \
        --resource-type Microsoft.CloudHealth/healthmodels \
        --query "[?name=='${HEALTH_MODEL_NAME}'].name | [0]" --output tsv)"
    if [[ -n "$existing_model" ]]; then
        printf 'Health model %s already exists; refusing to overwrite it.\n' "$HEALTH_MODEL_NAME" >&2
        exit 1
    fi

    vm_json="$(az vm list --resource-group "$resource_group" \
        --subscription "$subscription" --output json)"
    expected_names='["cpu-health-demo-1","cpu-health-demo-2","cpu-health-demo-3"]'
    jq -e --argjson expected "$expected_names" \
        '[.[].name] | sort == ($expected | sort)' >/dev/null <<< "$vm_json" || {
        printf 'Expected exactly these three deployed VMs in %s: %s\n' \
            "$resource_group" "$expected_names" >&2
        exit 1
    }

    parameter_file="$(mktemp "${TMPDIR:-/tmp}/cpu-health-demo-healthmodel.XXXXXX")"
    trap 'rm -f -- "$parameter_file"' EXIT
    chmod 600 "$parameter_file"
    python3 -c '
import json, sys
vms = json.load(sys.stdin)
parameters = {
    "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
    "contentVersion": "1.0.0.0",
    "parameters": {
        "healthModelName": {"value": sys.argv[1]},
        "location": {"value": sys.argv[2]},
        "vmNames": {"value": [vm["name"] for vm in vms]},
        "vmResourceIds": {"value": [vm["id"] for vm in vms]},
    },
}
json.dump(parameters, sys.stdout)
' "$HEALTH_MODEL_NAME" "$location" <<< "$vm_json" > "$parameter_file"

    vm_ids="$(jq -r 'sort_by(.name) | map(.id) | join(",")' <<< "$vm_json")"
    printf 'Read-only health-model deployment what-if:\n'
    az deployment group what-if \
        --subscription "$subscription" \
        --resource-group "$resource_group" \
        --name "cpu-health-model-preview" \
        --template-file "$HEALTHMODEL_TEMPLATE" \
        --parameters "@${parameter_file}"

    confirmation="DEPLOY HEALTH MODEL ${HEALTH_MODEL_NAME} in ${subscription}/${resource_group} (${location}): 1 model, 1 signal definition, 4 entities, 4 relationships, 3 Reader assignments on ${vm_ids}; system-assigned identity; no alerts or network changes; estimated monthly cost ${CPU_HEALTH_MODEL_MONTHLY_COST_ESTIMATE:-<estimate>}"
    if [[ "$deploy" != true ]]; then
        printf '\nNo Azure resources were changed. Review the what-if and estimate costs before deployment.\n'
        printf 'Exact confirmation required with --health-model --deploy:\n%s\n' "$confirmation"
        exit 0
    fi

    if [[ -z "${CPU_HEALTH_MODEL_MONTHLY_COST_ESTIMATE:-}" ]]; then
        printf 'Set CPU_HEALTH_MODEL_MONTHLY_COST_ESTIMATE to the reviewed monthly cost estimate before deployment.\n' >&2
        exit 1
    fi
    if [[ "${CPU_HEALTH_MODEL_DEPLOY_CONFIRMATION:-}" != "$confirmation" ]]; then
        printf 'No Azure resources were changed. To deploy after approval, set this exact confirmation:\n%s\n' \
            "$confirmation" >&2
        exit 1
    fi

    deployment_name="cpu-health-model-$(date -u '+%Y%m%d%H%M%S')"
    az deployment group create \
        --subscription "$subscription" \
        --resource-group "$resource_group" \
        --name "$deployment_name" \
        --template-file "$HEALTHMODEL_TEMPLATE" \
        --parameters "@${parameter_file}" \
        --output json
    exit 0
fi

account_json="$(az account show --subscription "$subscription" --output json)"
python3 -c '
import json, sys
account = json.load(sys.stdin)
expected = (sys.argv[1].lower(), sys.argv[2].lower())
actual = (account.get("id", "").lower(), account.get("tenantId", "").lower())
if actual != expected:
    raise SystemExit(f"Azure context mismatch: expected subscription/tenant {expected}, got {actual}")
' "$subscription" "$DEFAULT_TENANT" <<< "$account_json"

sku_json="$(az vm list-skus --subscription "$subscription" --location "$location" --size "$VM_SIZE" --all --output json)"
python3 -c '
import json, sys
rows = [row for row in json.load(sys.stdin)
        if row.get("name") == sys.argv[1] and row.get("resourceType") == "virtualMachines"]
if not rows:
    raise SystemExit(f"No VM SKU result for {sys.argv[1]}")
restrictions = [restriction for row in rows for restriction in row.get("restrictions", [])
                if restriction.get("type") == "Location"]
if restrictions:
    raise SystemExit(f"VM SKU is restricted in this region: {restrictions}")
print(f"SKU eligible: {sys.argv[1]} in {sys.argv[2]}; capacity is not guaranteed")
' "$VM_SIZE" "$location" <<< "$sku_json"

usage_json="$(az vm list-usage --subscription "$subscription" --location "$location" --output json)"
python3 -c '
import json, sys
entries = json.load(sys.stdin)
requirements = (("cores", 2 * int(sys.argv[1])), ("dsv5", 2 * int(sys.argv[1])))
for needle, required in requirements:
    if needle == "cores":
        matches = [item for item in entries
                   if item.get("name", {}).get("value", "").lower() == "cores"]
    else:
        matches = [item for item in entries
                   if item.get("name", {}).get("localizedValue", "").lower().startswith("standard dsv5 family")]
    if not matches:
        raise SystemExit(f"Could not verify {needle} quota")
    entry = matches[0]
    available = int(entry.get("limit", 0)) - int(entry.get("currentValue", 0))
    label = entry.get("name", {}).get("localizedValue", needle)
    print("{}: {}/{}; need {} available".format(
        label, entry.get("currentValue", 0), entry.get("limit", 0), required
    ))
    if available < required:
        raise SystemExit(f"Insufficient {needle} quota: need {required}, have {available}")
' "$VM_COUNT" <<< "$usage_json"

if [[ "$(az group exists --name "$resource_group" --subscription "$subscription")" == "true" ]]; then
    printf 'Refusing to deploy into existing resource group %s.\n' "$resource_group" >&2
    exit 1
fi

policy_json="$(az policy assignment list --subscription "$subscription" --scope "/subscriptions/${subscription}" --output json)"
python3 -c '
import json, sys
assignments = json.load(sys.stdin)
print(f"Subscription policy assignments to review: {len(assignments)}")
for item in assignments:
    print("  {}: {}".format(
        item.get("displayName", item.get("name", "unknown")),
        item.get("scope", "unknown scope")
    ))
' <<< "$policy_json"

printf '\nRead-only deployment what-if:\n'
az deployment sub what-if \
    --subscription "$subscription" \
    --location "$location" \
    --name "cpu-health-demo-preview" \
    --template-file "$TEMPLATE" \
    --parameters \
        "location=$location" \
        "resourceGroupName=$resource_group" \
        "adminPassword=Preview-Only-Not-A-Real-Password-123!"

if [[ "$deploy" != true ]]; then
    printf '\nNo Azure resources were changed. Review the what-if and estimate costs before deployment.\n'
    exit 0
fi

if [[ -z "${CPU_HEALTH_MONTHLY_COST_ESTIMATE:-}" ]]; then
    printf 'Set CPU_HEALTH_MONTHLY_COST_ESTIMATE to the reviewed monthly cost estimate before deployment.\n' >&2
    exit 1
fi

confirmation="DEPLOY ${subscription}/${resource_group} in ${location}: ${VM_COUNT} ${VM_SIZE} VMs, ${VM_COUNT} public IPv4 addresses, no public inbound ports; estimated monthly cost ${CPU_HEALTH_MONTHLY_COST_ESTIMATE}"
if [[ "${CPU_HEALTH_DEPLOY_CONFIRMATION:-}" != "$confirmation" ]]; then
    printf 'No Azure resources were changed. To deploy after approval, set this exact confirmation:\n%s\n' "$confirmation" >&2
    exit 1
fi

if [[ -n "${CPU_HEALTH_ADMIN_PASSWORD:-}" ]]; then
    admin_password="$CPU_HEALTH_ADMIN_PASSWORD"
else
    read -r -s -p 'VM administrator password (not displayed): ' admin_password
    printf '\n'
fi
if [[ -z "$admin_password" ]]; then
    printf 'An administrator password is required.\n' >&2
    exit 1
fi

parameter_file="$(mktemp "${TMPDIR:-/tmp}/cpu-health-demo-parameters.XXXXXX")"
trap 'rm -f -- "$parameter_file"' EXIT
chmod 600 "$parameter_file"
printf '%s' "$admin_password" | python3 -c '
import json, sys
parameters = {
    "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
    "contentVersion": "1.0.0.0",
    "parameters": {"adminPassword": {"value": sys.stdin.read()}}
}
json.dump(parameters, sys.stdout)
' > "$parameter_file"
unset admin_password

deployment_name="cpu-health-demo-$(date -u '+%Y%m%d%H%M%S')"
if deployment_output="$(az deployment sub create \
    --subscription "$subscription" \
    --location "$location" \
    --name "$deployment_name" \
    --template-file "$TEMPLATE" \
    --parameters "@${parameter_file}" "location=$location" "resourceGroupName=$resource_group" \
    --output json 2>&1)"; then
    printf '%s\n' "$deployment_output"
else
    status=$?
    printf '%s\n' "$deployment_output" >&2
    printf 'VM deployment failed with the error above. No fallback was created.\n' >&2
    printf 'Fallback proposal: minimal AKS with a fixed three-node pool and one unthrottled stress runner per node.\n' >&2
    printf 'Before creating it, recheck candidate node SKU restrictions and quota, then obtain separate approval for its exact resources, exposure, and cost.\n' >&2
    exit "$status"
fi
