#!/usr/bin/env bash
set -euo pipefail

cycles="${1:-50}"
if ! [[ "$cycles" =~ ^[1-9][0-9]*$ ]] || (( cycles > 50 )); then
    echo "Usage: $0 [cycles: 1-50]" >&2
    exit 2
fi

device="emulator-5556"
package="com.netlift.androidhost.minified"
activity="$package/com.netlift.androidhost.MainActivity"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
log_file="$(mktemp "$script_dir/minified-stress.XXXXXX")"
logcat_pid=""
run_id="$(date +%s)-$$"

count_matches() {
    local needle="$1"
    awk -v marker="RUN_START $run_id" -v needle="$needle" '
        index($0, marker) { active = 1; next }
        active && index($0, needle) { count++ }
        END { print count + 0 }
    ' "$log_file"
}

count_event_value() {
    local event="$1"
    local value="$2"
    awk -v marker="RUN_START $run_id" -v expected_event="event=$event" -v expected_value="value=$value" '
        index($0, marker) { active = 1; next }
        active {
            has_event = 0
            has_value = 0
            for (i = 1; i <= NF; i++) {
                if ($i == expected_event) has_event = 1
                if ($i == expected_value) has_value = 1
            }
            if (has_event && has_value) count++
        }
        END { print count + 0 }
    ' "$log_file"
}

wait_for_count() {
    local needle="$1"
    local expected="$2"
    local timeout_seconds="${3:-15}"
    local started=$SECONDS
    local actual=0

    while (( SECONDS - started < timeout_seconds )); do
        actual="$(count_matches "$needle")"
        if (( actual >= expected )); then
            return 0
        fi
        sleep 0.1
    done

    echo "Timed out waiting for '$needle' count $expected (observed $actual)." >&2
    echo "Stress log: $log_file" >&2
    exit 1
}

wait_for_event_value() {
    local event="$1"
    local value="$2"
    local expected="$3"
    local timeout_seconds="${4:-15}"
    local started=$SECONDS
    local actual=0

    while (( SECONDS - started < timeout_seconds )); do
        actual="$(count_event_value "$event" "$value")"
        if (( actual >= expected )); then
            return 0
        fi
        sleep 0.1
    done

    echo "Timed out waiting for '$event' value $value count $expected (observed $actual)." >&2
    echo "Stress log: $log_file" >&2
    exit 1
}

stop_logcat() {
    if [[ -n "$logcat_pid" ]]; then
        kill "$logcat_pid" 2>/dev/null || true
        wait "$logcat_pid" 2>/dev/null || true
    fi
}
trap stop_logcat EXIT
trap 'exit 130' INT TERM

if [[ "$(adb -s "$device" get-state)" != "device" ]]; then
    echo "Dedicated emulator $device is not connected; refusing other devices." >&2
    exit 1
fi
adb -s "$device" shell pm path "$package" >/dev/null
adb -s "$device" shell am force-stop "$package"
adb -s "$device" logcat -v time -T 1 \
    NetLiftStress:V NetLiftLiftAbi:I NetLiftCoreCLR:E AndroidRuntime:E libc:F DEBUG:F '*:S' \
    >"$log_file" &
logcat_pid=$!
sleep 0.5
adb -s "$device" shell log -t NetLiftStress "RUN_START $run_id"
adb -s "$device" shell am start -W -n "$activity" \
    -a com.netlift.androidhost.action.RUN_STRESS \
    --ei netlift.stress.cycles "$cycles" >/dev/null

wait_for_count "event=registration_created" 1
initial_pid="$(adb -s "$device" shell pidof "$package" | tr -d '\r')"
if [[ -z "$initial_pid" ]]; then
    echo "The app did not start." >&2
    exit 1
fi
wait_for_count "STRESS_STARTED cycles=$cycles" 1
wait_for_count "STRESS_COMPLETE cycles=$cycles" 1 180

