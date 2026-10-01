#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
header="$script_dir/NetLiftCallbackLifecycleV2.h"
state="$script_dir/CallbackLifecycleV2State.c"
state_header="$script_dir/CallbackLifecycleV2State.h"
accessor="$script_dir/CallbackLifecycleV2Accessor.c"
adapter="$script_dir/CallbackLifecycleV2Adapter.c"
adapter_header="$script_dir/CallbackLifecycleV2Adapter.h"
managed="$script_dir/ManagedEntryPoints.cs"
coreclr="$script_dir/CoreCLRHost.m"
coreclr_header="$script_dir/CoreCLRHost.h"
tests="$script_dir/tests/CallbackLifecycleV2Test.c"
accessor_tests="$script_dir/tests/CallbackLifecycleV2AccessorTest.c"
adapter_tests="$script_dir/tests/CallbackLifecycleV2AdapterTest.c"
adapter_runner="$script_dir/tests/run-v2-adapter-test.sh"
state_runner="$script_dir/tests/run-callback-lifecycle-v2-test.sh"
generation_script="$script_dir/generate-typed-facade.sh"
generated_swift="$script_dir/GeneratedTyped/NetLift.swift"
v1_header="$script_dir/GeneratedTyped/netlift.h"
v1_adapter="$script_dir/GeneratedFacadeAdapter.c"
project_yml="$script_dir/project.yml"
project_pbx="$script_dir/NativeSwiftHostTyped.xcodeproj/project.pbxproj"
app="$script_dir/NativeSwiftHostApp.swift"

require_text() {
    file=$1
    text=$2
    if ! grep -Fq -- "$text" "$file"; then
        echo "Missing V2 source contract in $file: $text" >&2
        exit 1
    fi
}

