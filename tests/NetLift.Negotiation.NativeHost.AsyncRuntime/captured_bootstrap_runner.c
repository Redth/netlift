#define _DARWIN_C_SOURCE

#include "coreclr_bootstrap.h"

#include <fcntl.h>
#include <inttypes.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

enum {
    MAX_COMPONENT_BYTES = 64 * 1024 * 1024,
    MAX_COMPONENTS = 32,
    HASH_BYTES = 32
};

static void
require_true(bool condition, const char *message)
{
    if (!condition) {
        fprintf(stderr, "FAIL %s\n", message);
        exit(1);
    }
}

static bool
decode_hash(const char *text, uint8_t hash[HASH_BYTES])
{
    if (text == NULL || strlen(text) != HASH_BYTES * 2)
        return false;
    for (size_t i = 0; i < HASH_BYTES; i++) {
        unsigned value = 0;
        if (sscanf(text + i * 2, "%2x", &value) != 1)
            return false;
        hash[i] = (uint8_t)value;
    }
    return true;
}

static uint8_t *
read_component(const char *path, size_t *length)
{
    *length = 0;
    int descriptor = open(path, O_RDONLY | O_CLOEXEC);
    if (descriptor < 0) {
        perror("open captured component");
        return NULL;
    }

    struct stat metadata;
    if (fstat(descriptor, &metadata) != 0 ||
        metadata.st_size <= 0 ||
        (uint64_t)metadata.st_size > MAX_COMPONENT_BYTES) {
        fprintf(stderr, "invalid captured component size: %s\n", path);
        close(descriptor);
        return NULL;
    }

    size_t size = (size_t)metadata.st_size;
    uint8_t *bytes = malloc(size);
    if (bytes == NULL) {
        close(descriptor);
        return NULL;
    }
    size_t offset = 0;
    while (offset < size) {
        ssize_t count = read(descriptor, bytes + offset, size - offset);
        if (count <= 0) {
            perror("read captured component");
            free(bytes);
            close(descriptor);
            return NULL;
        }
        offset += (size_t)count;
    }
    close(descriptor);
    *length = size;
    return bytes;
}

static bool
managed_api_is_complete(const nlv5_managed_api *api)
{
    return api != NULL &&
        api->get_manifest != NULL &&
        api->create_endpoint != NULL &&
        api->start != NULL &&
        api->get_peer_schema != NULL &&
        api->get_managed_result_schema != NULL &&
        api->close_endpoint != NULL &&
        api->free_buffer != NULL &&
        api->register_result_context != NULL &&
        api->decode_result != NULL &&
        api->invalidate_result_context != NULL &&
        api->get_outstanding_managed_work != NULL &&
        api->get_outstanding_managed_result_leases != NULL &&
        api->configure_native_callbacks != NULL &&
        api->register_generated_host != NULL &&
        api->unregister_generated_host != NULL &&
        api->start_request != NULL &&
        api->cancel_operation != NULL &&
        api->reply_confirmation != NULL &&
        api->cancel_confirmation != NULL;
}

static bool
contains_bytes(const uint8_t *buffer, size_t buffer_length, const char *text)
{
    size_t text_length = strlen(text);
    if (text_length > buffer_length)
        return false;
    for (size_t i = 0; i <= buffer_length - text_length; i++) {
        if (memcmp(buffer + i, text, text_length) == 0)
            return true;
    }
    return false;
}

static void
verify_executed_manifest(const char *expected_hash)
{
    const nlv5_managed_api *api = nlv5_coreclr_bootstrap_local_api();
    require_true(managed_api_is_complete(api), "captured managed API is complete");

    nlv5_v5_owned_bytes manifest = {
        .struct_size = sizeof(manifest),
    };
    nlv5_v5_status status = {
        .struct_size = sizeof(status),
    };
    uint32_t result = api->get_manifest(&manifest, &status);
    require_true(result == NLV5_STATUS_OK &&
                 status.code == NLV5_STATUS_OK &&
                 status.message == NULL &&
                 manifest.data != NULL &&
                 manifest.length != 0,
                 "captured NativeApi.GetManifest executes successfully");
    require_true(
        contains_bytes(manifest.data, manifest.length, expected_hash),
        "executed manifest contains the expected OLD/NEW schema hash");

    nlv5_v5_status free_status = {
        .struct_size = sizeof(free_status),
    };
    result = api->free_buffer(manifest.data, &free_status);
    require_true(result == NLV5_STATUS_OK &&
                 free_status.code == NLV5_STATUS_OK &&
                 free_status.message == NULL,
                 "captured NativeApi.FreeBuffer releases its output");
    puts("PASS captured NativeApi.GetManifest and complete NativeApi/AsyncRuntimeApi exports");
}

