#include <CommonCrypto/CommonDigest.h>
#include <coreclr_delegates.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <hostfxr.h>
#include <inttypes.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#define MAX_MANIFEST_BYTES (1024u * 1024u)
#define MAX_SNAPSHOT_BYTES (64u * 1024u)
#define TRANSPORT_SUCCESS 0
#define HOST_MODULE_UNAVAILABLE 0x4E4C0105
#define RESULT_REPLY_KIND 0u
#define ROUTE_SERVICE_ID 30u
#define ROUTE_METHOD_ID 1u

typedef struct {
    uint8_t *data;
    size_t length;
} image_bytes;

typedef struct {
    void *library;
    load_assembly_bytes_fn load_bytes;
    get_function_pointer_fn get_function_pointer;
} hostfxr_delegates;

typedef int (*get_api_v1_fn)(
    uint32_t requested_version,
    uint32_t output_capacity,
    uint32_t *required_size,
    void *output);

typedef struct {
    uint32_t struct_size;
    uint32_t api_version;
    int (*create_endpoint)(
        uint8_t *, uint32_t, uint32_t, uint8_t *, uint32_t,
        uint32_t, uint32_t, uint64_t *);
    int (*start_endpoint)(
        uint64_t, uint8_t *, uint32_t, uint8_t *, uint32_t, uint32_t);
    int (*get_snapshot)(uint64_t, uint32_t, uint8_t **, uint32_t *);
    int (*close_endpoint)(uint64_t);
    int (*free_buffer)(uint8_t *);
} authority_api_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t api_version;
    int (*register_generated_host_from_path)(
        uint64_t,
        uint8_t *, uint32_t,
        uint8_t *, uint32_t,
        uint8_t *, uint32_t,
        uint8_t *, uint32_t,
        uint8_t *, uint32_t,
        uint64_t *);
    int (*close_generated_host_lease)(uint64_t);
} app_host_api_v1;

typedef int (*native_request_callback_fn)(
    uint64_t, uint64_t, uint64_t, uint64_t, uint32_t, uint32_t,
    uint8_t *, uint32_t);
typedef void (*publish_result_callback_fn)(
    uint64_t, uint64_t, uint64_t, uint32_t,
    uint8_t *, uint32_t, uint8_t *, uint32_t);

typedef struct {
    uint32_t struct_size;
    uint32_t api_version;
    int (*configure_native_callbacks)(uint64_t, intptr_t, intptr_t);
    int (*start_request)(uint64_t, uint64_t, uint64_t, uint64_t, uint8_t *, uint32_t);
    int (*cancel_operation)(uint64_t, uint64_t, uint64_t, uint64_t);
    int (*reply_confirmation)(
        uint64_t, uint64_t, uint64_t, uint64_t, uint32_t, uint32_t, uint8_t *, uint32_t);
    int (*cancel_confirmation)(uint64_t, uint64_t, uint64_t, uint64_t);
    int (*revoke_endpoint)(uint64_t);
    int (*get_outstanding_work)(uint64_t, uint32_t *, uint32_t *, uint32_t *, uint32_t *);
} transport_api_v1;

typedef int (*negative_fn)(
    uint32_t,
    uint8_t *, uint32_t, uint8_t *, uint32_t,
    uint8_t *, uint32_t, uint8_t *, uint32_t,
    uint8_t *, uint32_t, uint8_t *, uint32_t);

typedef int (*prepare_fn)(
    uint8_t *, uint32_t, uint8_t *, uint32_t,
    uint8_t *, uint32_t, uint8_t *, uint32_t,
    uint8_t *, uint32_t, uint8_t *, uint32_t,
    uint8_t *, uint32_t, uint32_t *,
    uint8_t *, uint32_t, uint32_t *,
    uint8_t *, uint32_t, uint32_t *);

typedef int (*build_peer_hello_fn)(uint8_t *, uint32_t, uint8_t *, uint32_t, uint32_t *);
typedef int (*activate_fn)(uint64_t, uint64_t *);
typedef int (*close_streamed_route_fn)(uint64_t, uint64_t);

