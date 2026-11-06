#!/usr/bin/env bash
set -Eeuo pipefail

readonly CONTAINER_NAME="cpu-health-stress"
readonly IMAGE_NAME="cpu-health-stress:local"
readonly LOCK_DIR="${CPU_HEALTH_LOCK_DIR:-/run/lock}"
readonly LOCK_PATH="${LOCK_DIR}/cpu-health-stress.lock"
readonly SCRIPT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"

log() {
    printf '%s %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*"
}

terminate_container() {
    local container_id
    container_id="$(docker ps -aq --filter "name=^/${CONTAINER_NAME}$")" || {
        log "ERROR: could not query Docker for ${CONTAINER_NAME}"
        return 1
    }

    if [[ -n "$container_id" ]]; then
        docker rm -f "$CONTAINER_NAME" || {
            log "ERROR: could not stop ${CONTAINER_NAME}"
            return 1
        }
        log "Stopped ${CONTAINER_NAME}"
    fi
}

cleanup() {
    local status=$?
    trap - EXIT
    if ! terminate_container; then
        status=1
    fi
    if [[ -d "$LOCK_PATH" ]]; then
        rmdir "$LOCK_PATH" || {
            log "ERROR: could not release runner lock ${LOCK_PATH}"
            status=1
        }
    fi
    exit "$status"
}

start() {
    local status
    mkdir -p "$LOCK_DIR"
    if ! mkdir "$LOCK_PATH" 2>/dev/null; then
        log "ERROR: another CPU stress runner is already active"
        return 1
    fi

    trap cleanup EXIT
    trap 'log "Received stop signal"; exit 143' INT TERM

    docker run --rm --name "$CONTAINER_NAME" --privileged \
        --env "CPU_HEALTH_SCHEDULE_EPOCH=${CPU_HEALTH_SCHEDULE_EPOCH:-0}" \
        --mount "type=bind,source=${SCRIPT_PATH},target=/usr/local/sbin/cpu-health-stress,readonly" \
        "$IMAGE_NAME" /usr/local/sbin/cpu-health-stress schedule || {
        status=$?
        log "ERROR: stress schedule failed with status ${status}"
        return "$status"
    }
}

schedule() {
    local epoch="${CPU_HEALTH_SCHEDULE_EPOCH:-0}"
    local now_ns now_seconds cycle_start phase_start target_ns delay_ns delay
    local minutes duration started_ns stopped_ns stop_target_ns status
    local started=false

    if ! [[ "$epoch" =~ ^[0-9]{1,10}$ ]] || (( 10#$epoch > 9223368000 )); then
        log "ERROR: invalid schedule epoch: ${epoch}"
        return 1
    fi
    epoch=$((10#$epoch))
    now_ns="$(date +%s%N)"
    now_seconds=$((now_ns / 1000000000))
    if (( now_seconds < epoch )); then
        cycle_start="$epoch"
    else
        cycle_start=$((epoch + (now_seconds - epoch) / 3420 * 3420))
    fi

    while true; do
        phase_start="$cycle_start"
        for minutes in 1 2 3 4 5 6; do
            duration=$((minutes * 60))
            target_ns=$((phase_start * 1000000000))
            now_ns="$(date +%s%N)"
            if (( now_ns >= target_ns )); then
                if [[ "$started" == true ]]; then
                    log "ERROR: missed synchronized start at ${phase_start}"
                    return 1
                fi
            else
                delay_ns=$((target_ns - now_ns))
                printf -v delay '%d.%09d' "$((delay_ns / 1000000000))" "$((delay_ns % 1000000000))"
                sleep "$delay" || {
                    status=$?
                    log "ERROR: synchronized wait failed with status ${status}"
                    return "$status"
                }
                started_ns="$(date +%s%N)"
                if (( started_ns < target_ns || started_ns - target_ns > 1000000000 )); then
                    log "ERROR: missed synchronized start at ${phase_start}"
                    return 1
                fi
                log "SYNC_START scheduled=${phase_start} actual_ns=${started_ns} duration=${duration}"
                stress-ng --cpu 0 --cpu-load 100 --timeout "${duration}s" --metrics-brief || {
                    status=$?
                    log "ERROR: stress phase ${minutes} failed with status ${status}"
                    return "$status"
                }
                stopped_ns="$(date +%s%N)"
                stop_target_ns=$(((phase_start + duration) * 1000000000))
                if (( stopped_ns < stop_target_ns - 1000000000 || stopped_ns > stop_target_ns + 1000000000 )); then
                    log "ERROR: missed synchronized stop at $((phase_start + duration))"
                    return 1
                fi
                log "SYNC_STOP scheduled=$((phase_start + duration)) actual_ns=${stopped_ns} duration=${duration}"
                started=true
            fi
            phase_start=$((phase_start + duration + 360))
        done
        cycle_start=$((cycle_start + 3420))
    done
}

stop() {
    systemctl disable --now cpu-health-stress.service || {
        log "ERROR: could not disable and stop cpu-health-stress.service"
        return 1
    }
    terminate_container
}

case "${1:-}" in
    start)
        start
        ;;
    schedule)
        schedule
        ;;
    stop)
        stop
        ;;
    terminate)
        terminate_container
        ;;
    *)
        printf 'Usage: %s {start|stop|terminate|schedule}\n' "$0" >&2
        exit 2
        ;;
esac
