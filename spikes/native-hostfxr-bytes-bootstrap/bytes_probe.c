#include <CommonCrypto/CommonDigest.h>
#include <coreclr_delegates.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <hostfxr.h>
#include <inttypes.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

typedef struct {
    uint8_t *data;
    size_t length;
} image_bytes;

typedef struct {
    void *library;
    load_assembly_and_get_function_pointer_fn load_path;
    load_assembly_bytes_fn load_bytes;
    get_function_pointer_fn get_function_pointer;
} hostfxr_delegates;

static bool
parse_sha256(const char *text, uint8_t digest[CC_SHA256_DIGEST_LENGTH])
{
    if (strlen(text) != CC_SHA256_DIGEST_LENGTH * 2)
        return false;
    for (size_t i = 0; i < CC_SHA256_DIGEST_LENGTH; i++) {
        unsigned value = 0;
        if (sscanf(text + i * 2, "%2x", &value) != 1)
            return false;
        digest[i] = (uint8_t)value;
    }
    return true;
}

static bool
hash_bytes(const uint8_t *bytes, size_t length, uint8_t digest[CC_SHA256_DIGEST_LENGTH])
{
    if (length > UINT32_MAX)
        return false;
    CC_SHA256(bytes, (CC_LONG)length, digest);
    return true;
}

static void
print_sha256(const uint8_t digest[CC_SHA256_DIGEST_LENGTH])
{
    for (size_t i = 0; i < CC_SHA256_DIGEST_LENGTH; i++)
        printf("%02x", digest[i]);
}

static bool
read_verified_bytes(
    const char *path,
    const char *expected_hex,
    image_bytes *image)
{
    memset(image, 0, sizeof(*image));
    uint8_t expected[CC_SHA256_DIGEST_LENGTH];
    uint8_t actual[CC_SHA256_DIGEST_LENGTH];
    if (!parse_sha256(expected_hex, expected)) {
        fprintf(stderr, "invalid expected SHA-256 for %s\n", path);
        return false;
    }

    int fd = open(path, O_RDONLY | O_CLOEXEC);
    struct stat identity;
    if (fd < 0 || fstat(fd, &identity) != 0 ||
        identity.st_size <= 0 || (uint64_t)identity.st_size > SIZE_MAX) {
        perror("open/fstat component");
        if (fd >= 0)
            close(fd);
        return false;
    }

    size_t length = (size_t)identity.st_size;
    uint8_t *bytes = malloc(length);
    if (bytes == NULL) {
        close(fd);
        return false;
    }
    size_t offset = 0;
    while (offset < length) {
        ssize_t count = pread(fd, bytes + offset, length - offset, (off_t)offset);
        if (count <= 0) {
            perror("pread component");
            free(bytes);
            close(fd);
            return false;
        }
        offset += (size_t)count;
    }
    close(fd);

    if (!hash_bytes(bytes, length, actual)) {
        free(bytes);
        return false;
    }
    printf("verified path=%s device=%" PRIu64 " inode=%" PRIu64
           " size=%" PRIu64 " sha256=",
           path,
           (uint64_t)identity.st_dev,
           (uint64_t)identity.st_ino,
           (uint64_t)identity.st_size);
    print_sha256(actual);
    putchar('\n');
    if (memcmp(actual, expected, sizeof(actual)) != 0) {
        fprintf(stderr, "expected SHA-256 mismatch; refusing load: %s\n", path);
        free(bytes);
        return false;
    }

    image->data = bytes;
    image->length = length;
    return true;
}

static bool
verify_path_hash(const char *path, const char *expected_hex)
{
    image_bytes image = {0};
    if (!read_verified_bytes(path, expected_hex, &image))
        return false;
    free(image.data);
    return true;
}