static transport_api_v1 g_transport;
static uint64_t g_endpoint_token;
static uint64_t g_registration_token;
static uint64_t g_operation_token = 901;
static pthread_mutex_t g_callback_gate = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t g_callback_changed = PTHREAD_COND_INITIALIZER;
static uint32_t g_native_request_count;
static bool g_native_request_completed;
static uint32_t g_result_count;
static int g_reply_status = -1;
static uint32_t g_result_status = UINT32_MAX;
static uint64_t g_result_registration;
static uint64_t g_result_generation;
static uint64_t g_result_operation;
static bool g_callback_buffers_valid;
static uint8_t g_result[4096];
static uint32_t g_result_length;
static uint32_t g_error_length;

static uint32_t
read_u32(const uint8_t *source)
{
    return (uint32_t)source[0] |
        ((uint32_t)source[1] << 8) |
        ((uint32_t)source[2] << 16) |
        ((uint32_t)source[3] << 24);
}

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
read_verified_bytes(const char *path, const char *expected_hex, image_bytes *image)
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
        perror("open/fstat verified input");
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
            perror("pread verified input");
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
    if (!get_symbol(delegates->library, "hostfxr_initialize_for_runtime_config", &symbol))
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
    status = get_delegate(context, hdt_load_assembly_bytes, (void **)&delegates->load_bytes);
    if (status == 0)
        status = get_delegate(
            context, hdt_get_function_pointer,
            (void **)&delegates->get_function_pointer);
    int32_t close_status = close_context(context);
    if (status != 0 || close_status != 0 ||
        delegates->load_bytes == NULL || delegates->get_function_pointer == NULL) {
        fprintf(stderr, "hostfxr delegate setup failed: get=%d close=%d\n",
                status, close_status);
        return false;
    }
    return true;
}

static bool
load_image_bytes(const hostfxr_delegates *delegates, const char *label, image_bytes *image)
{
    int32_t status = delegates->load_bytes(
        image->data, image->length, NULL, 0, NULL, NULL);
    printf("hostfxr_load_bytes component=%s status=%d bytes=%zu pdb=(null,0)\n",
           label, status, image->length);
    free(image->data);
    image->data = NULL;
    image->length = 0;
    return status == 0;
}

static bool
resolve_uco(
    const hostfxr_delegates *delegates,
    const char *type_name,
    const char *method_name,
    void **function)
{
    int32_t status = delegates->get_function_pointer(
        type_name, method_name, UNMANAGEDCALLERSONLY_METHOD, NULL, NULL, function);
    if (status != 0 || *function == NULL) {
        fprintf(stderr, "get_function_pointer %s.%s status=%d ptr=%p\n",
                type_name, method_name, status, *function);
        return false;
    }
    return true;
}

static int
on_native_request(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t request_token,
    uint64_t operation_token,
    uint32_t service_id,
    uint32_t method_id,
    uint8_t *payload,
    uint32_t payload_length)
{
    pthread_mutex_lock(&g_callback_gate);
    g_native_request_count++;
    bool valid = registration_token == g_registration_token &&
        registration_generation == 1 &&
        request_token != 0 &&
        operation_token == g_operation_token &&
        service_id == ROUTE_SERVICE_ID &&
        method_id == ROUTE_METHOD_ID &&
        payload_length == 26 &&
        payload != NULL &&
        memcmp(payload, "NLF1", 4) == 0 &&
        payload[4] == 1 && payload[5] == 0 &&
        payload[6] == 1 && payload[7] == 0 &&
        read_u32(payload + 8) == 14 &&
        payload[12] == 1 && payload[13] == 0 &&
        payload[14] == 1 && read_u32(payload + 15) == 7 &&
        memcmp(payload + 19, "approve", 7) == 0;
    pthread_mutex_unlock(&g_callback_gate);

    if (!valid) {
        fprintf(stderr,
                "native confirmation payload mismatch registration=%" PRIu64
                " generation=%" PRIu64 " operation=%" PRIu64
                " service=%" PRIu32 " method=%" PRIu32 " length=%" PRIu32 "\n",
                registration_token, registration_generation, operation_token,
                service_id, method_id, payload_length);
        pthread_mutex_lock(&g_callback_gate);
        g_native_request_completed = true;
        pthread_cond_broadcast(&g_callback_changed);
        pthread_mutex_unlock(&g_callback_gate);
        return 0;
    }
    int status = g_transport.reply_confirmation(
        g_endpoint_token,
        registration_token,
        registration_generation,
        request_token,
        RESULT_REPLY_KIND,
        1,
        NULL,
        0);

    pthread_mutex_lock(&g_callback_gate);
    g_reply_status = status;
    g_native_request_completed = true;
    pthread_cond_broadcast(&g_callback_changed);
    pthread_mutex_unlock(&g_callback_gate);
    return status == TRANSPORT_SUCCESS ? 1 : 0;
}

