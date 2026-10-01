#import <Foundation/Foundation.h>
#include <string.h>

#import "Generated/netlift.h"
#import "StreamFacadeAdapter.h"

typedef uint32_t (*managed_stream_start_fn)(
    const lift_config *,
    lift_runtime_info *,
    lift_status *);
typedef uint32_t (*managed_stream_subscribe_fn)(
    uint32_t,
    uint32_t,
    const lift_bytes_view *,
    const lift_stream_callbacks *,
    uint32_t,
    uint32_t,
    uint32_t,
    uint64_t *,
    lift_status *);
typedef uint32_t (*managed_stream_token_fn)(uint64_t, lift_status *);
typedef uint32_t (*managed_stream_request_fn)(uint64_t, uint32_t, lift_status *);
typedef uint32_t (*managed_stream_overflow_probe_fn)(uint32_t, lift_status *);

@interface NetLiftStreamV4CallbackBox : NSObject
@property(nonatomic, copy) netlift_stream_v4_on_next_block onNext;
@property(nonatomic, copy) netlift_stream_v4_on_terminal_block onTerminal;
@property(nonatomic, copy) netlift_stream_v4_on_release_block onRelease;
@end

@implementation NetLiftStreamV4CallbackBox
@end

static void
stream_on_next(void *context, uint64_t token, const uint8_t *bytes, uint32_t length)
{
    NetLiftStreamV4CallbackBox *box = (__bridge NetLiftStreamV4CallbackBox *)context;
    if (box.onNext != nil)
        box.onNext(token, bytes, length);
}

static void
stream_on_terminal(
    void *context,
    uint64_t token,
    uint32_t terminal,
    uint32_t status,
    const uint8_t *diagnostic,
    uint32_t diagnostic_length)
{
    NetLiftStreamV4CallbackBox *box = (__bridge NetLiftStreamV4CallbackBox *)context;
    if (box.onTerminal != nil)
        box.onTerminal(token, terminal, status, diagnostic, diagnostic_length);
}

static void
stream_on_release(void *context, uint64_t token)
{
    NetLiftStreamV4CallbackBox *box = (__bridge_transfer NetLiftStreamV4CallbackBox *)context;
    if (box.onRelease != nil)
        box.onRelease(token);
}

static uint32_t
status_code(uint32_t result, const lift_status *status)
{
    return status->code != LIFT_STATUS_OK ? status->code : result;
}

uint32_t
netlift_stream_v4_start(
    uint32_t abi_version,
    const uint8_t *schema_hash,
    uint32_t schema_hash_length,
    uint32_t *runtime_abi_version,
    uint32_t *feature_bits,
    uint8_t *runtime_schema_hash,
    uint32_t runtime_schema_hash_length)
{
    if (schema_hash == NULL || schema_hash_length != 32 ||
        runtime_abi_version == NULL || feature_bits == NULL ||
        runtime_schema_hash == NULL || runtime_schema_hash_length < 32)
        return LIFT_STATUS_INVALID_ARGUMENT;

    managed_stream_start_fn start =
        (managed_stream_start_fn)netlift_coreclr_get_stream_v4_delegate(NETLIFT_STREAM_V4_START);
    if (start == NULL)
        return LIFT_STATUS_UNAVAILABLE;

    lift_config config = {
        .struct_size = sizeof(lift_config),
        .abi_version = abi_version,
    };
    memcpy(config.schema_hash, schema_hash, sizeof(config.schema_hash));
    lift_runtime_info info = {
        .struct_size = sizeof(lift_runtime_info),
    };
    lift_status status = {0};
    uint32_t result = start(&config, &info, &status);
    uint32_t code = status_code(result, &status);
    if (code == LIFT_STATUS_OK) {
        *runtime_abi_version = info.abi_version;
        *feature_bits = info.feature_bits;
        memcpy(runtime_schema_hash, info.schema_hash, sizeof(info.schema_hash));
    }
    return code;
}

