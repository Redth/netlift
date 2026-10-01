#define _DARWIN_C_SOURCE

#include <coreclr_delegates.h>
#include <hostfxr.h>
#include <nethost.h>

#include <dlfcn.h>
#include <limits.h>
#include <pthread.h>
#include <sched.h>
#include <stdbool.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

#include "managed_abi.h"

_Static_assert(sizeof(managed_owned_buffer_v1) == 32, "owned buffer ABI layout mismatch");
_Static_assert(sizeof(managed_buffer_stats_v1) == 32, "stats ABI layout mismatch");

static void require_true(int condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL: %s\n", message);
        exit(2);
    }
}

static void expect_status(
    uint32_t actual,
    uint32_t expected,
    const char *test_name)
{
    if (actual != expected) {
        fprintf(stderr, "FAIL: %s returned status %u; expected %u\n",
            test_name, actual, expected);
        exit(2);
    }
    printf("PASS %s status=%u\n", test_name, actual);
}

static void *load_managed_method(
    load_assembly_and_get_function_pointer_fn loader,
    const char *assembly_path,
    const char *method_name)
{
    void *function = NULL;
    int rc = loader(
        assembly_path,
        "NetLift.BufferOwnership.Managed.BufferApi, NetLift.BufferOwnership.Managed",
        method_name,
        UNMANAGEDCALLERSONLY_METHOD,
        NULL,
        &function);
    if (rc != 0 || function == NULL) {
        fprintf(stderr, "FAIL: load managed method %s returned 0x%x\n", method_name, rc);
        exit(2);
    }
    return function;
}