static void
on_publish_result(
    uint64_t registration_token,
    uint64_t registration_generation,
    uint64_t operation_token,
    uint32_t status,
    uint8_t *result,
    uint32_t result_length,
    uint8_t *error,
    uint32_t error_length)
{
    pthread_mutex_lock(&g_callback_gate);
    g_result_count++;
    g_result_status = status;
    g_result_registration = registration_token;
    g_result_generation = registration_generation;
    g_result_operation = operation_token;
    g_error_length = error_length;
    g_callback_buffers_valid =
        (result_length == 0 || result != NULL) &&
        (error_length == 0 || error != NULL);
    if (result != NULL && result_length <= sizeof(g_result)) {
        memcpy(g_result, result, result_length);
        g_result_length = result_length;
    } else {
        g_result_length = 0;
    }
    pthread_cond_broadcast(&g_callback_changed);
    pthread_mutex_unlock(&g_callback_gate);
}

static void
write_u16(uint8_t *destination, uint16_t value)
{
    destination[0] = (uint8_t)value;
    destination[1] = (uint8_t)(value >> 8);
}

static void
write_u32(uint8_t *destination, uint32_t value)
{
    destination[0] = (uint8_t)value;
    destination[1] = (uint8_t)(value >> 8);
    destination[2] = (uint8_t)(value >> 16);
    destination[3] = (uint8_t)(value >> 24);
}

static uint32_t
build_request(uint8_t request[128])
{
    memset(request, 0, 128);
    memcpy(request, "NLF1", 4);
    write_u16(request + 4, 1);
    write_u16(request + 6, 3);
    size_t offset = 12;
    write_u16(request + offset, 1);
    request[offset + 2] = 1;
    write_u32(request + offset + 3, 7);
    memcpy(request + offset + 7, "approve", 7);
    offset += 14;
    write_u16(request + offset, 2);
    request[offset + 2] = 2;
    write_u32(request + offset + 3, 4);
    write_u32(request + offset + 7, 7);
    offset += 11;
    write_u16(request + offset, 7);
    request[offset + 2] = 1;
    write_u32(request + offset + 3, 6);
    memcpy(request + offset + 7, "native", 6);
    offset += 13;
    write_u32(request + 8, (uint32_t)(offset - 12));
    return (uint32_t)offset;
}

static bool
validate_result(void)
{
    if (g_result_length != 39 ||
        memcmp(g_result, "NLF1", 4) != 0 ||
        g_result[4] != 1 || g_result[5] != 0 ||
        g_result[6] != 3 || g_result[7] != 0 ||
        read_u32(g_result + 8) != 27)
        return false;

    size_t offset = 12;
    if (g_result[offset] != 1 || g_result[offset + 1] != 0 ||
        g_result[offset + 2] != 2 || read_u32(g_result + offset + 3) != 4 ||
        read_u32(g_result + offset + 7) != 7)
        return false;
    offset += 11;
    if (g_result[offset] != 2 || g_result[offset + 1] != 0 ||
        g_result[offset + 2] != 3 || read_u32(g_result + offset + 3) != 1 ||
        g_result[offset + 7] != 1)
        return false;
    offset += 8;
    return g_result[offset] == 9 && g_result[offset + 1] == 0 &&
        g_result[offset + 2] == 3 && read_u32(g_result + offset + 3) == 1 &&
        g_result[offset + 7] == 1;
}

static bool
wait_for_markers(const char *ready, const char *go)
{
    int fd = open(ready, O_WRONLY | O_CREAT | O_TRUNC, 0600);
    if (fd < 0) {
        perror("create ready marker");
        return false;
    }
    close(fd);
    for (unsigned i = 0; i < 2000; i++) {
        if (access(go, F_OK) == 0)
            return true;
        usleep(10000);
    }
    fprintf(stderr, "timed out waiting for replacement: %s\n", go);
    return false;
}