expected_accepted=$((cycles / 2))
expected_declined=$(((cycles + 1) / 2))
expected_completion="STRESS_COMPLETE cycles=$cycles cancelled=$cycles raced_declines=0 accepted=$expected_accepted declined=$expected_declined"
completion_entries="$(count_matches "STRESS_COMPLETE")"
matching_completion_entries="$(count_matches "$expected_completion")"
cycle_entries="$(count_matches "STRESS_CYCLE cycle=")"
if (( completion_entries != 1 ||
      matching_completion_entries != 1 ||
      cycle_entries != cycles )); then
    echo "Stress summary did not match the expected workload: expected '$expected_completion' and $cycles cycle entries; observed summaries=$completion_entries matching_summaries=$matching_completion_entries cycles=$cycle_entries." >&2
    echo "Stress log: $log_file" >&2
    exit 1
fi

if (( $(count_matches "STRESS_FAILED") != 0 )); then
    echo "The in-app stress hook reported failure." >&2
    echo "Stress log: $log_file" >&2
    exit 1
fi

final_pid="$(adb -s "$device" shell pidof "$package" | tr -d '\r')"
if [[ "$final_pid" != "$initial_pid" ]]; then
    echo "Process changed during stress: $initial_pid -> $final_pid" >&2
    exit 1
fi

registrations_created="$(count_matches "event=registration_created")"
registrations_released="$(count_matches "event=registration_released")"
operations_created="$(count_matches "event=operation_created")"
operations_removed="$(count_matches "event=operation_removed")"
requests_created="$(count_matches "event=request_created")"
requests_completed="$(count_matches "event=request_completed")"
accepted_requests="$(count_event_value "request_completed" 1)"
rejected_requests="$(count_event_value "request_completed" 0)"

if (( registrations_created != cycles + 1 ||
      registrations_released != cycles ||
      operations_created != cycles * 2 ||
      operations_removed != cycles * 2 ||
      requests_created != cycles * 2 ||
      requests_completed != cycles * 2 ||
      accepted_requests != cycles / 2 ||
      rejected_requests != cycles + (cycles + 1) / 2 )); then
    echo "Observed event counts did not match the expected stress workload." >&2
    echo "Stress log: $log_file" >&2
    exit 1
fi

max_count() {
    local field="$1"
    awk -v marker="RUN_START $run_id" -v field="$field=" '
        index($0, marker) { active = 1; next }
        active {
            for (i = 1; i <= NF; i++) {
                if (index($i, field) == 1) {
                    value = substr($i, length(field) + 1)
                    if (value > maximum) maximum = value
                    last = value
                }
            }
        }
        END { print maximum + 0, last + 0 }
    ' "$log_file"
}

read -r max_registrations final_registrations < <(max_count "live_registrations")
read -r max_operations final_operations < <(max_count "live_operations")
read -r max_requests final_requests < <(max_count "live_requests")
if (( final_registrations != 1 || final_operations != 0 || final_requests != 0 )); then
    echo "Handles were not fully drained: registrations=$final_registrations operations=$final_operations requests=$final_requests" >&2
    echo "Stress log: $log_file" >&2
    exit 1
fi

fatal_count="$(awk -v marker="RUN_START $run_id" '
    index($0, marker) { active = 1; next }
    active && $0 ~ /(FATAL EXCEPTION|Fatal signal|JNI DETECTED ERROR|SIGSEGV|SIGABRT)/ { count++ }
    END { print count + 0 }
' "$log_file")"
if (( fatal_count != 0 )); then
    echo "Fatal Android/JNI log entries found ($fatal_count)." >&2
    echo "Stress log: $log_file" >&2
    exit 1
fi

echo "cycles=$cycles process=$initial_pid"
echo "registrations_created=$registrations_created registrations_released=$registrations_released max_live_registrations=$max_registrations final_live_registrations=$final_registrations"
echo "operations_created=$operations_created operations_removed=$operations_removed max_live_operations=$max_operations final_live_operations=$final_operations"
echo "requests_created=$requests_created requests_completed=$requests_completed accepted=$accepted_requests rejected_or_cancelled=$rejected_requests"
echo "max_live_requests=$max_requests final_live_requests=$final_requests fatal_logs=$fatal_count"
echo "log=$log_file"