static managed_buffer_api start_coreclr(
    const char *dotnet_root,
    const char *runtime_config,
    const char *assembly_path)
{
    char_t hostfxr_path[PATH_MAX];
    size_t hostfxr_path_size = sizeof(hostfxr_path) / sizeof(hostfxr_path[0]);
    struct get_hostfxr_parameters parameters = {
        .size = sizeof(parameters),
        .assembly_path = NULL,
        .dotnet_root = dotnet_root,
    };
    int rc = get_hostfxr_path(hostfxr_path, &hostfxr_path_size, &parameters);
    require_true(rc == 0, "get_hostfxr_path failed");

    void *hostfxr_library = dlopen(hostfxr_path, RTLD_LAZY | RTLD_LOCAL);
    require_true(hostfxr_library != NULL, "dlopen(hostfxr) failed");
    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)dlsym(
            hostfxr_library, "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_runtime_delegate =
        (hostfxr_get_runtime_delegate_fn)dlsym(
            hostfxr_library, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_context =
        (hostfxr_close_fn)dlsym(hostfxr_library, "hostfxr_close");
    require_true(initialize != NULL && get_runtime_delegate != NULL && close_context != NULL,
        "hostfxr exports are missing");

    hostfxr_handle context = NULL;
    rc = initialize(runtime_config, NULL, &context);
    require_true(rc == 0 && context != NULL, "hostfxr runtime-config initialization failed");

    void *loader_pointer = NULL;
    rc = get_runtime_delegate(
        context, hdt_load_assembly_and_get_function_pointer, &loader_pointer);
    require_true(rc == 0 && loader_pointer != NULL,
        "hostfxr failed to provide assembly loader");
    require_true(close_context(context) == 0, "hostfxr_close failed");

    load_assembly_and_get_function_pointer_fn loader =
        (load_assembly_and_get_function_pointer_fn)loader_pointer;
    managed_buffer_api api = {
        .open_host = (managed_open_host_fn)load_managed_method(loader, assembly_path, "OpenHost"),
        .close_host = (managed_close_host_fn)load_managed_method(loader, assembly_path, "CloseHost"),
        .allocate = (managed_allocate_fn)load_managed_method(loader, assembly_path, "Allocate"),
        .release = (managed_release_fn)load_managed_method(loader, assembly_path, "Release"),
        .deliver = (managed_deliver_fn)load_managed_method(loader, assembly_path, "Deliver"),
        .get_stats = (managed_get_stats_fn)load_managed_method(loader, assembly_path, "GetStats"),
        .allocator_shutdown = (managed_allocator_shutdown_fn)load_managed_method(
            loader, assembly_path, "AllocatorShutdown"),
    };
    printf("PASS CoreCLR loaded managed allocator assembly\n");
    return api;
}

static void init_output(managed_owned_buffer_v1 *output)
{
    memset(output, 0, sizeof(*output));
    output->struct_size = sizeof(*output);
    output->abi_version = 1;
}

static void get_stats(
    managed_buffer_api api,
    uint64_t host,
    managed_buffer_stats_v1 *stats)
{
    memset(stats, 0, sizeof(*stats));
    stats->struct_size = sizeof(*stats);
    stats->abi_version = 1;
    expect_status(api.get_stats(host, stats), MANAGED_BUFFER_OK, "managed stats");
}

static int test_guarded_short_descriptor(managed_buffer_api api, uint64_t host)
{
    long system_page_size = sysconf(_SC_PAGESIZE);
    require_true(system_page_size > 0, "could not query system page size");
    size_t page_size = (size_t)system_page_size;
    size_t mapping_size = page_size * 2;
    uint8_t *mapping = mmap(NULL, mapping_size, PROT_READ | PROT_WRITE,
        MAP_PRIVATE | MAP_ANON, -1, 0);
    require_true(mapping != MAP_FAILED, "could not allocate guarded descriptor");
    uint8_t *guard_page = mapping + page_size;
    require_true(mprotect(guard_page, page_size, PROT_NONE) == 0,
        "could not protect descriptor guard page");

    managed_owned_buffer_v1 *short_output =
        (managed_owned_buffer_v1 *)(guard_page - sizeof(uint64_t));
    uint32_t declared_size = sizeof(uint32_t);
    memcpy(short_output, &declared_size, sizeof(declared_size));
    static const uint8_t byte = 0x42;
    expect_status(
        api.allocate(host, &byte, sizeof(byte), short_output),
        MANAGED_BUFFER_STRUCT_TOO_SMALL,
        "managed short descriptor size=4");

    struct {
        uint32_t struct_size;
        uint32_t abi_version;
    } short_header = {sizeof(short_header), 1};
    memcpy(short_output, &short_header, sizeof(short_header));
    expect_status(
        api.allocate(host, &byte, sizeof(byte), short_output),
        MANAGED_BUFFER_STRUCT_TOO_SMALL,
        "managed guarded descriptor size=8");

    require_true(munmap(mapping, mapping_size) == 0,
        "could not unmap guarded descriptor");
    return 0;
}

static int test_bounded_and_empty_outputs(managed_buffer_api api, uint64_t host)
{
    static const uint8_t one_byte = 0x33;
    managed_owned_buffer_v1 output;
    init_output(&output);
    expect_status(
        api.allocate(host, &one_byte, UINT64_MAX, &output),
        MANAGED_BUFFER_TOO_LARGE,
        "managed UINT64_MAX length");
    require_true(output.token == 0 && output.data == NULL && output.length == 0,
        "oversized managed result was not cleared");
    init_output(&output);
    expect_status(
        api.allocate(host, &one_byte, 1024u * 1024u + 1, &output),
        MANAGED_BUFFER_TOO_LARGE,
        "managed maximum plus one");
    require_true(output.token == 0 && output.data == NULL && output.length == 0,
        "over-bound managed result was not cleared");

    uint8_t *maximum_payload = malloc(1024u * 1024u);
    require_true(maximum_payload != NULL, "could not allocate maximum-sized native input");
    memset(maximum_payload, 0xa5, 1024u * 1024u);
    init_output(&output);
    expect_status(
        api.allocate(host, maximum_payload, 1024u * 1024u, &output),
        MANAGED_BUFFER_OK,
        "managed maximum-size output");
    require_true(output.length == 1024u * 1024u &&
        output.data[0] == 0xa5 && output.data[output.length - 1] == 0xa5,
        "managed maximum-size output was truncated or corrupted");
    expect_status(api.release(host, output.token), MANAGED_BUFFER_OK,
        "managed maximum-size release");
    free(maximum_payload);

    init_output(&output);
    expect_status(
        api.allocate(host, NULL, 0, &output),
        MANAGED_BUFFER_OK,
        "managed zero-length output");
    require_true(output.token == 0 && output.data == NULL && output.length == 0,
        "zero-length output was not canonical");

    init_output(&output);
    output.abi_version++;
    expect_status(
        api.allocate(host, &one_byte, sizeof(one_byte), &output),
        MANAGED_BUFFER_VERSION_MISMATCH,
        "managed output ABI version mismatch");
    return 0;
}

static void test_managed_reuse(managed_buffer_api api, uint64_t host)
{
    static const uint8_t payload[] = "managed NativeMemory address recycle";
    managed_owned_buffer_v1 first;
    init_output(&first);
    expect_status(
        api.allocate(host, payload, sizeof(payload) - 1, &first),
        MANAGED_BUFFER_OK,
        "managed first allocation");
    require_true(first.data != NULL && first.length == sizeof(payload) - 1 &&
        memcmp(first.data, payload, sizeof(payload) - 1) == 0,
        "managed output descriptor did not preserve copied bytes");
    require_true((uint32_t)(first.token >> 32) == host,
        "managed token did not carry its host identity");

    const uint8_t *first_pointer = first.data;
    uint64_t first_token = first.token;
    uint16_t first_generation = (uint16_t)(first_token >> 16);
    expect_status(api.release(host, first_token), MANAGED_BUFFER_OK,
        "managed release first generation");
    expect_status(api.release(host, first_token), MANAGED_BUFFER_STALE_TOKEN,
        "managed duplicate release");

    managed_owned_buffer_v1 second;
    init_output(&second);
    expect_status(
        api.allocate(host, payload, sizeof(payload) - 1, &second),
        MANAGED_BUFFER_OK,
        "managed reused allocation");
    require_true(second.data == first_pointer,
        "managed test allocator did not deterministically recycle its address");
    require_true(second.token != first_token &&
        (uint16_t)(second.token >> 16) > first_generation,
        "managed allocator did not advance generation on reuse");
    expect_status(api.release(host, first_token), MANAGED_BUFFER_STALE_TOKEN,
        "managed stale token after address reuse");
    require_true(memcmp(second.data, payload, sizeof(payload) - 1) == 0,
        "stale managed release damaged current allocation");
    expect_status(api.release(host, second.token), MANAGED_BUFFER_OK,
        "managed release current generation");
}

static void test_managed_cross_host_reuse(
    managed_buffer_api api,
    uint64_t first_host,
    uint64_t second_host)
{
    static const uint8_t payload[] = "managed cross-host buffer";
    managed_owned_buffer_v1 from_first;
    init_output(&from_first);
    expect_status(
        api.allocate(first_host, payload, sizeof(payload) - 1, &from_first),
        MANAGED_BUFFER_OK,
        "managed cross-host source allocation");
    const uint8_t *recycled_address = from_first.data;
    uint64_t stale_first_token = from_first.token;
    expect_status(api.release(first_host, stale_first_token), MANAGED_BUFFER_OK,
        "managed cross-host source release");

    managed_owned_buffer_v1 from_second;
    init_output(&from_second);
    expect_status(
        api.allocate(second_host, payload, sizeof(payload) - 1, &from_second),
        MANAGED_BUFFER_OK,
        "managed second-host allocation");
    require_true(from_second.data == recycled_address,
        "managed cross-host allocation did not reuse the source address");
    expect_status(api.release(second_host, stale_first_token), MANAGED_BUFFER_WRONG_HOST,
        "managed cross-host stale token");
    require_true(memcmp(from_second.data, payload, sizeof(payload) - 1) == 0,
        "cross-host release damaged current managed allocation");
    expect_status(api.release(second_host, from_second.token), MANAGED_BUFFER_OK,
        "managed second-host release");
}

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    bool entered;
    bool continue_callback;
    bool data_intact;
    uint32_t callback_depth;
    const uint8_t *expected;
    size_t expected_length;
} callback_gate;

