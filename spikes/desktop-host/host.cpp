#include <coreclr_delegates.h>
#include <hostfxr.h>
#include <nethost.h>

#include <condition_variable>
#include <cstdint>
#include <dlfcn.h>
#include <filesystem>
#include <iostream>
#include <mutex>

namespace {
std::mutex gate;
std::condition_variable completed;
bool finished = false;
int result = -1;

int32_t confirm(int32_t title_length)
{
    return title_length == 3 ? 1 : 0;
}

int32_t on_complete(int32_t value)
{
    {
        std::lock_guard<std::mutex> lock(gate);
        result = value;
        finished = true;
    }
    completed.notify_one();
    return 0;
}

template <typename T> T load_symbol(void *library, const char *name)
{
    return reinterpret_cast<T>(dlsym(library, name));
}
}

int main(int argc, char **argv)
{
    if (argc != 2) {
        std::cerr << "Usage: host <managed-output-directory>\n";
        return 1;
    }

    char_t hostfxr_path[4096];
    size_t path_size = sizeof(hostfxr_path) / sizeof(char_t);
    if (get_hostfxr_path(hostfxr_path, &path_size, nullptr) != 0) {
        std::cerr << "Could not locate hostfxr.\n";
        return 2;
    }
    void *library = dlopen(hostfxr_path, RTLD_NOW | RTLD_LOCAL);
    if (library == nullptr) {
        std::cerr << "Could not load hostfxr: " << dlerror() << '\n';
        return 3;
    }

    const auto init = load_symbol<hostfxr_initialize_for_runtime_config_fn>(
        library, "hostfxr_initialize_for_runtime_config");
    const auto get_delegate = load_symbol<hostfxr_get_runtime_delegate_fn>(
        library, "hostfxr_get_runtime_delegate");
    const auto close = load_symbol<hostfxr_close_fn>(library, "hostfxr_close");
    if (init == nullptr || get_delegate == nullptr || close == nullptr) {
        std::cerr << "Missing hostfxr exports.\n";
        return 4;
    }

    const auto output = std::filesystem::path(argv[1]);
    const auto config = output / "NetLift.DesktopBridge.runtimeconfig.json";
    const auto assembly = output / "NetLift.DesktopBridge.dll";
    hostfxr_handle context = nullptr;
    if (init(config.c_str(), nullptr, &context) != 0 || context == nullptr) {
        std::cerr << "CoreCLR host initialization failed.\n";
        return 5;
    }

    void *delegate = nullptr;
    const int delegate_status = get_delegate(
        context, hdt_load_assembly_and_get_function_pointer, &delegate);
    close(context);
    if (delegate_status != 0 || delegate == nullptr) {
        std::cerr << "Could not obtain managed assembly loader.\n";
        return 6;
    }

    void *entry = nullptr;
    const auto load_assembly =
        reinterpret_cast<load_assembly_and_get_function_pointer_fn>(delegate);
    const int load_status = load_assembly(
        assembly.c_str(), "NetLift.Spikes.Desktop.HostBridge, NetLift.DesktopBridge",
        "Start", UNMANAGEDCALLERSONLY_METHOD, nullptr, &entry);
    if (load_status != 0 || entry == nullptr) {
        std::cerr << "Could not find managed bridge entry point.\n";
        return 7;
    }

    const auto start = reinterpret_cast<int32_t (*)(int32_t,
        int32_t (*)(int32_t), int32_t (*)(int32_t))>(entry);
    if (start(2, confirm, on_complete) != 0) {
        std::cerr << "Bridge start failed.\n";
        return 8;
    }

    {
        std::unique_lock<std::mutex> lock(gate);
        if (!completed.wait_for(lock, std::chrono::seconds(10),
                                [] { return finished; })) {
            std::cerr << "Timed out waiting for managed completion.\n";
            return 9;
        }
    }
    if (result != 2) {
        std::cerr << "Managed operation failed: " << result << '\n';
        return 10;
    }

    std::cout << "Native macOS host -> CoreCLR -> native callback -> "
                 "CoreCLR -> native completion passed.\n";
    return 0;
}