require_text "$header" '#define LIFT_FEATURE_CALLBACK_LIFECYCLE_V2 0x00000020u'
require_text "$header" '#define LIFT_FUNCTION_TABLE_V2_VERSION 2u'
require_text "$header" '#define LIFT_V2_MAX_DIAGNOSTIC_BYTES LIFT_MAX_BUFFER_BYTES'
require_text "$header" 'uint32_t abi_version;'
require_text "$header" 'uint32_t reserved;'
require_text "$header" 'uint64_t operation_token'
require_text "$header" 'lift_on_revoke_v2_fn on_revoke;'
require_text "$header" 'lift_ack_drain_v2_fn ack_drain;'
require_text "$header" 'lift_cancel_request_v2_fn cancel_request;'
require_text "$header" 'netlift_coreclr_get_function_table_v2'
require_text "$header" 'LIFT_ENTRYPOINT_REPLY_TYPED_V2 "ReplyTypedV2"'
require_text "$state_header" 'lift_v2_reply_typed'
require_text "$state_header" 'lift_v2_cancel_operation'
require_text "$state_header" 'lift_v2_begin_request_cancel'
require_text "$state_header" 'lift_v2_finish_request_cancel'
require_text "$state_header" 'lift_v2_begin_adapter_cleanup'
require_text "$state_header" 'lift_v2_end_adapter_cleanup'
require_text "$state_header" 'bool *already_cancelled'
require_text "$state_header" 'lift_v2_revoke'
require_text "$state_header" 'lift_v2_ack_drain'
require_text "$state" 'pthread_equal(lifecycle->revoke_callback_thread, pthread_self())'
require_text "$state" 'lifecycle->replies_in_flight != 0'
require_text "$state" 'lifecycle->adapter_cleanups_in_flight != 0'
require_text "$state" 'lifecycle->adapter_cleanups_in_flight > allowed_adapter_cleanups'
require_text "$state" 'uint64_t operation_token;'
require_text "$state" 'find_seen_request(lifecycle, request_token) != NULL'
require_text "$state" 'operation_has_requests'
require_text "$state" 'managed_free(managed_context, managed_status.message.data);'
require_text "$state" 'REQUEST_RETIREMENT_MANAGED_CANCELLED'
require_text "$state" 'pthread_cond_wait(&lifecycle->changed, &lifecycle->mutex);'
require_text "$state" 'attempt->waiters != 0'
require_text "$state" 'REQUEST_RETIREMENT_REPLIED'
require_text "$state" '(*operation_link)->callbacks_in_flight != 0'
require_text "$state" 'terminal_in_flight = true;'
require_text "$state" '(*request_link)->reply_claimed = false;'
require_text "$state" 'request->cancel_claimed = true;'
require_text "$state" 'managed_status != LIFT_STATUS_OK'
require_text "$accessor" 'required_table_size < sizeof(lift_function_table_v2)'
require_text "$accessor" 'LIFT_FEATURE_ASYNC_CALLBACKS'
require_text "$accessor" 'LIFT_FEATURE_TYPED_NATIVE_REPLY'
require_text "$accessor" 'LIFT_FEATURE_CALLBACK_LIFECYCLE_V2'
require_text "$accessor" 'netlift_coreclr_apply_v2_feature_negotiation'
require_text "$accessor" '*table = NULL;'
require_text "$accessor" 'lift_v2_validate_function_table('
require_text "$coreclr_header" '#define NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2 0'
require_text "$coreclr_header" 'NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2 must be 0 or 1'
require_text "$v1_adapter" 'info->feature_bits = netlift_coreclr_apply_v2_feature_negotiation('
require_text "$v1_adapter" 'V2 lifecycle capability withheld; readiness status %d.'
require_text "$project_yml" 'CallbackLifecycleV2Accessor.c'
require_text "$project_pbx" 'CallbackLifecycleV2Accessor.c in Sources'
require_text "$project_yml" 'CallbackLifecycleV2Adapter.c'
require_text "$project_pbx" 'CallbackLifecycleV2Adapter.c in Sources'
require_text "$project_yml" 'CallbackLifecycleV2State.c'
require_text "$project_pbx" 'CallbackLifecycleV2State.c in Sources'
require_text "$state" 'bool release_in_flight;'
require_text "$state" 'lifecycle->released = true;'
require_text "$state_header" 'lift_v2_activate_operation'
require_text "$state" 'lift_v2_activate_operation('
require_text "$state" '!is_valid_utf8(reply->diagnostic.data, reply->diagnostic.length)'
require_text "$tests" 'test_delayed_reply_after_callback_returns'
require_text "$tests" 'test_diagnostic_utf8_and_size_boundaries'
require_text "$tests" 'LIFT_V2_MAX_DIAGNOSTIC_BYTES + 1'
require_text "$tests" '0xed, 0xa0, 0x80'
require_text "$tests" 'test_reply_claim_revoke_ack_barrier'
require_text "$tests" 'test_generation_and_operation_drain'
require_text "$tests" 'test_parent_requests_cancellation_and_status_ownership'
require_text "$tests" 'test_concurrent_request_cancel_join_and_drain_barrier'
require_text "$tests" 'test_failed_cancel_join_then_retry'
require_text "$tests" 'state.managed_reply_status = LIFT_STATUS_MANAGED_FAILURE;'
require_text "$tests" 'assert(lift_v2_ack_drain(lifecycle, 109, 113) == LIFT_STATUS_UNAVAILABLE);'
require_text "$tests" 'test_terminal_result_waits_for_request_callback_return'
require_text "$tests" 'state->lifecycle,'
require_text "$tests" '== LIFT_STATUS_UNAVAILABLE);'
require_text "$tests" 'cancel_request_with_managed_status('
require_text "$tests" 'lift_v2_finish_request_cancel('
require_text "$tests" 'assert(state.managed_free_count == 1);'
require_text "$tests" 'LIFT_STATUS_MANAGED_FAILURE) == LIFT_STATUS_MANAGED_FAILURE'
require_text "$tests" 'test_reentrant_ack_drain_from_revoke'
require_text "$tests" 'test_release_callback_completion_barrier'
require_text "$tests" 'test_activation_rejects_cancelled_and_revoked_operations'
require_text "$tests" 'assert(!lift_v2_is_released(lifecycle));'
require_text "$tests" 'assert(!lift_v2_lifecycle_destroy(lifecycle));'
require_text "$tests" 'test_release_callback_completion_barrier'
require_text "$accessor_tests" 'LIFT_STATUS_UNSUPPORTED'
require_text "$accessor_tests" 'LIFT_STATUS_NOT_READY'
require_text "$accessor_tests" 'v1_register_count == 0'
require_text "$accessor_tests" 'table == NULL'
require_text "$accessor_tests" 'test_positive_negotiation_and_revoke'
require_text "$accessor_tests" 'test_accessor_revalidates_malformed_table'
require_text "$accessor_tests" 'get_v2_and_register'
require_text "$adapter" 'static const lift_function_table_v2 v2_function_table ='
require_text "$adapter" '.cancel_request = &cancel_request,'
require_text "$adapter" '.reply_typed = &reply_typed,'
require_text "$adapter" '.ack_drain = &ack_drain,'
require_text "$adapter" 'lift_v2_activate_operation('
require_text "$adapter" 'copy_managed_status('
require_text "$adapter" 'netlift_managed_cancel_request_v2('
require_text "$adapter" 'pthread_cond_wait(&registration_changed, &registration_mutex);'
require_text "$adapter" 'pthread_equal((*current_operation)->activation_thread, pthread_self())'
require_text "$adapter" 'request_token_is_tracked(registration, request_token)'
require_text "$adapter" 'remove_operation_request_record('
require_text "$adapter" 'terminal_result_in_flight'
require_text "$adapter" 'store_pending_result('
require_text "$adapter" 'flush_pending_result(registration, operation_token);'
require_text "$adapter" 'cancel_order_in_flight'
require_text "$adapter" 'uint32_t finish_status = managed_cancellation_succeeded'
require_text "$adapter" 'managed_cancellation_succeeded'
require_text "$adapter" '*cleanup_lease_held = true;'
require_text "$adapter" 'lift_v2_begin_adapter_cleanup('
require_text "$adapter" 'lift_v2_end_adapter_cleanup(registration->lifecycle)'
require_text "$adapter" 'pause_after_cleanup_for_test(NETLIFT_V2_TEST_CLEANUP_OPERATION);'
require_text "$adapter" 'pause_after_cleanup_for_test(NETLIFT_V2_TEST_CLEANUP_REVOKE);'
require_text "$adapter" 'optional status diagnostic could not be copied or released'
require_text "$adapter" 'return LIFT_STATUS_BUFFER_TOO_LARGE;'
require_text "$adapter_header" 'netlift_coreclr_v2_table_candidate'
require_text "$adapter_header" 'netlift_v2_adapter_test_wait_for_cleanup'
require_text "$managed" 'public static unsafe int StartOrderV2('
require_text "$managed" 'public static int CancelOrderV2('
require_text "$managed" 'public static unsafe uint ReplyTypedV2('
require_text "$managed" 'public static unsafe uint CancelRequestV2('
require_text "$managed" 'public static unsafe uint FreeBufferV2('
require_text "$managed" 'PendingConfirmationsV2.TryAdd(key, completion)'
require_text "$managed" 'completion.TrySetResult(new NativeReplySnapshot(ReplyCancelled, false, string.Empty))'
require_text "$managed" 'new NativeConfirmationV2('
require_text "$coreclr" '"ReplyTypedV2"'
require_text "$coreclr" '"CancelRequestV2"'
require_text "$coreclr" '"FreeBufferV2"'
require_text "$adapter_tests" 'fake_reply_status = LIFT_STATUS_MANAGED_FAILURE;'
require_text "$adapter_tests" 'fake_cancel_status = LIFT_STATUS_MANAGED_FAILURE;'
require_text "$adapter_tests" 'fake_duplicate_request_next = true;'
require_text "$adapter_tests" 'fake_cancel_order_count == cancel_order_count_before'
require_text "$adapter_tests" 'netlift_v2_adapter_test_fail_next_status_copy();'
require_text "$adapter_tests" 'fixture.reenter_cancel_from_request = true;'
require_text "$adapter_tests" 'fake_cancel_with_optional_message = true;'
require_text "$adapter_tests" 'fake_reply_free_count == 2'
require_text "$adapter_tests" 'fake_managed_free_status = LIFT_STATUS_INVALID_RELEASE;'
require_text "$adapter_tests" 'fake_publish_result_during_request = true;'
require_text "$adapter_tests" 'fake_publish_result_during_reply = true;'
require_text "$adapter_tests" 'fake_publish_result_during_cancel = true;'
require_text "$adapter_tests" 'finish_revoke(table, &fixture);'
require_text "$adapter_tests" 'test_request_cancel_retirement_receipts(table);'
require_text "$adapter_tests" 'test_parent_cancel_joins_public_cancel_and_revoke(table);'
require_text "$adapter_tests" 'netlift_v2_adapter_test_wait_for_request_cancel_waiters('
require_text "$adapter_tests" 'test_cancel_cleanup_lease_blocks_external_and_reentrant_ack'
require_text "$adapter_tests" 'test_operation_cleanup_lease_blocks_ack_after_child_lease_ends'
require_text "$adapter_tests" 'test_revoke_cleanup_lease_blocks_ack_after_nested_cancel'
require_text "$adapter_tests" 'test_reentrant_ack_from_revoke_survives_outer_lease'
require_text "$adapter_tests" 'netlift_v2_adapter_test_wait_for_cleanup()'
require_text "$app" '@Environment(\.scenePhase) private var scenePhase'
require_text "$app" '.onChange(of: scenePhase)'
require_text "$app" '.onDisappear'
require_text "$app" 'func scenePhaseChanged(_ phase: ScenePhase)'
require_text "$app" 'func sceneDidDisappear()'
require_text "$app" 'private func closeForSceneLoss()'
require_text "$app" 'owner?.requestClose()'
require_text "$app" 'guard !sceneLossHandled else { throw CancellationError() }'
require_text "$app" 'decision == nil, !sceneLossHandled'
require_text "$app" 'FAIL-CLOSED: production V2 owner retained'
require_text "$app" 'proof.hasUnreleasedOwner'
require_text "$app" 'MainActor prompts=1, OnRelease=1'
require_text "$state_runner" '-DNETLIFT_V2_TESTING'
require_text "$generated_swift" 'Task { await cancelRequest(request, record) }'
require_text "$adapter_runner" 'CallbackLifecycleV2Adapter.c'
require_text "$adapter_runner" '-DNETLIFT_V2_TESTING'
if grep -Fq 'source.replace(old, new, 1)' "$generation_script" ||
   grep -Fq 'requests[record.operation] == nil' "$generation_script"; then
    echo "The generator must not rewrite V2 request drain/cancellation bookkeeping." >&2
    exit 1
fi
require_text "$script_dir/tests/TypedReplyDescriptorTest.cs" 'Malformed UTF-8 was not rejected explicitly'
require_text "$script_dir/tests/TypedReplyDescriptorTest.cs" 'valid 4096-byte diagnostic'
require_text "$v1_header" '#define LIFT_ABI_VERSION 1u'
require_text "$v1_header" 'LIFT_SCHEMA_HASH_HEX "eeda280e446ad6a906222f1ca9fd458c80b2fd279507804449f3700322b36f05"'
if grep -Fq 'LIFT_FEATURE_CALLBACK_LIFECYCLE_V2' "$v1_header" ||
   grep -Fq 'NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1' "$project_yml" ||
   grep -Fq 'NETLIFT_ENABLE_CALLBACK_LIFECYCLE_V2=1' "$project_pbx"; then
    echo "V2 0x20 must remain absent from V1 ABI and default sample configuration." >&2
    exit 1
fi

echo "PASS V2 host source contract; V1 schema/layout and default-off feature gate remain unchanged."
