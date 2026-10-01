#include "CoreCLRHostABI.h"

#include <coreclr_delegates.h>
#include <hostfxr.h>
#include <nethost.h>

#include <dlfcn.h>
#include <cstdint>
#include <cstring>
#include <exception>
#include <filesystem>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_set>

namespace {
struct RuntimeState {
    std::mutex gate;
    void *hostfxr_library = nullptr;
    load_assembly_and_get_function_pointer_fn load_assembly = nullptr;
    std::string runtime_config_path;
    std::unordered_set<netlift_coreclr_host> active_hosts;
    netlift_coreclr_host next_host_id = 1;
};

RuntimeState runtime;

void write_error(char *buffer, size_t capacity, std::string_view message)
{
    if (buffer == nullptr || capacity == 0)
        return;

    constexpr char replacement[] = "\xEF\xBF\xBD";
    const auto output_capacity = capacity - 1;
    size_t output_length = 0;
    size_t index = 0;
    const auto *bytes = reinterpret_cast<const unsigned char *>(message.data());

    while (index < message.size() && output_length < output_capacity) {
        const auto first = bytes[index];
        size_t sequence_length = 0;
        if (first <= 0x7f)
            sequence_length = 1;
        else if (first >= 0xc2 && first <= 0xdf)
            sequence_length = 2;
        else if (first >= 0xe0 && first <= 0xef)
            sequence_length = 3;
        else if (first >= 0xf0 && first <= 0xf4)
            sequence_length = 4;

        bool valid = sequence_length != 0 &&
            sequence_length <= message.size() - index;
        if (valid && sequence_length > 1) {
            for (size_t offset = 1; offset < sequence_length; ++offset) {
                if ((bytes[index + offset] & 0xc0) != 0x80) {
                    valid = false;
                    break;
                }
            }
            if (valid &&
                ((first == 0xe0 && bytes[index + 1] < 0xa0) ||
                 (first == 0xed && bytes[index + 1] >= 0xa0) ||
                 (first == 0xf0 && bytes[index + 1] < 0x90) ||
                 (first == 0xf4 && bytes[index + 1] > 0x8f)))
                valid = false;
        }

        if (valid && first != 0) {
            if (sequence_length > output_capacity - output_length)
                break;
            std::memcpy(buffer + output_length, bytes + index, sequence_length);
            output_length += sequence_length;
            index += sequence_length;
            continue;
        }

        if (output_capacity - output_length >= sizeof(replacement) - 1) {
            std::memcpy(
                buffer + output_length, replacement, sizeof(replacement) - 1);
            output_length += sizeof(replacement) - 1;
        } else {
            buffer[output_length++] = '?';
        }
        index += valid ? sequence_length : 1;
    }
    buffer[output_length] = '\0';
}

template <typename T>
T load_symbol(void *library, const char *name)
{
    return reinterpret_cast<T>(dlsym(library, name));
}

int open_runtime(const std::string &runtime_config_path, char *error, size_t capacity)
{
    if (runtime.load_assembly != nullptr) {
        if (runtime.runtime_config_path != runtime_config_path) {
            write_error(
                error, capacity,
                "CoreCLR is already initialized from a different runtime config in this process.");
            return 1;
        }
        return 0;
    }
    if (runtime.hostfxr_library != nullptr) {
        write_error(error, capacity, "hostfxr initialization previously failed in this process.");
        return 1;
    }

    char_t hostfxr_path[4096];
    size_t path_size = sizeof(hostfxr_path) / sizeof(char_t);
    if (get_hostfxr_path(hostfxr_path, &path_size, nullptr) != 0) {
        write_error(error, capacity, "nethost could not locate hostfxr.");
        return 1;
    }

    void *library = dlopen(hostfxr_path, RTLD_NOW | RTLD_LOCAL);
    if (library == nullptr) {
        const char *detail = dlerror();
        write_error(
            error, capacity,
            detail == nullptr ? "Could not load hostfxr." : detail);
        return 1;
    }

    const auto initialize =
        load_symbol<hostfxr_initialize_for_runtime_config_fn>(
            library, "hostfxr_initialize_for_runtime_config");
    const auto get_delegate =
        load_symbol<hostfxr_get_runtime_delegate_fn>(
            library, "hostfxr_get_runtime_delegate");
    const auto close =
        load_symbol<hostfxr_close_fn>(library, "hostfxr_close");
    if (initialize == nullptr || get_delegate == nullptr || close == nullptr) {
        dlclose(library);
        write_error(error, capacity, "hostfxr is missing a required hosting export.");
        return 1;
    }

    hostfxr_handle context = nullptr;
    const int initialize_status = initialize(
        runtime_config_path.c_str(), nullptr, &context);
    if (initialize_status != 0 || context == nullptr) {
        if (context != nullptr)
            close(context);
        dlclose(library);
        write_error(
            error, capacity,
            "hostfxr_initialize_for_runtime_config failed.");
        return 1;
    }

    void *delegate = nullptr;
    const int delegate_status = get_delegate(
        context, hdt_load_assembly_and_get_function_pointer, &delegate);
    close(context);
    if (delegate_status != 0 || delegate == nullptr) {
        runtime.hostfxr_library = library;
        write_error(
            error, capacity,
            "hostfxr_get_runtime_delegate could not obtain the assembly loader.");
        return 1;
    }

    runtime.hostfxr_library = library;
    runtime.load_assembly =
        reinterpret_cast<load_assembly_and_get_function_pointer_fn>(delegate);
    runtime.runtime_config_path = runtime_config_path;
    return 0;
}
}