typedef struct {
    managed_buffer_api api;
    uint64_t host;
    uint64_t token;
    callback_gate *gate;
    uint32_t result;
} delivery_args;

static void borrowed_callback(void *context, const uint8_t *data, uint64_t length)
{
    callback_gate *gate = context;
    pthread_mutex_lock(&gate->mutex);
    gate->callback_depth++;
    gate->entered = true;
    pthread_cond_broadcast(&gate->condition);
    while (!gate->continue_callback)
        pthread_cond_wait(&gate->condition, &gate->mutex);
    gate->data_intact = length == gate->expected_length &&
        memcmp(data, gate->expected, gate->expected_length) == 0;
    gate->callback_depth--;
    pthread_cond_broadcast(&gate->condition);
    pthread_mutex_unlock(&gate->mutex);
}

static void *deliver_on_thread(void *context)
{
    delivery_args *args = context;
    args->result = args->api.deliver(
        args->host, args->token, borrowed_callback, args->gate);
    return NULL;
}

static void test_release_during_managed_borrow(
    managed_buffer_api api,
    uint64_t host)
{
    static const uint8_t payload[] = "managed completion remains borrowed";
    managed_owned_buffer_v1 output;
    init_output(&output);
    expect_status(
        api.allocate(host, payload, sizeof(payload) - 1, &output),
        MANAGED_BUFFER_OK,
        "managed borrowed allocation");

    callback_gate gate = {
        .mutex = PTHREAD_MUTEX_INITIALIZER,
        .condition = PTHREAD_COND_INITIALIZER,
        .expected = payload,
        .expected_length = sizeof(payload) - 1,
    };
    delivery_args args = {
        .api = api,
        .host = host,
        .token = output.token,
        .gate = &gate,
        .result = MANAGED_BUFFER_INVALID_ARGUMENT,
    };
    pthread_t thread;
    require_true(pthread_create(&thread, NULL, deliver_on_thread, &args) == 0,
        "could not start managed completion thread");

    pthread_mutex_lock(&gate.mutex);
    while (!gate.entered)
        pthread_cond_wait(&gate.condition, &gate.mutex);
    require_true(gate.callback_depth == 1,
        "native callback did not enter before concurrent release");
    pthread_mutex_unlock(&gate.mutex);

    expect_status(
        api.release(host, output.token),
        MANAGED_BUFFER_RELEASE_DEFERRED,
        "managed release during borrowed callback");
    expect_status(
        api.release(host, output.token),
        MANAGED_BUFFER_ALREADY_RELEASING,
        "managed duplicate deferred release");

    managed_buffer_stats_v1 stats;
    get_stats(api, host, &stats);
    require_true(stats.live_slots == 1 && stats.release_pending == 1 &&
        stats.active_borrows == 1,
        "managed allocator reclaimed or detached buffer before callback drain");

    pthread_mutex_lock(&gate.mutex);
    gate.continue_callback = true;
    pthread_cond_broadcast(&gate.condition);
    pthread_mutex_unlock(&gate.mutex);
    require_true(pthread_join(thread, NULL) == 0,
        "could not join managed completion thread");

    expect_status(args.result, MANAGED_BUFFER_OK, "managed completion callback drain");
    require_true(gate.data_intact && gate.callback_depth == 0,
        "managed callback observed reclaimed data or failed to drain");
    get_stats(api, host, &stats);
    require_true(stats.live_slots == 0 && stats.release_pending == 0 &&
        stats.active_borrows == 0,
        "managed allocation did not finalize after callback return");
    expect_status(api.release(host, output.token), MANAGED_BUFFER_STALE_TOKEN,
        "managed token stale after callback drain");

    pthread_cond_destroy(&gate.condition);
    pthread_mutex_destroy(&gate.mutex);
}

