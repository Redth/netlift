#define _DARWIN_C_SOURCE

#include <CoreFoundation/CoreFoundation.h>
#include <Security/SecRequirement.h>
#include <Security/SecStaticCode.h>
#include <coreclr_delegates.h>
#include <host_error_codes.h>
#include <hostfxr.h>
#include <mach-o/dyld.h>
#include <nethost.h>

#include <dlfcn.h>
#include <limits.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

typedef int (CORECLR_DELEGATE_CALLTYPE *managed_entry_fn)(void);

struct framework_resolution
{
    const char *dotnet_root;
    bool found;
    bool path_is_local;
};

static void
write_hostfxr_error(const char_t *message)
{
    if (message != NULL)
        fprintf(stderr, "HOSTFXR: %s\n", message);
}

static bool
path_is_within(const char *root, const char *path)
{
    size_t root_length = strlen(root);
    return strncmp(root, path, root_length) == 0 &&
        (path[root_length] == '/' || path[root_length] == '\0');
}

static OSStatus
check_team_requirement(const char *path, const char *team_id)
{
    char requirement_text[512];
    int length = snprintf(
        requirement_text,
        sizeof(requirement_text),
        "anchor apple generic and certificate leaf[subject.OU] = \"%s\"",
        team_id);
    if (length <= 0 || (size_t)length >= sizeof(requirement_text))
        return errSecParam;

    CFStringRef requirement_string = CFStringCreateWithCString(
        kCFAllocatorDefault, requirement_text, kCFStringEncodingUTF8);
    if (requirement_string == NULL)
        return errSecParam;

    SecRequirementRef requirement = NULL;
    OSStatus status = SecRequirementCreateWithString(
        requirement_string, kSecCSDefaultFlags, &requirement);
    CFRelease(requirement_string);
    if (status != errSecSuccess)
        return status;

    CFURLRef url = CFURLCreateFromFileSystemRepresentation(
        kCFAllocatorDefault,
        (const UInt8 *)path,
        (CFIndex)strlen(path),
        false);
    if (url == NULL) {
        CFRelease(requirement);
        return errSecParam;
    }

    SecStaticCodeRef code = NULL;
    status = SecStaticCodeCreateWithPath(url, kSecCSDefaultFlags, &code);
    CFRelease(url);
    if (status == errSecSuccess) {
        status = SecStaticCodeCheckValidity(
            code,
            kSecCSStrictValidate | kSecCSCheckAllArchitectures,
            requirement);
        CFRelease(code);
    }
    CFRelease(requirement);
    return status;
}

static OSStatus
validate_team(const char *path, const char *team_id, const char *label)
{
    OSStatus status = check_team_requirement(path, team_id);
    if (status == errSecSuccess)
        printf("PASS external Team requirement validates %s\n", label);
    else
        fprintf(stderr, "FAIL external Team requirement rejects %s OSStatus=%d\n",
            label, (int)status);
    return status;
}

static void
resolve_framework_callback(
    const struct hostfxr_resolve_frameworks_result *result,
    void *context)
{
    struct framework_resolution *resolution = context;
    if (result == NULL) {
        fprintf(stderr, "FAIL hostfxr returned a null framework resolution result\n");
        return;
    }
    for (size_t i = 0; i < result->resolved_count; i++) {
        const struct hostfxr_framework_result *framework =
            &result->resolved_frameworks[i];
        if (framework->name == NULL ||
            strcmp(framework->name, "Microsoft.NETCore.App") != 0 ||
            framework->resolved_path == NULL)
            continue;

        char canonical_path[PATH_MAX];
        resolution->found = true;
        resolution->path_is_local =
            realpath(framework->resolved_path, canonical_path) != NULL &&
            path_is_within(resolution->dotnet_root, canonical_path);
        if (!resolution->path_is_local) {
            fprintf(stderr, "FAIL runtime framework resolved outside staged root\n");
            return;
        }
        printf("PASS hostfxr resolves Microsoft.NETCore.App under staged root\n");
        return;
    }
    fprintf(stderr, "FAIL hostfxr did not resolve Microsoft.NETCore.App\n");
}

static bool
get_symbol(void *library, const char *name, void *function_pointer, size_t size)
{
    dlerror();
    void *symbol = dlsym(library, name);
    const char *error = dlerror();
    if (error != NULL || symbol == NULL) {
        fprintf(stderr, "FAIL resolve hostfxr export %s: %s\n",
            name, error == NULL ? "(null symbol)" : error);
        return false;
    }
    if (size != sizeof(symbol)) {
        fprintf(stderr, "FAIL function and data pointer sizes differ for %s\n", name);
        return false;
    }
    memcpy(function_pointer, &symbol, size);
    return true;
}

