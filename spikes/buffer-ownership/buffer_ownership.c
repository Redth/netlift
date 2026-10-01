#define _DARWIN_C_SOURCE
#define _POSIX_C_SOURCE 200809L

#include "buffer_ownership.h"

#include <inttypes.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

#if !defined(MAP_ANONYMOUS) && defined(MAP_ANON)
#define MAP_ANONYMOUS MAP_ANON
#endif

#define NL_BUFFER_MAX_BYTES (1024u * 1024u)
#define NL_BUFFER_SLOT_COUNT 64u
#define NL_BUFFER_SLOT_BITS 16u
#define NL_BUFFER_GENERATION_BITS 16u
#define NL_BUFFER_GENERATION_MAX UINT16_MAX

typedef struct {
    void *cached;
    size_t cached_capacity;
    uint64_t physical_allocations;
    uint64_t physical_reuses;
    uint64_t logical_releases;
    pthread_mutex_t mutex;
} recycling_allocator;

typedef struct {
    uint8_t *data;
    size_t length;
    uint16_t generation;
    uint32_t active_deliveries;
    bool active;
    bool release_pending;
    bool retired;
} buffer_slot;

struct nl_buffer_host {
    uint32_t host_id;
    buffer_slot slots[NL_BUFFER_SLOT_COUNT];
    pthread_mutex_t mutex;
    recycling_allocator *allocator;
};

static atomic_uint_fast64_t next_host_id = 1;
static recycling_allocator test_allocator;

static int check(bool condition, const char *message)
{
    if (condition)
        return 0;
    fprintf(stderr, "FAIL: %s\n", message);
    return 1;
}

static void *recycling_allocate(recycling_allocator *allocator, size_t size)
{
    pthread_mutex_lock(&allocator->mutex);
    if (allocator->cached != NULL && allocator->cached_capacity >= size) {
        void *result = allocator->cached;
        allocator->cached = NULL;
        allocator->cached_capacity = 0;
        allocator->physical_reuses++;
        pthread_mutex_unlock(&allocator->mutex);
        return result;
    }

    if (allocator->cached != NULL) {
        free(allocator->cached);
        allocator->cached = NULL;
        allocator->cached_capacity = 0;
    }

    void *result = malloc(size == 0 ? 1 : size);
    if (result != NULL)
        allocator->physical_allocations++;
    pthread_mutex_unlock(&allocator->mutex);
    return result;
}

static void recycling_release(recycling_allocator *allocator, void *pointer, size_t capacity)
{
    if (pointer == NULL)
        return;

    pthread_mutex_lock(&allocator->mutex);
    allocator->logical_releases++;
    if (allocator->cached == NULL) {
        allocator->cached = pointer;
        allocator->cached_capacity = capacity;
    } else {
        free(pointer);
    }
    pthread_mutex_unlock(&allocator->mutex);
}

static void recycling_allocator_dispose(recycling_allocator *allocator)
{
    pthread_mutex_lock(&allocator->mutex);
    free(allocator->cached);
    allocator->cached = NULL;
    allocator->cached_capacity = 0;
    pthread_mutex_unlock(&allocator->mutex);
    pthread_mutex_destroy(&allocator->mutex);
}

static nl_buffer_host *host_create(recycling_allocator *allocator)
{
    uint64_t candidate = atomic_fetch_add_explicit(
        &next_host_id, 1, memory_order_relaxed);
    if (candidate == 0 || candidate > UINT32_MAX)
        return NULL;

    nl_buffer_host *host = calloc(1, sizeof(*host));
    if (host == NULL)
        return NULL;
    if (pthread_mutex_init(&host->mutex, NULL) != 0) {
        free(host);
        return NULL;
    }
    host->host_id = (uint32_t)candidate;
    host->allocator = allocator;
    for (size_t i = 0; i < NL_BUFFER_SLOT_COUNT; i++)
        host->slots[i].generation = 1;
    return host;
}