static bool
wait_for_marker(const char *ready, const char *go)
{
    int fd = open(ready, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (fd < 0) {
        perror("create ready marker");
        return false;
    }
    close(fd);
    for (unsigned i = 0; i < 1000; i++) {
        if (access(go, F_OK) == 0)
            return true;
        usleep(10000);
    }
    fprintf(stderr, "timed out waiting for replacement marker: %s\n", go);
    return false;
}

static bool
get_symbol(void *library, const char *name, void **symbol)
{
    dlerror();
    *symbol = dlsym(library, name);
    const char *error = dlerror();
    if (error != NULL) {
        fprintf(stderr, "dlsym(%s) failed: %s\n", name, error);
        return false;
    }
    return *symbol != NULL;
}

static bool
initialize_hostfxr(
    const char *hostfxr_path,
    const char *hostfxr_hash,
    const char *runtime_config,
    hostfxr_delegates *delegates)
{
    memset(delegates, 0, sizeof(*delegates));
    if (!verify_path_hash(hostfxr_path, hostfxr_hash))
        return false;

    delegates->library = dlopen(hostfxr_path, RTLD_NOW | RTLD_LOCAL);
    if (delegates->library == NULL) {
        fprintf(stderr, "dlopen(hostfxr) failed: %s\n", dlerror());
        return false;
    }

    hostfxr_initialize_for_runtime_config_fn initialize = NULL;
    hostfxr_get_runtime_delegate_fn get_delegate = NULL;
    hostfxr_close_fn close_context = NULL;
    void *symbol = NULL;
    if (!get_symbol(
            delegates->library,
            "hostfxr_initialize_for_runtime_config",
            &symbol))
        return false;
    initialize = (hostfxr_initialize_for_runtime_config_fn)symbol;
    if (!get_symbol(delegates->library, "hostfxr_get_runtime_delegate", &symbol))
        return false;
    get_delegate = (hostfxr_get_runtime_delegate_fn)symbol;
    if (!get_symbol(delegates->library, "hostfxr_close", &symbol))
        return false;
    close_context = (hostfxr_close_fn)symbol;

    hostfxr_handle context = NULL;
    int32_t status = initialize(runtime_config, NULL, &context);
    if (status != 0 || context == NULL) {
        fprintf(stderr, "hostfxr initialize returned %d\n", status);
        return false;
    }

    status = get_delegate(
        context,
        hdt_load_assembly_and_get_function_pointer,
        (void **)&delegates->load_path);
    if (status == 0)
        status = get_delegate(
            context,
            hdt_load_assembly_bytes,
            (void **)&delegates->load_bytes);
    if (status == 0)
        status = get_delegate(
            context,
            hdt_get_function_pointer,
            (void **)&delegates->get_function_pointer);
    int32_t close_status = close_context(context);
    if (status != 0 || close_status != 0 ||
        delegates->load_path == NULL || delegates->load_bytes == NULL ||
        delegates->get_function_pointer == NULL) {
        fprintf(stderr,
                "delegate setup failed: get=%d close=%d path=%p bytes=%p getfp=%p\n",
                status,
                close_status,
                (void *)delegates->load_path,
                (void *)delegates->load_bytes,
                (void *)delegates->get_function_pointer);
        return false;
    }
    return true;
}

static bool
get_fixture_markers(
    const hostfxr_delegates *delegates,
    bool use_bytes_delegate,
    const char *assembly_path,
    void **marker,
    void **secondary_marker)
{
    const char *type_name = "BytesBootstrap.Probe, ManagedProbe";
    int32_t status;
    if (use_bytes_delegate) {
        status = delegates->get_function_pointer(
            type_name,
            "GetMarker",
            UNMANAGEDCALLERSONLY_METHOD,
            NULL,
            NULL,
            marker);
        if (status == 0 && *marker != NULL)
            status = delegates->get_function_pointer(
                type_name,
                "GetSecondaryMarker",
                UNMANAGEDCALLERSONLY_METHOD,
                NULL,
                NULL,
                secondary_marker);
    } else {
        status = delegates->load_path(
            assembly_path,
            type_name,
            "GetMarker",
            UNMANAGEDCALLERSONLY_METHOD,
            NULL,
            marker);
        if (status == 0 && *marker != NULL)
            status = delegates->load_path(
                assembly_path,
                type_name,
                "GetSecondaryMarker",
                UNMANAGEDCALLERSONLY_METHOD,
                NULL,
                secondary_marker);
    }
    if (status != 0 || *marker == NULL || *secondary_marker == NULL) {
        fprintf(stderr, "fixture entrypoint resolution returned %d\n", status);
        return false;
    }
    return true;
}

static int
probe_fixture(
    const char *mode,
    const char *hostfxr_path,
    const char *hostfxr_hash,
    const char *runtime_config,
    const char *assembly_path,
    const char *assembly_hash,
    int32_t expected_marker,
    const char *ready,
    const char *go)
{
    bool use_bytes = strcmp(mode, "bytes") == 0;
    int32_t expected_secondary_marker = expected_marker + 1;
    image_bytes assembly = {0};
    if (!read_verified_bytes(assembly_path, assembly_hash, &assembly))
        return 2;

    hostfxr_delegates delegates;
    if (!initialize_hostfxr(
            hostfxr_path, hostfxr_hash, runtime_config, &delegates)) {
        free(assembly.data);
        return 3;
    }
    if (!use_bytes) {
        free(assembly.data);
        assembly.data = NULL;
        assembly.length = 0;
    }
    if (!wait_for_marker(ready, go)) {
        free(assembly.data);
        return 4;
    }

    void *marker = NULL;
    void *secondary_marker = NULL;
    if (use_bytes) {
        int32_t load_status = delegates.load_bytes(
            assembly.data,
            assembly.length,
            NULL,
            0,
            NULL,
            NULL);
        printf("load_assembly_bytes_status=%d bytes=%zu pdb=(null,0)\n",
               load_status, assembly.length);
        free(assembly.data);
        assembly.data = NULL;
        if (load_status != 0) {
            fprintf(stderr, "hdt_load_assembly_bytes failed: %d\n", load_status);
            return 5;
        }
    }

    if (!get_fixture_markers(
            &delegates, use_bytes, assembly_path, &marker,
            &secondary_marker))
        return 6;

    int32_t actual_marker = ((int32_t (*)(void))marker)();
    int32_t actual_secondary_marker =
        ((int32_t (*)(void))secondary_marker)();
    printf("fixture_mode=%s marker=%" PRId32 " secondary_marker=%" PRId32 "\n",
           mode, actual_marker, actual_secondary_marker);
    if (actual_marker != expected_marker ||
        actual_secondary_marker != expected_secondary_marker) {
        fprintf(stderr,
                "unexpected markers: expected=%" PRId32 "/%" PRId32 "\n",
                expected_marker, expected_secondary_marker);
        return 7;
    }

    if (use_bytes) {
        static const uint8_t invalid_image[] = "not a managed assembly";
        int32_t bad_load_status = delegates.load_bytes(
            invalid_image,
            sizeof(invalid_image),
            NULL,
            0,
            NULL,
            NULL);
        printf("invalid_bytes_status=%d\n", bad_load_status);
        if (bad_load_status == 0) {
            fprintf(stderr, "invalid assembly bytes were reported as success\n");
            return 8;
        }

        void *missing = NULL;
        int32_t missing_status = delegates.get_function_pointer(
            "BytesBootstrap.MissingType, ManagedProbe",
            "MissingMethod",
            UNMANAGEDCALLERSONLY_METHOD,
            NULL,
            NULL,
            &missing);
        printf("missing_entrypoint_status=%d pointer=%p\n",
               missing_status, missing);
        if (missing_status == 0 || missing != NULL) {
            fprintf(stderr, "missing managed entrypoint did not fail closed\n");
            return 9;
        }
    }

    return 0;
}

static bool
load_image_bytes(
    const hostfxr_delegates *delegates,
    const char *label,
    image_bytes *image)
{
    int32_t status = delegates->load_bytes(
        image->data,
        image->length,
        NULL,
        0,
        NULL,
        NULL);
    printf("load_bytes_image=%s status=%d bytes=%zu pdb=(null,0)\n",
           label, status, image->length);
    free(image->data);
    image->data = NULL;
    image->length = 0;
    return status == 0;
}

static int
probe_managed_host_bytes(
    const char *hostfxr_path,
    const char *hostfxr_hash,
    const char *runtime_config,
    const char *contracts_path,
    const char *contracts_hash,
    const char *negotiation_path,
    const char *negotiation_hash,
    const char *managed_host_path,
    const char *managed_host_hash,
    const char *ready,
    const char *go)
{
    image_bytes contracts = {0};
    image_bytes negotiation = {0};
    image_bytes managed_host = {0};
    if (!read_verified_bytes(contracts_path, contracts_hash, &contracts) ||
        !read_verified_bytes(negotiation_path, negotiation_hash, &negotiation) ||
        !read_verified_bytes(managed_host_path, managed_host_hash, &managed_host)) {
        free(contracts.data);
        free(negotiation.data);
        free(managed_host.data);
        return 10;
    }

    hostfxr_delegates delegates;
    if (!initialize_hostfxr(
            hostfxr_path, hostfxr_hash, runtime_config, &delegates)) {
        free(contracts.data);
        free(negotiation.data);
        free(managed_host.data);
        return 11;
    }

    if (!wait_for_marker(ready, go)) {
        free(contracts.data);
        free(negotiation.data);
        free(managed_host.data);
        return 12;
    }
    puts("managedhost_replacement_handshake=passed");

    if (!load_image_bytes(&delegates, "NetLift.Contracts", &contracts) ||
        !load_image_bytes(&delegates, "NetLift.Negotiation", &negotiation) ||
        !load_image_bytes(&delegates, "NetLift.ManagedHost", &managed_host)) {
        free(contracts.data);
        free(negotiation.data);
        free(managed_host.data);
        return 13;
    }

    const struct {
        const char *type_name;
        const char *method_name;
        uint32_t expected_size;
    } exports[] = {
        {
            "NetLift.ManagedHost.ManagedAppEndpointAuthorityApiV1Exports, NetLift.ManagedHost",
            "GetApiV1",
            48,
        },
        {
            "NetLift.ManagedHost.ManagedAppHostApiV1Exports, NetLift.ManagedHost",
            "GetApiV1",
            24,
        },
        {
            "NetLift.ManagedHost.ManagedAppEndpointTransportApiV1Exports, NetLift.ManagedHost",
            "GetApiV1",
            64,
        },
    };
    for (size_t i = 0; i < sizeof(exports) / sizeof(exports[0]); i++) {
        void *get_api_pointer = NULL;
        int32_t status = delegates.get_function_pointer(
            exports[i].type_name,
            exports[i].method_name,
            UNMANAGEDCALLERSONLY_METHOD,
            NULL,
            NULL,
            &get_api_pointer);
        if (status != 0 || get_api_pointer == NULL) {
            fprintf(stderr, "%s pointer status=%d\n",
                    exports[i].type_name, status);
            return 14;
        }

        uint32_t required_size = 0;
        status = ((int32_t (*)(uint32_t, uint32_t, uint32_t *, void *))
                      get_api_pointer)(1, 0, &required_size, NULL);
        printf("managedhost_export=%s status=%d required_size=%" PRIu32 "\n",
               exports[i].type_name, status, required_size);
        if (status != 0 || required_size != exports[i].expected_size)
            return 15;
    }
    return 0;
}

static int
probe_managed_host_path_control(
    const char *hostfxr_path,
    const char *hostfxr_hash,
    const char *runtime_config,
    const char *managed_host_path,
    const char *managed_host_hash,
    const char *ready,
    const char *go)
{
    image_bytes verified_image = {0};
    if (!read_verified_bytes(managed_host_path, managed_host_hash, &verified_image))
        return 20;
    free(verified_image.data);

    hostfxr_delegates delegates;
    if (!initialize_hostfxr(
            hostfxr_path, hostfxr_hash, runtime_config, &delegates))
        return 21;
    if (!wait_for_marker(ready, go))
        return 22;

    void *entrypoint = NULL;
    int32_t status = delegates.load_path(
        managed_host_path,
        "NetLift.ManagedHost.ManagedAppHostApiV1Exports, NetLift.ManagedHost",
        "GetApiV1",
        UNMANAGEDCALLERSONLY_METHOD,
        NULL,
        &entrypoint);
    printf("managedhost_path_control_status=%d pointer=%p\n",
           status, entrypoint);
    if (status == 0 || entrypoint != NULL) {
        fprintf(stderr, "path loader accepted the invalid replacement image\n");
        return 23;
    }
    return 0;
}

int
main(int argc, char **argv)
{
    if (argc == 10 &&
        (strcmp(argv[1], "path") == 0 || strcmp(argv[1], "bytes") == 0)) {
        int32_t expected_marker = (int32_t)strtol(argv[7], NULL, 10);
        return probe_fixture(
            argv[1], argv[2], argv[3], argv[4], argv[5], argv[6],
            expected_marker, argv[8], argv[9]);
    }
    if (argc == 13 && strcmp(argv[1], "managed-host") == 0)
        return probe_managed_host_bytes(
            argv[2], argv[3], argv[4], argv[5], argv[6], argv[7], argv[8],
            argv[9], argv[10], argv[11], argv[12]);
    if (argc == 9 && strcmp(argv[1], "managed-host-path-control") == 0)
        return probe_managed_host_path_control(
            argv[2], argv[3], argv[4], argv[5], argv[6], argv[7], argv[8]);

    fprintf(stderr,
            "usage:\n"
            "  %s <path|bytes> <hostfxr> <hostfxr-sha256> <runtimeconfig> "
            "<assembly> <assembly-sha256> <expected-marker> <ready> <go>\n"
            "  %s managed-host <hostfxr> <hostfxr-sha256> <runtimeconfig> "
            "<contracts> <contracts-sha256> <negotiation> "
            "<negotiation-sha256> <managedhost> <managedhost-sha256> "
            "<ready> <go>\n"
            "  %s managed-host-path-control <hostfxr> <hostfxr-sha256> "
            "<runtimeconfig> <managedhost> <managedhost-sha256> <ready> <go>\n",
            argv[0], argv[0], argv[0]);
    return 2;
}
