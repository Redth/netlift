#include <coreclr_delegates.h>
#include <hostfxr.h>
#include <nethost.h>

#include <cstdint>
#include <cstdlib>
#include <dlfcn.h>
#include <filesystem>
#include <iostream>

namespace {
template <typename T> T load_symbol(void *library, const char *name)
{
    return reinterpret_cast<T>(dlsym(library, name));
}

template <typename T>
T load_managed_entry(load_assembly_and_get_function_pointer_fn load_assembly,
                     const std::filesystem::path &assembly,
                     const char *method)
{
    void *entry = nullptr;
    const int status = load_assembly(
        assembly.c_str(),
        "NetLift.HotReload.NativeBridge.EntryPoints, HostBridge",
        method,
        UNMANAGEDCALLERSONLY_METHOD,
        nullptr,
        &entry);
    if (status != 0 || entry == nullptr) {
        std::cerr << "Could not load managed entry point " << method
                  << " (status " << status << ").\n";
        return nullptr;
    }
    return reinterpret_cast<T>(entry);
}
}

int main(int argc, char **argv)
{
    if (argc != 3) {
        std::cerr << "Usage: native-hot-reload-host <bridge-output> <target-assembly>\n";
        return 1;
    }
    if (setenv("DOTNET_MODIFIABLE_ASSEMBLIES", "debug", 1) != 0) {
        std::cerr << "Could not enable modifiable assemblies for this host process.\n";
        return 2;
    }

    char_t hostfxr_path[4096];
    size_t path_size = sizeof(hostfxr_path) / sizeof(char_t);
    if (get_hostfxr_path(hostfxr_path, &path_size, nullptr) != 0) {
        std::cerr << "Could not locate hostfxr.\n";
        return 3;
    }
    void *library = dlopen(hostfxr_path, RTLD_NOW | RTLD_LOCAL);
    if (library == nullptr) {
        std::cerr << "Could not load hostfxr: " << dlerror() << '\n';
        return 4;
    }

    const auto init = load_symbol<hostfxr_initialize_for_runtime_config_fn>(
        library, "hostfxr_initialize_for_runtime_config");
    const auto get_delegate = load_symbol<hostfxr_get_runtime_delegate_fn>(
        library, "hostfxr_get_runtime_delegate");
    const auto close = load_symbol<hostfxr_close_fn>(library, "hostfxr_close");
    if (init == nullptr || get_delegate == nullptr || close == nullptr) {
        std::cerr << "Missing hostfxr exports.\n";
        return 5;
    }

    const std::filesystem::path bridge_output(argv[1]);
    const auto runtime_config = bridge_output / "HostBridge.runtimeconfig.json";
    const auto bridge_assembly = bridge_output / "HostBridge.dll";
    hostfxr_handle context = nullptr;
    if (init(runtime_config.c_str(), nullptr, &context) != 0 || context == nullptr) {
        std::cerr << "CoreCLR host initialization failed.\n";
        return 6;
    }

    void *delegate = nullptr;
    const int delegate_status = get_delegate(
        context, hdt_load_assembly_and_get_function_pointer, &delegate);
    close(context);
    if (delegate_status != 0 || delegate == nullptr) {
        std::cerr << "Could not obtain managed assembly loader.\n";
        return 7;
    }
    const auto load_assembly =
        reinterpret_cast<load_assembly_and_get_function_pointer_fn>(delegate);
    using supported_fn = int32_t (*)();
    using initialize_fn = int32_t (*)(const char *);
    using observe_fn = int32_t (*)();
    using apply_delta_fn = int32_t (*)();
    const auto supported = load_managed_entry<supported_fn>(
        load_assembly, bridge_assembly, "MetadataUpdaterSupported");
    const auto initialize = load_managed_entry<initialize_fn>(
        load_assembly, bridge_assembly, "Initialize");
    const auto observe = load_managed_entry<observe_fn>(
        load_assembly, bridge_assembly, "Observe");
    const auto apply_delta = load_managed_entry<apply_delta_fn>(
        load_assembly, bridge_assembly, "ApplyDelta");
    if (supported == nullptr || initialize == nullptr || observe == nullptr ||
        apply_delta == nullptr) {
        return 8;
    }

    const int32_t updates_supported = supported();
    std::cout << "METADATA_UPDATER_SUPPORTED="
              << (updates_supported == 1 ? "true" : "false") << std::endl;
    if (updates_supported != 1) {
        return 9;
    }

    const auto target_assembly = std::filesystem::absolute(argv[2]);
    if (initialize(target_assembly.c_str()) != 0) {
        return 10;
    }
    const int32_t before_first = observe();
    const int32_t before_second = observe();
    std::cout << "OBSERVE_BEFORE=" << before_first << ',' << before_second << std::endl;
    if (before_first != 101 || before_second != 102) {
        std::cerr << "Unexpected baseline method behavior.\n";
        return 11;
    }

    if (apply_delta() != 0) {
        return 12;
    }
    const int32_t after_delta = observe();
    std::cout << "OBSERVE_AFTER=" << after_delta << std::endl;
    if (after_delta != 203) {
        std::cerr << "Delta did not update behavior while preserving static call count.\n";
        return 13;
    }
    std::cout << "PASS native-hosted method-body delta preserved managed state."
              << std::endl;
    return 0;
}