static bool
wait_for_result(void)
{
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += 15;
    pthread_mutex_lock(&g_callback_gate);
    while (g_result_count == 0 || !g_native_request_completed) {
        int status = pthread_cond_timedwait(
            &g_callback_changed, &g_callback_gate, &deadline);
        if (status != 0) {
            pthread_mutex_unlock(&g_callback_gate);
            fprintf(stderr, "timed out waiting for managed result callback (%d)\n", status);
            return false;
        }
    }
    pthread_mutex_unlock(&g_callback_gate);
    return true;
}

static bool
wait_for_no_work(uint64_t endpoint_token)
{
    for (unsigned i = 0; i < 500; i++) {
        uint32_t operations = UINT32_MAX;
        uint32_t confirmations = UINT32_MAX;
        uint32_t callbacks = UINT32_MAX;
        uint32_t route_leases = UINT32_MAX;
        int status = g_transport.get_outstanding_work(
            endpoint_token, &operations, &confirmations, &callbacks, &route_leases);
        if (status != TRANSPORT_SUCCESS) {
            fprintf(stderr, "GetOutstandingWork failed: %d\n", status);
            return false;
        }
        if (operations == 0 && confirmations == 0 && callbacks == 0 && route_leases == 0)
            return true;
        usleep(10000);
    }
    fprintf(stderr, "managed operation/callback/route leases did not drain\n");
    return false;
}

static bool
copy_snapshot(
    const authority_api_v1 *api,
    uint64_t endpoint_token,
    uint32_t kind,
    uint8_t **bytes,
    uint32_t *length)
{
    *bytes = NULL;
    *length = 0;
    int status = api->get_snapshot(endpoint_token, kind, bytes, length);
    if (status != 0 || *bytes == NULL || *length == 0) {
        fprintf(stderr, "GetSnapshot kind=%" PRIu32 " status=%d length=%" PRIu32 "\n",
                kind, status, *length);
        return false;
    }
    return true;
}

static bool
query_api(
    const hostfxr_delegates *delegates,
    const char *type_name,
    uint32_t version,
    uint32_t expected_size,
    void *table)
{
    void *entrypoint = NULL;
    if (!resolve_uco(delegates, type_name, "GetApiV1", &entrypoint))
        return false;
    uint32_t required = 0;
    int status = ((get_api_v1_fn)entrypoint)(version, 0, &required, NULL);
    if (status != 0 || required != expected_size) {
        fprintf(stderr, "%s GetApiV1 size query: status=%d required=%" PRIu32 "\n",
                type_name, status, required);
        return false;
    }
    memset(table, 0, expected_size);
    status = ((get_api_v1_fn)entrypoint)(version, expected_size, &required, table);
    if (status != 0 || required != expected_size)
        return false;
    printf("NATIVE_TABLE type=%s version=%" PRIu32 " size=%" PRIu32 " status=0\n",
           type_name, version, required);
    return true;
}