static uint64_t token_for_slot(const nl_buffer_host *host, size_t index)
{
    const buffer_slot *slot = &host->slots[index];
    uint64_t slot_id = (uint64_t)index + 1;
    return ((uint64_t)host->host_id << 32) |
        ((uint64_t)slot->generation << NL_BUFFER_SLOT_BITS) |
        slot_id;
}

static nl_buffer_status validate_output(nl_owned_buffer_v1 *output)
{
    if (output == NULL)
        return NL_BUFFER_INVALID_ARGUMENT;

    uint32_t declared_size = 0;
    memcpy(&declared_size, output, sizeof(declared_size));
    if (declared_size < sizeof(uint32_t))
        return NL_BUFFER_STRUCT_TOO_SMALL;
    if (declared_size < offsetof(nl_owned_buffer_v1, token))
        return NL_BUFFER_STRUCT_TOO_SMALL;

    uint32_t abi_version = 0;
    memcpy(&abi_version,
        (const uint8_t *)output + offsetof(nl_owned_buffer_v1, abi_version),
        sizeof(abi_version));
    if (abi_version != NL_BUFFER_ABI_VERSION)
        return NL_BUFFER_VERSION_MISMATCH;
    if (declared_size < sizeof(*output))
        return NL_BUFFER_STRUCT_TOO_SMALL;

    output->token = 0;
    output->data = NULL;
    output->length = 0;
    return NL_BUFFER_OK;
}

static nl_buffer_status lookup_slot_locked(
    nl_buffer_host *host,
    uint64_t token,
    buffer_slot **out_slot)
{
    if (token == 0)
        return NL_BUFFER_INVALID_TOKEN;

    uint32_t token_host = (uint32_t)(token >> 32);
    if (token_host != host->host_id)
        return NL_BUFFER_WRONG_HOST;

    uint16_t generation = (uint16_t)(token >> NL_BUFFER_SLOT_BITS);
    uint16_t encoded_slot = (uint16_t)token;
    if (generation == 0 || encoded_slot == 0 || encoded_slot > NL_BUFFER_SLOT_COUNT)
        return NL_BUFFER_INVALID_TOKEN;

    buffer_slot *slot = &host->slots[encoded_slot - 1];
    if (!slot->active || slot->generation != generation)
        return NL_BUFFER_STALE_TOKEN;

    *out_slot = slot;
    return NL_BUFFER_OK;
}

static void finalize_slot_locked(
    nl_buffer_host *host,
    buffer_slot *slot)
{
    recycling_release(host->allocator, slot->data, slot->length);
    slot->data = NULL;
    slot->length = 0;
    slot->active = false;
    slot->release_pending = false;
    slot->active_deliveries = 0;
    if (slot->generation == NL_BUFFER_GENERATION_MAX) {
        slot->retired = true;
    } else {
        slot->generation++;
    }
}

static nl_buffer_status nl_buffer_allocate_impl(
    nl_buffer_host *host,
    const uint8_t *data,
    uint64_t length,
    nl_owned_buffer_v1 *out_buffer)
{
    nl_buffer_status output_status = validate_output(out_buffer);
    if (output_status != NL_BUFFER_OK)
        return output_status;
    if (host == NULL || (length != 0 && data == NULL))
        return NL_BUFFER_INVALID_ARGUMENT;
    if (length > NL_BUFFER_MAX_BYTES || length > SIZE_MAX)
        return NL_BUFFER_TOO_LARGE;
    if (length == 0)
        return NL_BUFFER_OK;

    pthread_mutex_lock(&host->mutex);
    size_t index = NL_BUFFER_SLOT_COUNT;
    for (size_t i = 0; i < NL_BUFFER_SLOT_COUNT; i++) {
        if (!host->slots[i].active && !host->slots[i].retired) {
            index = i;
            break;
        }
    }
    if (index == NL_BUFFER_SLOT_COUNT) {
        pthread_mutex_unlock(&host->mutex);
        return NL_BUFFER_REGISTRY_FULL;
    }

    uint8_t *copy = recycling_allocate(host->allocator, (size_t)length);
    if (copy == NULL) {
        pthread_mutex_unlock(&host->mutex);
        return NL_BUFFER_OUT_OF_MEMORY;
    }
    memcpy(copy, data, (size_t)length);

    buffer_slot *slot = &host->slots[index];
    slot->data = copy;
    slot->length = (size_t)length;
    slot->active_deliveries = 0;
    slot->release_pending = false;
    slot->active = true;
    out_buffer->token = token_for_slot(host, index);
    out_buffer->data = copy;
    out_buffer->length = length;
    pthread_mutex_unlock(&host->mutex);
    return NL_BUFFER_OK;
}