static bool
no_jit_image_loaded(void)
{
    uint32_t count = _dyld_image_count();
    for (uint32_t index = 0; index < count; index++) {
        const char *image = _dyld_get_image_name(index);
        if (image != NULL && strstr(image, "libclrjit") != NULL) {
            fprintf(stderr, "FAIL JIT image loaded: %s\n", image);
            return false;
        }
    }
    printf("PASS no libclrjit image is loaded in the host process\n");
    return true;
}

static int
run_host(const char *dotnet_root, const char *runtime_config, const char *assembly)
{
    char canonical_root[PATH_MAX];
    if (realpath(dotnet_root, canonical_root) == NULL) {
        fprintf(stderr, "FAIL resolve staged DOTNET_ROOT\n");
        return 1;
    }

    struct get_hostfxr_parameters locator = {
        .size = sizeof(locator),
        .assembly_path = NULL,
        .dotnet_root = canonical_root
    };
    char hostfxr_path[PATH_MAX];
    size_t hostfxr_path_size = sizeof(hostfxr_path);
    int locator_status = get_hostfxr_path(
        hostfxr_path, &hostfxr_path_size, &locator);
    if (locator_status != 0) {
        fprintf(stderr, "FAIL nethost get_hostfxr_path status=0x%x\n",
            locator_status);
        return 1;
    }

    char canonical_hostfxr[PATH_MAX];
    if (realpath(hostfxr_path, canonical_hostfxr) == NULL ||
        !path_is_within(canonical_root, canonical_hostfxr)) {
        fprintf(stderr, "FAIL nethost resolved hostfxr outside staged DOTNET_ROOT\n");
        return 1;
    }
    printf("PASS nethost resolves hostfxr inside staged DOTNET_ROOT\n");

    const char *host_team = getenv("NETLIFT_SAME_TEAM_ID");
    if (host_team == NULL || host_team[0] == '\0') {
        fprintf(stderr, "FAIL NETLIFT_SAME_TEAM_ID is required\n");
        return 1;
    }
    if (validate_team(canonical_hostfxr, host_team, "staged hostfxr") != errSecSuccess)
        return 1;

    void *hostfxr = dlopen(canonical_hostfxr, RTLD_NOW | RTLD_LOCAL);
    if (hostfxr == NULL) {
        fprintf(stderr, "FAIL dlopen staged hostfxr: %s\n", dlerror());
        return 1;
    }

    hostfxr_initialize_for_runtime_config_fn initialize = NULL;
    hostfxr_get_runtime_delegate_fn get_runtime_delegate = NULL;
    hostfxr_close_fn close_context = NULL;
    hostfxr_set_error_writer_fn set_error_writer = NULL;
    hostfxr_resolve_frameworks_for_runtime_config_fn resolve_frameworks = NULL;
    if (!get_symbol(hostfxr, "hostfxr_initialize_for_runtime_config",
                    &initialize, sizeof(initialize)) ||
        !get_symbol(hostfxr, "hostfxr_get_runtime_delegate",
                    &get_runtime_delegate, sizeof(get_runtime_delegate)) ||
        !get_symbol(hostfxr, "hostfxr_close", &close_context, sizeof(close_context)) ||
        !get_symbol(hostfxr, "hostfxr_set_error_writer",
                    &set_error_writer, sizeof(set_error_writer)) ||
        !get_symbol(hostfxr, "hostfxr_resolve_frameworks_for_runtime_config",
                    &resolve_frameworks, sizeof(resolve_frameworks))) {
        dlclose(hostfxr);
        return 1;
    }
    set_error_writer(write_hostfxr_error);

    struct hostfxr_initialize_parameters parameters = {
        .size = sizeof(parameters),
        .host_path = NULL,
        .dotnet_root = canonical_root
    };
    struct framework_resolution resolution = {
        .dotnet_root = canonical_root,
        .found = false,
        .path_is_local = false
    };
    int32_t status = resolve_frameworks(
        runtime_config, &parameters, resolve_framework_callback, &resolution);
    if (!STATUS_CODE_SUCCEEDED(status) ||
        !resolution.found || !resolution.path_is_local) {
        fprintf(stderr, "FAIL resolve framework status=0x%x\n", (unsigned int)status);
        dlclose(hostfxr);
        return 1;
    }

    hostfxr_handle context = NULL;
    status = initialize(runtime_config, &parameters, &context);
    if (!STATUS_CODE_SUCCEEDED(status) || context == NULL) {
        fprintf(stderr, "FAIL hostfxr_initialize_for_runtime_config status=0x%x\n",
            (unsigned int)status);
        dlclose(hostfxr);
        return 1;
    }
    printf("PASS hostfxr_initialize_for_runtime_config status=0x%x\n",
        (unsigned int)status);

    void *delegate = NULL;
    status = get_runtime_delegate(
        context, hdt_load_assembly_and_get_function_pointer, &delegate);
    if (!STATUS_CODE_SUCCEEDED(status) || delegate == NULL) {
        fprintf(stderr, "FAIL hostfxr_get_runtime_delegate status=0x%x\n",
            (unsigned int)status);
        close_context(context);
        dlclose(hostfxr);
        return 1;
    }
    printf("PASS hostfxr_get_runtime_delegate\n");

    load_assembly_and_get_function_pointer_fn load_assembly = NULL;
    if (sizeof(load_assembly) != sizeof(delegate)) {
        fprintf(stderr, "FAIL load-assembly function pointer size mismatch\n");
        close_context(context);
        dlclose(hostfxr);
        return 1;
    }
    memcpy(&load_assembly, &delegate, sizeof(load_assembly));

    void *entry_pointer = NULL;
    status = load_assembly(
        assembly,
        "RuntimeProbe.Entry, ManagedProbe",
        "Invoke",
        UNMANAGEDCALLERSONLY_METHOD,
        NULL,
        &entry_pointer);
    if (!STATUS_CODE_SUCCEEDED(status) || entry_pointer == NULL) {
        fprintf(stderr, "FAIL load_assembly_and_get_function_pointer status=0x%x\n",
            (unsigned int)status);
        close_context(context);
        dlclose(hostfxr);
        return 1;
    }
    printf("PASS load_assembly_and_get_function_pointer\n");

    managed_entry_fn invoke = NULL;
    if (sizeof(invoke) != sizeof(entry_pointer)) {
        fprintf(stderr, "FAIL managed entry function pointer size mismatch\n");
        close_context(context);
        dlclose(hostfxr);
        return 1;
    }
    memcpy(&invoke, &entry_pointer, sizeof(invoke));

    int managed_result = invoke();
    if (managed_result != 202611) {
        fprintf(stderr, "FAIL managed API returned %d, expected 202611\n",
            managed_result);
        close_context(context);
        dlclose(hostfxr);
        return 1;
    }
    printf("PASS managed API invoked result=%d\n", managed_result);
    if (!no_jit_image_loaded()) {
        close_context(context);
        dlclose(hostfxr);
        return 1;
    }

    status = close_context(context);
    if (!STATUS_CODE_SUCCEEDED(status)) {
        fprintf(stderr, "FAIL hostfxr_close status=0x%x\n", (unsigned int)status);
        dlclose(hostfxr);
        return 1;
    }
    dlclose(hostfxr);
    return 0;
}