typedef struct {
    managed_buffer_api api;
    uint64_t host;
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    bool first_attempt_complete;
    bool first_output_live;
    bool allow_first_release;
    uint32_t first_status;
    atomic_bool close_succeeded;
    atomic_uint failures;
} close_race_state;

static void *allocate_until_host_closes(void *context)
{
    close_race_state *state = context;
    static const uint8_t payload = 0x5a;
    managed_owned_buffer_v1 output;
    init_output(&output);
    uint32_t result = state->api.allocate(state->host, &payload, sizeof(payload), &output);
    pthread_mutex_lock(&state->mutex);
    state->first_status = result;
    state->first_output_live = result == MANAGED_BUFFER_OK;
    state->first_attempt_complete = true;
    pthread_cond_broadcast(&state->condition);
    if (result != MANAGED_BUFFER_OK) {
        atomic_fetch_add(&state->failures, 1);
        pthread_mutex_unlock(&state->mutex);
        return NULL;
    }
    while (!state->allow_first_release)
        pthread_cond_wait(&state->condition, &state->mutex);
    pthread_mutex_unlock(&state->mutex);

    if (state->api.release(state->host, output.token) != MANAGED_BUFFER_OK)
        atomic_fetch_add(&state->failures, 1);

    for (uint32_t iteration = 0; iteration < 10000; iteration++) {
        init_output(&output);
        result = state->api.allocate(state->host, &payload, sizeof(payload), &output);
        if (result == MANAGED_BUFFER_STALE_TOKEN)
            return NULL;
        if (result != MANAGED_BUFFER_OK) {
            atomic_fetch_add(&state->failures, 1);
            return NULL;
        }
        if (atomic_load(&state->close_succeeded))
            atomic_fetch_add(&state->failures, 1);
        if (state->api.release(state->host, output.token) != MANAGED_BUFFER_OK)
            atomic_fetch_add(&state->failures, 1);
        sched_yield();
    }
    return NULL;
}