uint32_t
netlift_stream_v4_subscribe(
    uint32_t service_id,
    uint32_t subscription_id,
    uint32_t initial_credit,
    uint32_t transport_capacity,
    uint32_t max_item_bytes,
    netlift_stream_v4_on_next_block on_next,
    netlift_stream_v4_on_terminal_block on_terminal,
    netlift_stream_v4_on_release_block on_release,
    uint64_t *token)
{
    if (token == NULL || *token != 0 || on_next == nil ||
        on_terminal == nil || on_release == nil)
        return LIFT_STATUS_INVALID_ARGUMENT;

    managed_stream_subscribe_fn subscribe =
        (managed_stream_subscribe_fn)netlift_coreclr_get_stream_v4_delegate(
            NETLIFT_STREAM_V4_SUBSCRIBE);
    if (subscribe == NULL)
        return LIFT_STATUS_UNAVAILABLE;

    NetLiftStreamV4CallbackBox *box = [NetLiftStreamV4CallbackBox new];
    box.onNext = on_next;
    box.onTerminal = on_terminal;
    box.onRelease = on_release;
    void *context = (__bridge_retained void *)box;
    lift_stream_callbacks callbacks = {
        .struct_size = sizeof(lift_stream_callbacks),
        .abi_version = LIFT_ABI_VERSION,
        .context = context,
        .on_next = &stream_on_next,
        .on_terminal = &stream_on_terminal,
        .on_release = &stream_on_release,
    };
    lift_bytes_view arguments = {0};
    lift_status status = {0};
    uint32_t result = subscribe(
        service_id,
        subscription_id,
        &arguments,
        &callbacks,
        initial_credit,
        transport_capacity,
        max_item_bytes,
        token,
        &status);
    uint32_t code = status_code(result, &status);
    if (code != LIFT_STATUS_OK || *token == 0) {
        (void)CFBridgingRelease(context);
        return code != LIFT_STATUS_OK ? code : LIFT_STATUS_UNAVAILABLE;
    }
    return LIFT_STATUS_OK;
}

uint32_t
netlift_stream_v4_activate(uint64_t token)
{
    managed_stream_token_fn activate =
        (managed_stream_token_fn)netlift_coreclr_get_stream_v4_delegate(
            NETLIFT_STREAM_V4_ACTIVATE);
    if (activate == NULL)
        return LIFT_STATUS_UNAVAILABLE;
    lift_status status = {0};
    return status_code(activate(token, &status), &status);
}

uint32_t
netlift_stream_v4_request(uint64_t token, uint32_t credits)
{
    managed_stream_request_fn request =
        (managed_stream_request_fn)netlift_coreclr_get_stream_v4_delegate(
            NETLIFT_STREAM_V4_REQUEST);
    if (request == NULL)
        return LIFT_STATUS_UNAVAILABLE;
    lift_status status = {0};
    return status_code(request(token, credits, &status), &status);
}

uint32_t
netlift_stream_v4_unsubscribe(uint64_t token)
{
    managed_stream_token_fn unsubscribe =
        (managed_stream_token_fn)netlift_coreclr_get_stream_v4_delegate(
            NETLIFT_STREAM_V4_UNSUBSCRIBE);
    if (unsubscribe == NULL)
        return LIFT_STATUS_UNAVAILABLE;
    lift_status status = {0};
    return status_code(unsubscribe(token, &status), &status);
}

uint32_t
netlift_stream_v4_run_overflow_probe(uint32_t update_count)
{
    managed_stream_overflow_probe_fn run_probe =
        (managed_stream_overflow_probe_fn)netlift_coreclr_get_stream_v4_delegate(
            NETLIFT_STREAM_V4_OVERFLOW_PROBE);
    if (run_probe == NULL)
        return LIFT_STATUS_UNAVAILABLE;
    lift_status status = {0};
    return status_code(run_probe(update_count, &status), &status);
}