static int
run_foreign_hostfxr_control(const char *hostfxr_path, const char *microsoft_team)
{
    if (validate_team(
            hostfxr_path,
            microsoft_team,
            "original Microsoft-signed hostfxr") != errSecSuccess)
        return 1;

    const char *host_team = getenv("NETLIFT_SAME_TEAM_ID");
    if (host_team == NULL || host_team[0] == '\0') {
        fprintf(stderr, "FAIL NETLIFT_SAME_TEAM_ID is required\n");
        return 1;
    }
    OSStatus host_team_status = check_team_requirement(hostfxr_path, host_team);
    if (host_team_status == errSecSuccess) {
        fprintf(stderr, "FAIL original Microsoft hostfxr unexpectedly matched host Team\n");
        return 1;
    }
    printf("PASS original Microsoft hostfxr rejected by host Team requirement status=%d\n",
        (int)host_team_status);

    void *handle = dlopen(hostfxr_path, RTLD_NOW | RTLD_LOCAL);
    if (handle != NULL) {
        dlclose(handle);
        fprintf(stderr, "FAIL Hardened Runtime loaded different-Team original hostfxr\n");
        return 1;
    }

    const char *error = dlerror();
    if (error == NULL || strstr(error, "different Team IDs") == NULL) {
        fprintf(stderr, "FAIL original hostfxr load failed for an unclassified reason: %s\n",
            error == NULL ? "(no dyld error)" : error);
        return 1;
    }
    printf("PASS Hardened Runtime denied original cross-Team hostfxr\n");
    return 0;
}

int
main(int argc, char **argv)
{
    if (argc == 5 && strcmp(argv[1], "run") == 0)
        return run_host(argv[2], argv[3], argv[4]);
    if (argc == 4 && strcmp(argv[1], "foreign-hostfxr") == 0)
        return run_foreign_hostfxr_control(argv[2], argv[3]);
    fprintf(stderr,
        "usage: %s run DOTNET_ROOT RUNTIMECONFIG ASSEMBLY | "
        "foreign-hostfxr PATH MICROSOFT_TEAM_ID\n",
        argv[0]);
    return 2;
}