static void test_close_vs_allocate_race(managed_buffer_api api)
{
    uint64_t host = 0;
    expect_status(api.open_host(&host), MANAGED_BUFFER_OK, "managed open close-race host");
    close_race_state state = {
        .api = api,
        .host = host,
        .mutex = PTHREAD_MUTEX_INITIALIZER,
        .condition = PTHREAD_COND_INITIALIZER,
    };
    pthread_t worker;
    require_true(pthread_create(&worker, NULL, allocate_until_host_closes, &state) == 0,
        "could not start managed close/allocation race");

    pthread_mutex_lock(&state.mutex);
    while (!state.first_attempt_complete)
        pthread_cond_wait(&state.condition, &state.mutex);
    pthread_mutex_unlock(&state.mutex);
    expect_status(state.first_status, MANAGED_BUFFER_OK,
        "managed race worker holds live output");

    expect_status(api.close_host(host), MANAGED_BUFFER_BUSY,
        "managed close refuses live output");
    pthread_mutex_lock(&state.mutex);
    state.allow_first_release = true;
    pthread_cond_broadcast(&state.condition);
    pthread_mutex_unlock(&state.mutex);

    bool closed = false;
    for (uint32_t attempt = 0; attempt < 10000; attempt++) {
        uint32_t status = api.close_host(host);
        if (status == MANAGED_BUFFER_OK) {
            atomic_store(&state.close_succeeded, true);
            closed = true;
            break;
        }
        if (status != MANAGED_BUFFER_BUSY) {
            fprintf(stderr, "FAIL: close/allocation race returned status %u\n", status);
            atomic_fetch_add(&state.failures, 1);
            break;
        }
        sched_yield();
    }
    require_true(pthread_join(worker, NULL) == 0,
        "could not join managed close/allocation race");
    require_true(closed, "managed host did not close within bounded race attempts");
    require_true(atomic_load(&state.failures) == 0,
        "allocation succeeded after close or left an unreleased output");

    managed_owned_buffer_v1 output;
    init_output(&output);
    static const uint8_t byte = 0x6b;
    expect_status(api.allocate(host, &byte, sizeof(byte), &output),
        MANAGED_BUFFER_STALE_TOKEN,
        "managed allocation after close");
    managed_buffer_stats_v1 stats = {
        .struct_size = sizeof(stats),
        .abi_version = 1,
    };
    expect_status(api.get_stats(host, &stats), MANAGED_BUFFER_STALE_TOKEN,
        "managed stats after close");
    pthread_cond_destroy(&state.condition);
    pthread_mutex_destroy(&state.mutex);
    printf("PASS managed close/allocation race: no post-close success or orphan buffer\n");
}

int main(int argc, char **argv)
{
    require_true(argc == 4,
        "usage: managed-host <dotnet-root> <managed.dll> <runtimeconfig.json>");
    managed_buffer_api api = start_coreclr(argv[1], argv[3], argv[2]);

    uint64_t first_host = 0;
    uint64_t second_host = 0;
    expect_status(api.open_host(&first_host), MANAGED_BUFFER_OK, "managed open host A");
    expect_status(api.open_host(&second_host), MANAGED_BUFFER_OK, "managed open host B");
    require_true(first_host != 0 && second_host != 0 && first_host != second_host,
        "managed host IDs are not unique");

    test_guarded_short_descriptor(api, first_host);
    test_bounded_and_empty_outputs(api, first_host);
    test_managed_reuse(api, first_host);
    test_managed_cross_host_reuse(api, first_host, second_host);
    test_release_during_managed_borrow(api, first_host);
    test_close_vs_allocate_race(api);

    managed_buffer_stats_v1 stats;
    get_stats(api, first_host, &stats);
    require_true(stats.live_slots == 0 && stats.active_borrows == 0,
        "managed host retained live output buffers after suite");
    require_true(stats.allocator_reuses >= 3,
        "managed NativeMemory allocator did not exercise recycled addresses");

    expect_status(api.close_host(first_host), MANAGED_BUFFER_OK, "managed close host A");
    expect_status(api.close_host(second_host), MANAGED_BUFFER_OK, "managed close host B");
    expect_status(api.allocator_shutdown(), MANAGED_BUFFER_OK,
        "managed allocator shutdown");
    printf("PASS managed NativeMemory output ownership across native C/CoreCLR boundary\n");
    return 0;
}