nl_buffer_status nl_buffer_allocate(
    nl_buffer_host *host,
    const uint8_t *data,
    uint64_t length,
    nl_owned_buffer_v1 *out_buffer)
{
    return nl_buffer_allocate_impl(host, data, length, out_buffer);
}

nl_buffer_status nl_buffer_release(nl_buffer_host *host, uint64_t token)
{
    if (host == NULL)
        return NL_BUFFER_INVALID_ARGUMENT;
    pthread_mutex_lock(&host->mutex);
    buffer_slot *slot = NULL;
    nl_buffer_status result = lookup_slot_locked(host, token, &slot);
    if (result != NL_BUFFER_OK) {
        pthread_mutex_unlock(&host->mutex);
        return result;
    }
    if (slot->release_pending) {
        pthread_mutex_unlock(&host->mutex);
        return NL_BUFFER_ALREADY_RELEASING;
    }

    if (slot->active_deliveries != 0) {
        slot->release_pending = true;
        pthread_mutex_unlock(&host->mutex);
        return NL_BUFFER_RELEASE_DEFERRED;
    }

    finalize_slot_locked(host, slot);
    pthread_mutex_unlock(&host->mutex);
    return NL_BUFFER_OK;
}

nl_buffer_status nl_buffer_deliver_completion(
    nl_buffer_host *host,
    uint64_t token,
    nl_completion_callback_v1 callback,
    void *context)
{
    if (host == NULL || callback == NULL)
        return NL_BUFFER_INVALID_ARGUMENT;

    pthread_mutex_lock(&host->mutex);
    buffer_slot *slot = NULL;
    nl_buffer_status result = lookup_slot_locked(host, token, &slot);
    if (result != NL_BUFFER_OK) {
        pthread_mutex_unlock(&host->mutex);
        return result;
    }
    if (slot->release_pending) {
        pthread_mutex_unlock(&host->mutex);
        return NL_BUFFER_STALE_TOKEN;
    }

    slot->active_deliveries++;
    nl_borrowed_bytes_v1 borrowed = {
        .struct_size = sizeof(nl_borrowed_bytes_v1),
        .abi_version = NL_BUFFER_ABI_VERSION,
        .data = slot->data,
        .length = slot->length,
    };
    pthread_mutex_unlock(&host->mutex);

    callback(context, &borrowed);

    pthread_mutex_lock(&host->mutex);
    buffer_slot *current = NULL;
    result = lookup_slot_locked(host, token, &current);
    if (result == NL_BUFFER_OK && current->active_deliveries != 0) {
        current->active_deliveries--;
        if (current->release_pending && current->active_deliveries == 0)
            finalize_slot_locked(host, current);
    } else if (result == NL_BUFFER_OK) {
        result = NL_BUFFER_INVALID_TOKEN;
    }
    pthread_mutex_unlock(&host->mutex);
    return result;
}

static nl_buffer_status legacy_pointer_only_release(
    nl_buffer_host *host,
    const void *stale_pointer)
{
    uint64_t matching_token = 0;
    pthread_mutex_lock(&host->mutex);
    for (size_t i = 0; i < NL_BUFFER_SLOT_COUNT; i++) {
        buffer_slot *slot = &host->slots[i];
        if (slot->active && slot->data == stale_pointer) {
            matching_token = token_for_slot(host, i);
            break;
        }
    }
    pthread_mutex_unlock(&host->mutex);
    if (matching_token == 0)
        return NL_BUFFER_STALE_TOKEN;
    return nl_buffer_release(host, matching_token);
}