extern "C" int netlift_coreclr_host_open(
    const char *runtime_config_path,
    netlift_coreclr_host *host,
    char *error_buffer,
    size_t error_capacity)
{
    if (error_buffer != nullptr && error_capacity > 0)
        error_buffer[0] = '\0';
    if (runtime_config_path == nullptr || host == nullptr) {
        write_error(error_buffer, error_capacity, "Runtime config path and host output are required.");
        return 1;
    }
    *host = 0;

    try {
        const auto normalized_path =
            std::filesystem::absolute(runtime_config_path).lexically_normal().string();
        std::lock_guard<std::mutex> lock(runtime.gate);
        if (open_runtime(normalized_path, error_buffer, error_capacity) != 0)
            return 1;

        if (runtime.next_host_id == 0) {
            write_error(error_buffer, error_capacity, "CoreCLR host handle identifiers are exhausted.");
            return 1;
        }
        const auto identifier = runtime.next_host_id;
        if (!runtime.active_hosts.insert(identifier).second) {
            write_error(error_buffer, error_capacity, "CoreCLR host handle identifier was already issued.");
            return 1;
        }
        runtime.next_host_id = identifier + 1;
        *host = identifier;
        return 0;
    } catch (const std::exception &exception) {
        write_error(error_buffer, error_capacity, exception.what());
        return 1;
    } catch (...) {
        write_error(error_buffer, error_capacity, "CoreCLR host initialization failed unexpectedly.");
        return 1;
    }
}

extern "C" int netlift_coreclr_host_load_unmanaged_callers_only(
    netlift_coreclr_host host,
    const char *assembly_path,
    const char *type_name,
    const char *method_name,
    void **entry_point,
    char *error_buffer,
    size_t error_capacity)
{
    if (error_buffer != nullptr && error_capacity > 0)
        error_buffer[0] = '\0';
    if (host == 0 ||
        assembly_path == nullptr || type_name == nullptr ||
        method_name == nullptr || entry_point == nullptr) {
        write_error(
            error_buffer, error_capacity,
            "Host handle, assembly, type, method, and entry-point output are required.");
        return 1;
    }
    *entry_point = nullptr;

    try {
        const auto path = std::filesystem::absolute(assembly_path).lexically_normal();
        std::lock_guard<std::mutex> lock(runtime.gate);
        if (runtime.active_hosts.find(host) == runtime.active_hosts.end()) {
            write_error(error_buffer, error_capacity, "CoreCLR host handle is stale or invalid.");
            return 1;
        }
        if (runtime.load_assembly == nullptr) {
            write_error(error_buffer, error_capacity, "CoreCLR has not been initialized.");
            return 1;
        }

        const int status = runtime.load_assembly(
            path.c_str(),
            type_name,
            method_name,
            UNMANAGEDCALLERSONLY_METHOD,
            nullptr,
            entry_point);
        if (status != 0 || *entry_point == nullptr) {
            write_error(
                error_buffer, error_capacity,
                "load_assembly_and_get_function_pointer could not resolve the requested method.");
            return 1;
        }
        return 0;
    } catch (const std::exception &exception) {
        write_error(error_buffer, error_capacity, exception.what());
        return 1;
    } catch (...) {
        write_error(error_buffer, error_capacity, "Managed entry-point resolution failed unexpectedly.");
        return 1;
    }
}

extern "C" int netlift_coreclr_host_release(
    netlift_coreclr_host host,
    char *error_buffer,
    size_t error_capacity)
{
    if (error_buffer != nullptr && error_capacity > 0)
        error_buffer[0] = '\0';
    if (host == 0) {
        write_error(error_buffer, error_capacity, "CoreCLR host handle is invalid.");
        return 1;
    }

    try {
        std::lock_guard<std::mutex> lock(runtime.gate);
        if (runtime.active_hosts.erase(host) == 0) {
            write_error(error_buffer, error_capacity, "CoreCLR host handle is stale or invalid.");
            return 1;
        }
        return 0;
    } catch (const std::exception &exception) {
        write_error(error_buffer, error_capacity, exception.what());
        return 1;
    } catch (...) {
        write_error(error_buffer, error_capacity, "CoreCLR host release failed unexpectedly.");
        return 1;
    }
}