static bool
wait_for_path_mutation(const char *ready_path, const char *continue_path)
{
    int descriptor = open(ready_path, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (descriptor < 0) {
        perror("create capture-ready marker");
        return false;
    }
    close(descriptor);

    for (unsigned i = 0; i < 3000; i++) {
        if (access(continue_path, F_OK) == 0)
            return true;
        usleep(10000);
    }
    fprintf(stderr, "timed out waiting for staged path replacement\n");
    return false;
}

int
main(int argc, char **argv)
{
    if (argc < 10) {
        fprintf(stderr,
                "usage: %s <positive|missing-dependency|malformed-root> "
                "<dotnet-root> <runtime-config> <root-assembly> <namespace> "
                "<expected-schema-hash> <ready> <continue> <component-count> "
                "<assembly-name> <path> <expected-sha256> ...\n",
                argv[0]);
        return 2;
    }

    const char *mode = argv[1];
    const char *dotnet_root = argv[2];
    const char *runtime_config = argv[3];
    const char *root_assembly = argv[4];
    const char *managed_namespace = argv[5];
    const char *expected_schema_hash = argv[6];
    const char *ready_path = argv[7];
    const char *continue_path = argv[8];
    char *count_end = NULL;
    unsigned long parsed_count = strtoul(argv[9], &count_end, 10);
    require_true(count_end != argv[9] && *count_end == '\0' &&
                 parsed_count > 0 && parsed_count <= MAX_COMPONENTS,
                 "component count is valid");
    size_t component_count = (size_t)parsed_count;
    require_true(argc == 10 + (int)(component_count * 3),
                 "component argument count is exact");
    require_true(strcmp(mode, "positive") == 0 ||
                 strcmp(mode, "missing-dependency") == 0 ||
                 strcmp(mode, "malformed-root") == 0,
                 "mode is supported");

    nlv5_coreclr_managed_component_bytes_v1 *components =
        calloc(component_count, sizeof(*components));
    require_true(components != NULL, "allocate captured component descriptors");
    for (size_t i = 0; i < component_count; i++) {
        const char *name = argv[10 + i * 3];
        const char *path = argv[11 + i * 3];
        const char *hash = argv[12 + i * 3];
        components[i].assembly_name = name;
        components[i].bytes = read_component(path, &components[i].byte_length);
        require_true(components[i].bytes != NULL, "read isolated managed component");
        require_true(decode_hash(hash, components[i].expected_sha256),
                     "decode expected component SHA-256");
        printf("CAPTURE_INPUT name=%s path=%s bytes=%zu\n",
               name, path, components[i].byte_length);
    }

    size_t expected_component_count = component_count +
        (strcmp(mode, "missing-dependency") == 0 ? 1 : 0);
    const char **expected_component_names =
        calloc(expected_component_count, sizeof(*expected_component_names));
    require_true(expected_component_names != NULL, "allocate expected component order");
    size_t expected_offset = 0;
    if (strcmp(mode, "missing-dependency") == 0)
        expected_component_names[expected_offset++] = "NetLift.Contracts";
    for (size_t i = 0; i < component_count; i++)
        expected_component_names[expected_offset++] = components[i].assembly_name;
    require_true(expected_offset == expected_component_count,
                 "construct exact expected component order");
    const char **provided_component_names =
        calloc(component_count, sizeof(*provided_component_names));
    require_true(provided_component_names != NULL, "allocate provided component order");
    for (size_t i = 0; i < component_count; i++)
        provided_component_names[i] = components[i].assembly_name;

    nlv5_coreclr_managed_component_bytes_v1 *wrong_hash_components =
        malloc(component_count * sizeof(*wrong_hash_components));
    require_true(wrong_hash_components != NULL, "allocate wrong-hash probe");
    memcpy(
        wrong_hash_components,
        components,
        component_count * sizeof(*wrong_hash_components));
    wrong_hash_components[0].expected_sha256[0] ^= 0x80;
    uint32_t result = nlv5_coreclr_bootstrap_initialize_from_captured_bytes(
        dotnet_root,
        runtime_config,
        root_assembly,
        managed_namespace,
        provided_component_names,
        component_count,
        wrong_hash_components,
        component_count);
    require_true(result == NLV5_STATUS_SCHEMA_MISMATCH &&
                 !nlv5_coreclr_bootstrap_is_ready() &&
                 nlv5_coreclr_bootstrap_local_api() == NULL,
                 "wrong expected hash is rejected before runtime initialization");
    puts("NEGATIVE wrong_hash=rejected_before_runtime state=cold");
    free(wrong_hash_components);

    result = nlv5_coreclr_bootstrap_initialize_from_captured_bytes(
        dotnet_root,
        runtime_config,
        root_assembly,
        managed_namespace,
        provided_component_names,
        component_count,
        components,
        component_count - 1);
    require_true(result == NLV5_STATUS_INVALID_ARGUMENT &&
                 !nlv5_coreclr_bootstrap_is_ready() &&
                 nlv5_coreclr_bootstrap_local_api() == NULL,
                 "missing root component is rejected before runtime initialization");
    puts("NEGATIVE missing_root=rejected_before_runtime state=cold");

    if (!wait_for_path_mutation(ready_path, continue_path))
        return 3;
    puts("PATH_MUTATION barrier passed; only captured in-memory component bytes remain");

    result = nlv5_coreclr_bootstrap_initialize_from_captured_bytes(
        dotnet_root,
        runtime_config,
        root_assembly,
        managed_namespace,
        expected_component_names,
        expected_component_count,
        components,
        component_count);
    if (strcmp(mode, "positive") == 0) {
        require_true(result == NLV5_STATUS_OK &&
                     nlv5_coreclr_bootstrap_is_ready(),
                     "captured-byte OLD/NEW bootstrap reaches READY");
        verify_executed_manifest(expected_schema_hash);

        result = nlv5_coreclr_bootstrap_initialize(
            dotnet_root,
            runtime_config,
            "/invalidated/path/that/must/not/be/loaded.dll",
            root_assembly,
            managed_namespace);
        require_true(result == NLV5_STATUS_UNAVAILABLE &&
                     nlv5_coreclr_bootstrap_is_ready(),
                     "one-shot READY bootstrap rejects path reinitialization");
        puts("PASS captured-byte mode never falls back to path bootstrap");
    } else if (strcmp(mode, "missing-dependency") == 0) {
        require_true(result == NLV5_STATUS_INVALID_ARGUMENT &&
                     !nlv5_coreclr_bootstrap_is_ready() &&
                     nlv5_coreclr_bootstrap_local_api() == NULL,
                     "required direct dependency omission is rejected before runtime initialization");
        puts("NEGATIVE missing_dependency=component_selection_rejected state=cold");
    } else {
        require_true(result != NLV5_STATUS_OK &&
                     !nlv5_coreclr_bootstrap_is_ready() &&
                     nlv5_coreclr_bootstrap_local_api() == NULL,
                     "missing or malformed captured component quarantines bootstrap");
        printf("NEGATIVE mode=%s status=%" PRIu32 " ready=0 api=null\n",
               mode, result);

        result = nlv5_coreclr_bootstrap_initialize(
            dotnet_root,
            runtime_config,
            "/invalidated/path/that/must/not/be/loaded.dll",
            root_assembly,
            managed_namespace);
        require_true(result == NLV5_STATUS_UNAVAILABLE &&
                     !nlv5_coreclr_bootstrap_is_ready(),
                     "quarantined byte bootstrap rejects path fallback");
        puts("PASS failed captured-byte mode remains quarantined without path fallback");
    }

    free(expected_component_names);
    free(provided_component_names);
    for (size_t i = 0; i < component_count; i++)
        free((void *)components[i].bytes);
    free(components);
    return 0;
}