static nl_buffer_status host_destroy(nl_buffer_host *host)
{
    pthread_mutex_lock(&host->mutex);
    for (size_t i = 0; i < NL_BUFFER_SLOT_COUNT; i++) {
        if (host->slots[i].active) {
            pthread_mutex_unlock(&host->mutex);
            return NL_BUFFER_BUSY;
        }
    }
    pthread_mutex_unlock(&host->mutex);
    pthread_mutex_destroy(&host->mutex);
    free(host);
    return NL_BUFFER_OK;
}

static int test_guarded_short_structs(nl_buffer_host *host)
{
    long system_page_size = sysconf(_SC_PAGESIZE);
    if (system_page_size <= 0)
        return check(false, "query system page size");
    size_t page_size = (size_t)system_page_size;
    uint8_t *pages = mmap(NULL, page_size * 2, PROT_READ | PROT_WRITE,
        MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (pages == MAP_FAILED)
        return check(false, "allocate guard-page mapping");
    if (mprotect(pages + page_size, page_size, PROT_NONE) != 0) {
        munmap(pages, page_size * 2);
        return check(false, "protect trailing guard page");
    }

    const uint8_t bytes[] = {1, 2, 3};
    uint32_t size4 = sizeof(uint32_t);
    nl_owned_buffer_v1 *short4 =
        (nl_owned_buffer_v1 *)(pages + page_size - sizeof(uint64_t));
    memcpy(short4, &size4, sizeof(size4));
    nl_buffer_status first = nl_buffer_allocate(host, bytes, sizeof(bytes), short4);

    struct {
        uint32_t struct_size;
        uint32_t abi_version;
    } header8 = {sizeof(header8), NL_BUFFER_ABI_VERSION};
    nl_owned_buffer_v1 *short8 =
        (nl_owned_buffer_v1 *)(pages + page_size - sizeof(header8));
    memcpy(short8, &header8, sizeof(header8));
    nl_buffer_status second = nl_buffer_allocate(host, bytes, sizeof(bytes), short8);

    munmap(pages, page_size * 2);
    return check(first == NL_BUFFER_STRUCT_TOO_SMALL &&
        second == NL_BUFFER_STRUCT_TOO_SMALL,
        "short output structs were accessed beyond their guarded allocation");
}

static void init_output(nl_owned_buffer_v1 *output)
{
    memset(output, 0, sizeof(*output));
    output->struct_size = sizeof(*output);
    output->abi_version = NL_BUFFER_ABI_VERSION;
}

static int test_zero_and_error_outputs(nl_buffer_host *host)
{
    nl_owned_buffer_v1 empty;
    init_output(&empty);
    if (check(nl_buffer_allocate(host, NULL, 0, &empty) == NL_BUFFER_OK &&
        empty.token == 0 && empty.data == NULL && empty.length == 0,
        "zero-length output must be canonical and allocation-free"))
        return 1;
    if (check(nl_buffer_release(host, empty.token) == NL_BUFFER_INVALID_TOKEN,
        "zero-length output must not create a releasable allocation"))
        return 1;

    static const uint8_t error_message[] = "managed failure: invalid response";
    nl_owned_buffer_v1 error;
    init_output(&error);
    if (check(nl_buffer_allocate(host, error_message, sizeof(error_message) - 1, &error) == NL_BUFFER_OK &&
        error.token != 0 && error.length == sizeof(error_message) - 1 &&
        memcmp(error.data, error_message, sizeof(error_message) - 1) == 0,
        "error payload must use the same owned-buffer allocator"))
        return 1;
    return check(nl_buffer_release(host, error.token) == NL_BUFFER_OK,
        "release owned error payload");
}

static int test_output_version_mismatch(nl_buffer_host *host)
{
    static const uint8_t byte = 1;
    nl_owned_buffer_v1 output;
    init_output(&output);
    output.abi_version++;
    nl_buffer_status result = nl_buffer_allocate(host, &byte, sizeof(byte), &output);
    return check(result == NL_BUFFER_VERSION_MISMATCH &&
        output.token == 0 && output.data == NULL && output.length == 0,
        "output ABI version mismatch must be rejected before allocation");
}

static int test_oversized_output(nl_buffer_host *host)
{
    uint8_t *maximum_payload = malloc(NL_BUFFER_MAX_BYTES);
    if (check(maximum_payload != NULL, "allocate input at maximum supported length"))
        return 1;
    memset(maximum_payload, 0xa5, NL_BUFFER_MAX_BYTES);

    nl_owned_buffer_v1 output;
    init_output(&output);
    nl_buffer_status boundary_result = nl_buffer_allocate(
        host, maximum_payload, NL_BUFFER_MAX_BYTES, &output);
    bool boundary_valid = boundary_result == NL_BUFFER_OK &&
        output.length == NL_BUFFER_MAX_BYTES &&
        output.data[0] == 0xa5 &&
        output.data[NL_BUFFER_MAX_BYTES - 1] == 0xa5;
    nl_buffer_status boundary_release = boundary_result == NL_BUFFER_OK
        ? nl_buffer_release(host, output.token)
        : NL_BUFFER_INVALID_ARGUMENT;
    free(maximum_payload);
    if (check(boundary_valid && boundary_release == NL_BUFFER_OK,
        "maximum supported payload length must be accepted"))
        return 1;

    static const uint8_t one_byte = 0x7f;
    init_output(&output);
    nl_buffer_status result = nl_buffer_allocate(
        host, &one_byte, (uint64_t)NL_BUFFER_MAX_BYTES + 1, &output);
    nl_buffer_status huge_result = nl_buffer_allocate(
        host, &one_byte, UINT64_MAX, &output);
    return check(result == NL_BUFFER_TOO_LARGE && huge_result == NL_BUFFER_TOO_LARGE &&
        output.token == 0 && output.data == NULL && output.length == 0,
        "oversized and wrapping lengths must fail before reading or allocating payload bytes");
}

static int test_generation_and_address_reuse(
    nl_buffer_host *host,
    recycling_allocator *allocator)
{
    static const uint8_t first_bytes[] = "recycled allocation";
    nl_owned_buffer_v1 first;
    init_output(&first);
    if (check(nl_buffer_allocate(host, first_bytes, sizeof(first_bytes), &first) == NL_BUFFER_OK,
        "allocate first generation"))
        return 1;
    const uint8_t *old_pointer = first.data;
    uint64_t old_token = first.token;
    if (check(nl_buffer_release(host, old_token) == NL_BUFFER_OK &&
        nl_buffer_release(host, old_token) == NL_BUFFER_STALE_TOKEN,
        "duplicate release must be rejected"))
        return 1;

    nl_owned_buffer_v1 second;
    init_output(&second);
    if (check(nl_buffer_allocate(host, first_bytes, sizeof(first_bytes), &second) == NL_BUFFER_OK &&
        second.data == old_pointer && second.token != old_token &&
        allocator->physical_reuses != 0,
        "test allocator must recycle the address with a new generation token"))
        return 1;
    if (check(nl_buffer_release(host, old_token) == NL_BUFFER_STALE_TOKEN,
        "stale generation must not release the recycled allocation"))
        return 1;
    if (check(nl_buffer_release(host, second.token) == NL_BUFFER_OK,
        "current generation remains releasable after stale-token probe"))
        return 1;

    nl_owned_buffer_v1 legacy_first;
    init_output(&legacy_first);
    if (check(nl_buffer_allocate(host, first_bytes, sizeof(first_bytes), &legacy_first) == NL_BUFFER_OK,
        "allocate pointer-only comparison generation"))
        return 1;
    const uint8_t *legacy_stale_pointer = legacy_first.data;
    if (check(nl_buffer_release(host, legacy_first.token) == NL_BUFFER_OK,
        "retire pointer-only comparison allocation"))
        return 1;

    nl_owned_buffer_v1 legacy_second;
    init_output(&legacy_second);
    if (check(nl_buffer_allocate(host, first_bytes, sizeof(first_bytes), &legacy_second) == NL_BUFFER_OK &&
        legacy_second.data == legacy_stale_pointer,
        "pointer-only comparison must exercise exact address reuse"))
        return 1;
    if (check(legacy_pointer_only_release(host, legacy_stale_pointer) == NL_BUFFER_OK &&
        nl_buffer_release(host, legacy_second.token) == NL_BUFFER_STALE_TOKEN,
        "pointer-only stale free model should misidentify and release the new allocation"))
        return 1;
    return 0;
}

static int test_cross_host_recycled_pointer(
    nl_buffer_host *first_host,
    nl_buffer_host *second_host)
{
    static const uint8_t bytes[] = "cross-host";
    nl_owned_buffer_v1 first;
    init_output(&first);
    if (check(nl_buffer_allocate(first_host, bytes, sizeof(bytes), &first) == NL_BUFFER_OK,
        "allocate first-host output"))
        return 1;
    uint64_t stale_first_token = first.token;
    const uint8_t *recycled_address = first.data;
    if (check(nl_buffer_release(first_host, stale_first_token) == NL_BUFFER_OK,
        "release first-host output"))
        return 1;

    nl_owned_buffer_v1 second;
    init_output(&second);
    if (check(nl_buffer_allocate(second_host, bytes, sizeof(bytes), &second) == NL_BUFFER_OK &&
        second.data == recycled_address,
        "second host should receive the recycled address"))
        return 1;
    if (check(nl_buffer_release(second_host, stale_first_token) == NL_BUFFER_WRONG_HOST,
        "cross-host token must be rejected despite address reuse"))
        return 1;
    return check(nl_buffer_release(second_host, second.token) == NL_BUFFER_OK,
        "cross-host rejection must leave the second host's output valid");
}

typedef struct {
    nl_buffer_host *host;
    uint64_t token;
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    bool entered;
    bool continue_callback;
    bool data_intact;
    nl_buffer_status borrowed_release_result;
} completion_gate;

static void gated_completion(void *context, const nl_borrowed_bytes_v1 *view)
{
    completion_gate *gate = context;
    static const uint8_t expected[] = "completion payload stays borrowed";
    bool header_valid = view->struct_size == sizeof(*view) &&
        view->abi_version == NL_BUFFER_ABI_VERSION &&
        view->length == sizeof(expected) - 1;
    gate->borrowed_release_result = nl_buffer_release(gate->host, 0);

    pthread_mutex_lock(&gate->mutex);
    gate->entered = true;
    pthread_cond_broadcast(&gate->condition);
    while (!gate->continue_callback)
        pthread_cond_wait(&gate->condition, &gate->mutex);
    gate->data_intact = header_valid &&
        memcmp(view->data, expected, sizeof(expected) - 1) == 0;
    pthread_mutex_unlock(&gate->mutex);
}

typedef struct {
    nl_buffer_host *host;
    uint64_t token;
    nl_buffer_status result;
    completion_gate *gate;
} completion_thread_args;

static void *complete_on_thread(void *argument)
{
    completion_thread_args *args = argument;
    args->result = nl_buffer_deliver_completion(
        args->host, args->token, gated_completion, args->gate);
    return NULL;
}

static int test_concurrent_release_vs_completion(nl_buffer_host *host)
{
    static const uint8_t payload[] = "completion payload stays borrowed";
    nl_owned_buffer_v1 output;
    init_output(&output);
    if (check(nl_buffer_allocate(host, payload, sizeof(payload) - 1, &output) == NL_BUFFER_OK,
        "allocate output for concurrent completion test"))
        return 1;

    completion_gate gate = {
        .host = host,
        .token = output.token,
        .mutex = PTHREAD_MUTEX_INITIALIZER,
        .condition = PTHREAD_COND_INITIALIZER,
        .borrowed_release_result = NL_BUFFER_OK,
    };
    completion_thread_args args = {host, output.token, NL_BUFFER_INVALID_ARGUMENT, &gate};
    pthread_t thread;
    if (pthread_create(&thread, NULL, complete_on_thread, &args) != 0)
        return check(false, "start completion callback thread");

    pthread_mutex_lock(&gate.mutex);
    while (!gate.entered)
        pthread_cond_wait(&gate.condition, &gate.mutex);
    pthread_mutex_unlock(&gate.mutex);

    nl_buffer_status release_result = nl_buffer_release(host, output.token);
    nl_buffer_status duplicate_result = nl_buffer_release(host, output.token);

    pthread_mutex_lock(&gate.mutex);
    gate.continue_callback = true;
    pthread_cond_broadcast(&gate.condition);
    pthread_mutex_unlock(&gate.mutex);

    pthread_join(thread, NULL);
    pthread_cond_destroy(&gate.condition);
    pthread_mutex_destroy(&gate.mutex);

    return check(release_result == NL_BUFFER_RELEASE_DEFERRED &&
        duplicate_result == NL_BUFFER_ALREADY_RELEASING &&
        args.result == NL_BUFFER_OK && gate.data_intact &&
        gate.borrowed_release_result == NL_BUFFER_INVALID_TOKEN &&
        nl_buffer_release(host, output.token) == NL_BUFFER_STALE_TOKEN,
        "release/completion race must defer reclamation through borrowed callback and reject repeat release");
}

int main(void)
{
    if (pthread_mutex_init(&test_allocator.mutex, NULL) != 0)
        return check(false, "initialize test allocator");

    nl_buffer_host *first_host = host_create(&test_allocator);
    nl_buffer_host *second_host = host_create(&test_allocator);
    if (first_host == NULL || second_host == NULL) {
        fprintf(stderr, "FAIL: create test hosts\n");
        if (first_host != NULL)
            host_destroy(first_host);
        if (second_host != NULL)
            host_destroy(second_host);
        recycling_allocator_dispose(&test_allocator);
        return 1;
    }

    int failed =
        test_guarded_short_structs(first_host) ||
        test_zero_and_error_outputs(first_host) ||
        test_output_version_mismatch(first_host) ||
        test_oversized_output(first_host) ||
        test_generation_and_address_reuse(first_host, &test_allocator) ||
        test_cross_host_recycled_pointer(first_host, second_host) ||
        test_concurrent_release_vs_completion(first_host);

    if (host_destroy(first_host) != NL_BUFFER_OK ||
        host_destroy(second_host) != NL_BUFFER_OK) {
        fprintf(stderr, "FAIL: host still owns live outputs after tests\n");
        failed = 1;
    }

    if (test_allocator.physical_reuses == 0 ||
        test_allocator.logical_releases == 0) {
        fprintf(stderr, "FAIL: allocation accounting did not observe reuse/release\n");
        failed = 1;
    }

    if (!failed) {
        printf("PASS output ownership: generation/stale/duplicate/cross-host, guarded structs, "
            "zero/error/oversize, release-completion race, borrowed callback\n");
        printf("Allocator evidence: %" PRIu64 " physical allocations, %" PRIu64
            " recycled-address reuses, %" PRIu64 " logical releases\n",
            test_allocator.physical_allocations,
            test_allocator.physical_reuses,
            test_allocator.logical_releases);
        printf("ABI layout: owned=%zu bytes, borrowed=%zu bytes, host registry=%zu bytes "
            "(%u fixed slots of %zu bytes)\n",
            sizeof(nl_owned_buffer_v1),
            sizeof(nl_borrowed_bytes_v1),
            sizeof(nl_buffer_host),
            NL_BUFFER_SLOT_COUNT,
            sizeof(buffer_slot));
    }

    recycling_allocator_dispose(&test_allocator);
    return failed;
}