static int
run_probe(int argc, char **argv)
{
    if (argc != 20) {
        fprintf(stderr,
                "usage: %s <hostfxr> <hostfxr-sha256> <runtimeconfig> "
                "<contracts> <contracts-sha256> <negotiation> <negotiation-sha256> "
                "<managedhost> <managedhost-sha256> <harness> <harness-sha256> "
                "<root> <root-sha256> <dependency> <dependency-sha256> "
                "<provider> <provider-sha256> <ready> <go>\n",
                argv[0]);
        return 2;
    }

    image_bytes contracts = {0};
    image_bytes negotiation = {0};
    image_bytes managed_host = {0};
    image_bytes harness = {0};
    image_bytes root = {0};
    image_bytes dependency = {0};
    image_bytes provider = {0};
    const char *hostfxr_path = argv[1];
    const char *hostfxr_hash = argv[2];
    const char *runtime_config = argv[3];
    const char *contracts_path = argv[4];
    const char *negotiation_path = argv[6];
    const char *managed_host_path = argv[8];
    const char *harness_path = argv[10];
    const char *root_path = argv[12];
    const char *dependency_path = argv[14];
    const char *provider_path = argv[16];
    if (!read_verified_bytes(contracts_path, argv[5], &contracts) ||
        !read_verified_bytes(negotiation_path, argv[7], &negotiation) ||
        !read_verified_bytes(managed_host_path, argv[9], &managed_host) ||
        !read_verified_bytes(harness_path, argv[11], &harness) ||
        !read_verified_bytes(root_path, argv[13], &root) ||
        !read_verified_bytes(dependency_path, argv[15], &dependency) ||
        !read_verified_bytes(provider_path, argv[17], &provider))
        return 3;

    static const char wrong_hash[] =
        "0000000000000000000000000000000000000000000000000000000000000000";
    image_bytes rejected = {0};
    if (read_verified_bytes(root_path, wrong_hash, &rejected)) {
        free(rejected.data);
        fprintf(stderr, "incorrect expected hash unexpectedly passed verification\n");
        return 4;
    }
    puts("NEGATIVE incorrect_expected_hash=rejected_before_load");

    hostfxr_delegates delegates;
    if (!initialize_hostfxr(hostfxr_path, hostfxr_hash, runtime_config, &delegates))
        return 5;
    if (!load_image_bytes(&delegates, "NetLift.Contracts", &contracts) ||
        !load_image_bytes(&delegates, "NetLift.Negotiation", &negotiation) ||
        !load_image_bytes(&delegates, "NetLift.ManagedHost", &managed_host) ||
        !load_image_bytes(&delegates, "NativeStreamedRoute", &harness))
        return 6;

    void *negative_pointer = NULL;
    if (!resolve_uco(
            &delegates,
            "NativeStreamedRoute.RouteExports, NativeStreamedRoute",
            "ProbeNegative",
            &negative_pointer))
        return 7;
    negative_fn negative = (negative_fn)negative_pointer;
    int negative_status = negative(
        1,
        (uint8_t *)root_path, (uint32_t)strlen(root_path),
        root.data, (uint32_t)root.length,
        (uint8_t *)dependency_path, (uint32_t)strlen(dependency_path),
        dependency.data, (uint32_t)dependency.length,
        (uint8_t *)provider_path, (uint32_t)strlen(provider_path),
        provider.data, (uint32_t)provider.length);
    if (negative_status != 0)
        return 8;
    negative_status = negative(
        2,
        (uint8_t *)root_path, (uint32_t)strlen(root_path),
        root.data, (uint32_t)root.length,
        (uint8_t *)dependency_path, (uint32_t)strlen(dependency_path),
        dependency.data, (uint32_t)dependency.length,
        (uint8_t *)provider_path, (uint32_t)strlen(provider_path),
        provider.data, (uint32_t)provider.length);
    if (negative_status != 0)
        return 9;
    static uint8_t corrupt_provider[] = "corrupted provider bytes";
    negative_status = negative(
        3,
        (uint8_t *)root_path, (uint32_t)strlen(root_path),
        root.data, (uint32_t)root.length,
        (uint8_t *)dependency_path, (uint32_t)strlen(dependency_path),
        dependency.data, (uint32_t)dependency.length,
        (uint8_t *)provider_path, (uint32_t)strlen(provider_path),
        corrupt_provider, sizeof(corrupt_provider));
    if (negative_status != 0)
        return 10;

    void *prepare_pointer = NULL;
    void *hello_pointer = NULL;
    void *activate_pointer = NULL;
    void *close_pointer = NULL;
    if (!resolve_uco(
            &delegates,
            "NativeStreamedRoute.RouteExports, NativeStreamedRoute",
            "Prepare",
            &prepare_pointer) ||
        !resolve_uco(
            &delegates,
            "NativeStreamedRoute.RouteExports, NativeStreamedRoute",
            "BuildPeerHello",
            &hello_pointer) ||
        !resolve_uco(
            &delegates,
            "NativeStreamedRoute.RouteExports, NativeStreamedRoute",
            "Activate",
            &activate_pointer) ||
        !resolve_uco(
            &delegates,
            "NativeStreamedRoute.RouteExports, NativeStreamedRoute",
            "Close",
            &close_pointer))
        return 11;

    uint8_t *manifest = calloc(1, MAX_MANIFEST_BYTES);
    uint8_t schema_hash[32] = {0};
    uint8_t marker[128] = {0};
    uint32_t manifest_size = 0;
    uint32_t schema_hash_size = 0;
    uint32_t marker_size = 0;
    if (manifest == NULL)
        return 12;
    int status = ((prepare_fn)prepare_pointer)(
        (uint8_t *)root_path, (uint32_t)strlen(root_path),
        root.data, (uint32_t)root.length,
        (uint8_t *)dependency_path, (uint32_t)strlen(dependency_path),
        dependency.data, (uint32_t)dependency.length,
        (uint8_t *)provider_path, (uint32_t)strlen(provider_path),
        provider.data, (uint32_t)provider.length,
        manifest, MAX_MANIFEST_BYTES, &manifest_size,
        schema_hash, sizeof(schema_hash), &schema_hash_size,
        marker, sizeof(marker), &marker_size);
    if (status != 0 || manifest_size == 0 || schema_hash_size != 32 ||
        marker_size == 0 || marker_size >= sizeof(marker) ||
        strcmp((char *)marker, "root-original/dependency-original") != 0) {
        fprintf(stderr, "Prepare streamed closure status=%d manifest=%" PRIu32
                " hash=%" PRIu32 " marker=%s\n",
                status, manifest_size, schema_hash_size, marker);
        free(manifest);
        return 13;
    }
    puts("PASS streamed root/dependency/provider snapshots prepared");

    authority_api_v1 authority = {0};
    app_host_api_v1 app_host = {0};
    transport_api_v1 transport = {0};
    if (!query_api(
            &delegates,
            "NetLift.ManagedHost.ManagedAppEndpointAuthorityApiV1Exports, NetLift.ManagedHost",
            1,
            48,
            &authority) ||
        !query_api(
            &delegates,
            "NetLift.ManagedHost.ManagedAppHostApiV1Exports, NetLift.ManagedHost",
            1,
            24,
            &app_host) ||
        !query_api(
            &delegates,
            "NetLift.ManagedHost.ManagedAppEndpointTransportApiV1Exports, NetLift.ManagedHost",
            1,
            64,
            &transport))
        return 14;
    g_transport = transport;

    uint32_t hello_size = 0;
    uint8_t hello[54] = {0};
    status = ((build_peer_hello_fn)hello_pointer)(
        manifest, manifest_size, hello, sizeof(hello), &hello_size);
    if (status != 0 || hello_size != sizeof(hello)) {
        fprintf(stderr, "peer hello status=%d size=%" PRIu32 "\n", status, hello_size);
        return 15;
    }

    uint64_t endpoint_token = 0;
    status = authority.create_endpoint(
        manifest, manifest_size, 1, schema_hash, schema_hash_size, 1, 1, &endpoint_token);
    if (status != 0 || endpoint_token == 0) {
        fprintf(stderr, "CreateEndpoint status=%d token=%" PRIu64 "\n",
                status, endpoint_token);
        return 16;
    }
    status = authority.start_endpoint(
        endpoint_token, manifest, manifest_size, hello, hello_size, 1);
    if (status != 0) {
        fprintf(stderr, "StartEndpoint status=%d\n", status);
        return 17;
    }
    g_endpoint_token = endpoint_token;

    uint8_t *local_manifest = NULL;
    uint8_t *nlps = NULL;
    uint8_t *nlmr = NULL;
    uint32_t local_manifest_length = 0;
    uint32_t nlps_length = 0;
    uint32_t nlmr_length = 0;
    if (!copy_snapshot(
            &authority, endpoint_token, 1, &local_manifest, &local_manifest_length) ||
        !copy_snapshot(&authority, endpoint_token, 2, &nlps, &nlps_length) ||
        !copy_snapshot(&authority, endpoint_token, 3, &nlmr, &nlmr_length))
        return 18;
    if (local_manifest_length != manifest_size ||
        memcmp(local_manifest, manifest, manifest_size) != 0 ||
        nlps_length < 88 || nlmr_length < 136 ||
        memcmp(nlps, "NLPS", 4) != 0 || memcmp(nlmr, "NLMR", 4) != 0) {
        fprintf(stderr, "authority snapshots do not match the prepared v5 fixture\n");
        return 19;
    }

    if (!wait_for_markers(argv[18], argv[19]))
        return 20;
    puts("PATH_REPLACEMENT_HANDSHAKE=passed (runner replaced staged root, dependency, provider)");

    static const uint8_t provider_type[] =
        "NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider";
    uint64_t path_lease = 0;
    status = app_host.register_generated_host_from_path(
        endpoint_token,
        nlps, nlps_length,
        nlmr, nlmr_length,
        local_manifest, local_manifest_length,
        (uint8_t *)root_path, (uint32_t)strlen(root_path),
        (uint8_t *)provider_type, (uint32_t)(sizeof(provider_type) - 1),
        &path_lease);
    printf("PATH_CONTROL register_from_invalid_path_status=%d lease=%" PRIu64 "\n",
           status, path_lease);
    if (status != HOST_MODULE_UNAVAILABLE || path_lease != 0) {
        fprintf(stderr, "path-based ManagedHost registration accepted invalid root bytes\n");
        return 21;
    }

    uint64_t registration_token = 0;
    status = ((activate_fn)activate_pointer)(endpoint_token, &registration_token);
    if (status != 0 || registration_token == 0) {
        fprintf(stderr, "streamed provider activation status=%d token=%" PRIu64 "\n",
                status, registration_token);
        return 22;
    }
    g_registration_token = registration_token;
    status = transport.configure_native_callbacks(
        endpoint_token,
        (intptr_t)&on_native_request,
        (intptr_t)&on_publish_result);
    if (status != TRANSPORT_SUCCESS) {
        fprintf(stderr, "ConfigureNativeCallbacks status=%d\n", status);
        return 23;
    }

    uint8_t request[128];
    uint32_t request_length = build_request(request);
    status = transport.start_request(
        endpoint_token, registration_token, 1, g_operation_token,
        request, request_length);
    if (status != TRANSPORT_SUCCESS) {
        fprintf(stderr, "StartRequest status=%d\n", status);
        return 24;
    }
    if (!wait_for_result())
        return 25;

    pthread_mutex_lock(&g_callback_gate);
    bool callback_values_valid =
        g_native_request_count == 1 &&
        g_result_count == 1 &&
        g_reply_status == TRANSPORT_SUCCESS &&
        g_result_status == TRANSPORT_SUCCESS &&
        g_result_registration == registration_token &&
        g_result_generation == 1 &&
        g_result_operation == g_operation_token &&
        g_error_length == 0 &&
        g_callback_buffers_valid &&
        validate_result();
    pthread_mutex_unlock(&g_callback_gate);
    if (!callback_values_valid) {
        fprintf(stderr,
                "callback/result validation failed: requests=%" PRIu32
                " results=%" PRIu32 " reply=%d result_status=%" PRIu32
                " bytes=%" PRIu32 "\n",
                g_native_request_count, g_result_count, g_reply_status,
                g_result_status, g_result_length);
        return 26;
    }
    printf("NATIVE_CALLBACK typed_confirm=accepted result_code=7 accepted=true audited=true\n");

    if (!wait_for_no_work(endpoint_token))
        return 27;
    status = transport.revoke_endpoint(endpoint_token);
    if (status != TRANSPORT_SUCCESS) {
        fprintf(stderr, "RevokeEndpoint status=%d\n", status);
        return 28;
    }
    status = ((close_streamed_route_fn)close_pointer)(
        endpoint_token, registration_token);
    if (status != 0) {
        fprintf(stderr, "streamed route close status=%d\n", status);
        return 29;
    }
    status = authority.close_endpoint(endpoint_token);
    if (status != 0) {
        fprintf(stderr, "CloseEndpoint status=%d\n", status);
        return 30;
    }

    uint32_t operations = UINT32_MAX;
    uint32_t confirmations = UINT32_MAX;
    uint32_t callbacks = UINT32_MAX;
    uint32_t route_leases = UINT32_MAX;
    status = transport.get_outstanding_work(
        endpoint_token, &operations, &confirmations, &callbacks, &route_leases);
    if (status != 10 || operations != 0 || confirmations != 0 ||
        callbacks != 0 || route_leases != 0) {
        fprintf(stderr, "post-close stale/drained check status=%d counts=%" PRIu32
                "/%" PRIu32 "/%" PRIu32 "/%" PRIu32 "\n",
                status, operations, confirmations, callbacks, route_leases);
        return 31;
    }
    puts("PASS real ManagedHost V1 tables + generated V2 route + native callback/result + close/drain");

    authority.free_buffer(local_manifest);
    authority.free_buffer(nlps);
    authority.free_buffer(nlmr);
    free(manifest);
    free(root.data);
    free(dependency.data);
    free(provider.data);
    return 0;
}

int
main(int argc, char **argv)
{
    return run_probe(argc, argv);
}
