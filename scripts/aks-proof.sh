#!/usr/bin/env bash
# Read-only: prints signal history state changes and data annotations for every stress node entity
# of the AKS health model created by the stress-manager app.
set -Eeuo pipefail

readonly API_VERSION="2026-11-01"
readonly SIGNAL_NAME="average-cpu"
START_AT="${1:-$(date -u -v-2H '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u -d '-2 hours' '+%Y-%m-%dT%H:%M:%SZ')}"
readonly START_AT

eval "$(azd env get-values)"
readonly MODEL_URL="https://management.azure.com/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${AZURE_RESOURCE_GROUP}/providers/Microsoft.CloudHealth/healthmodels/${HEALTH_MODEL_NAME}"
TOKEN="$(az account get-access-token --resource https://management.azure.com --query accessToken -o tsv)"
readonly TOKEN
WORK_DIR="$(mktemp -d)"
readonly WORK_DIR
trap 'rm -rf "${WORK_DIR}"' EXIT

# arm METHOD URL [BODY]. The token goes through a curl config file so it stays out of the process list.
arm() {
    local method="$1" url="$2" body="${3:-}"
    local args=(--fail-with-body --silent --show-error --request "${method}")
    if [[ -n "${body}" ]]; then
        args+=(--header 'Content-Type: application/json' --data "${body}")
    fi
    curl "${args[@]}" --config <(printf 'header = "Authorization: Bearer %s"\n' "${TOKEN}") "${url}"
}

list_stress_nodes() {
    local url="${MODEL_URL}/entities?api-version=${API_VERSION}" page
    while [[ -n "${url}" ]]; do
        page="$(arm GET "${url}")"
        jq -r '.value // [] | .[] | select(.properties.signalGroups.azureResource? != null) | .name' <<<"${page}"
        url="$(jq -r '.nextLink // empty' <<<"${page}")"
    done
}

# post_all URL BODY FIELD: follows nextMarker and prints one JSON array of FIELD across all pages.
post_all() {
    local url="$1" body="$2" field="$3" marker="" page
    while :; do
        page="$(arm POST "${url}" "$(jq -c --arg m "${marker}" 'if $m == "" then . else . + {nextMarker: $m} end' <<<"${body}")")"
        jq -c --arg f "${field}" '.[$f] // []' <<<"${page}"
        marker="$(jq -r '.nextMarker // empty' <<<"${page}")"
        [[ -n "${marker}" ]] || break
    done | jq -s 'add // []'
}

report_node() {
    local node="$1"
    local entity_url="${MODEL_URL}/entities/${node}"

    printf '## %s\n\nSignal history (%s), state changes only:\n\n' "${node}" "${SIGNAL_NAME}"
    post_all "${entity_url}/getSignalHistory?api-version=${API_VERSION}" \
        "$(jq -nc --arg s "${SIGNAL_NAME}" --arg t "${START_AT}" '{signalName: $s, startAt: $t}')" history \
        | jq -r 'sort_by(.occurredAt // "")
            | reduce .[] as $p ([]; if (length == 0 or .[-1].healthState != $p.healthState) then . + [$p] else . end)
            | .[]
            | "- \((.occurredAt // "")[0:19])Z  \(.healthState // "Unknown")  \(if .value == null then "n/a" else "\(.value * 10 | round / 10)%" end)"'

    printf '\nData annotations:\n\n'
    post_all "${entity_url}/getDataAnnotations?api-version=${API_VERSION}" \
        "$(jq -nc --arg t "${START_AT}" '{startAt: $t}')" annotations \
        | jq -r 'sort_by(.createdAt // "")
            | .[]
            | "- \((.createdAt // "")[0:19])Z  \(.annotationDetails.phase // "?")  run \(.annotationDetails.runId // "?")  \(.description // "")"'
    printf '\n'
}

node_list="$(list_stress_nodes | sort)"
nodes=()
while IFS= read -r node; do
    [[ -n "${node}" ]] && nodes+=("${node}")
done <<<"${node_list}"

if (( ${#nodes[@]} == 0 )); then
    printf 'No stress node entities in %s.\n' "${HEALTH_MODEL_NAME}"
    exit 0
fi

pids=()
for i in "${!nodes[@]}"; do
    report_node "${nodes[$i]}" >"${WORK_DIR}/${i}.md" &
    pids+=("$!")
done
for pid in "${pids[@]}"; do
    wait "${pid}"
done
for i in "${!nodes[@]}"; do
    cat "${WORK_DIR}/${i}.md"
done
