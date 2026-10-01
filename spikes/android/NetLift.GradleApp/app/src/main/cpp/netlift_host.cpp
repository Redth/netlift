#include <jni.h>
#include <android/log.h>
#include <dlfcn.h>

#include "android_lift_native_abi.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <dirent.h>
#include <mutex>
#include <string>
#include <vector>

namespace
{
constexpr char log_tag[] = "NetLiftCoreCLR";
using coreclr_initialize_fn = int (*)(const char *, const char *, int, const char **, const char **, void **, unsigned int *);
using coreclr_set_error_writer_fn = int (*)(void (*)(const char *));
using coreclr_create_delegate_fn = int (*)(void *, unsigned int, const char *, const char *, const char *, void **);
using runtime_version_major_fn = int32_t (*)();

struct host_runtime_contract
{
    size_t size;
    void *context;
    size_t (*get_runtime_property)(const char *, char *, size_t, void *);
    void *bundle_probe;
    void *pinvoke_override;
    void *external_assembly_probe;
    void *get_native_code_data;
};

void *coreclr_library = nullptr;
void *coreclr_host = nullptr;
unsigned int coreclr_domain = 0;
runtime_version_major_fn runtime_version_major = nullptr;
managed_place_order_fn place_order = nullptr;
std::mutex initialize_mutex;
std::vector<std::string> property_names;
std::vector<std::string> property_values;
std::vector<const char *> property_name_ptrs;
std::vector<const char *> property_value_ptrs;
std::string corelib_directory;
host_runtime_contract runtime_contract{};

void log_error(const char *message, int code)
{
    __android_log_print(ANDROID_LOG_ERROR, log_tag, "%s (0x%x)", message, code);
}

void coreclr_error_writer(const char *message)
{
    __android_log_print(ANDROID_LOG_ERROR, log_tag, "CLR: %s", message == nullptr ? "(null)" : message);
}

size_t get_runtime_property(const char *key, char *buffer, size_t buffer_size, void *context)
{
    __android_log_print(ANDROID_LOG_INFO, log_tag, "Host contract property requested: %s", key);
    if (std::strcmp(key, "SYSTEM_CORELIB_DIRECTORY") != 0)
        return static_cast<size_t>(-1);

    const auto *directory = static_cast<const std::string *>(context);
    const size_t required_size = directory->size() + 1;
    if (buffer_size < required_size)
        return required_size;
    std::memcpy(buffer, directory->c_str(), required_size);
    __android_log_print(ANDROID_LOG_INFO, log_tag, "Providing system core library directory: %s", directory->c_str());
    return required_size;
}

std::string get_tpa_list(const std::string &directory)
{
    DIR *dir = opendir(directory.c_str());
    if (dir == nullptr)
        return {};

    std::vector<std::string> paths;
    while (dirent *entry = readdir(dir))
    {
        std::string name(entry->d_name);
        if (name.size() > 4 && name.compare(name.size() - 4, 4, ".dll") == 0)
            paths.push_back(directory + "/" + name);
    }
    closedir(dir);

    std::sort(paths.begin(), paths.end());
    std::stable_sort(paths.begin(), paths.end(), [](const auto &left, const auto &right) {
        constexpr char corelib[] = "/System.Private.CoreLib.dll";
        constexpr size_t corelib_length = sizeof(corelib) - 1;
        const bool left_is_corelib =
            left.size() >= corelib_length &&
            left.compare(left.size() - corelib_length, corelib_length, corelib) == 0;
        const bool right_is_corelib =
            right.size() >= corelib_length &&
            right.compare(right.size() - corelib_length, corelib_length, corelib) == 0;
        return left_is_corelib && !right_is_corelib;
    });

    std::string result;
    for (const auto &path : paths)
    {
        if (!result.empty())
            result += ':';
        result += path;
    }
    return result;
}

void set_properties(const std::string &dotnet_dir, const std::string &native_library_dir)
{
    property_names = {
        "HOST_RUNTIME_CONTRACT",
        "TRUSTED_PLATFORM_ASSEMBLIES",
        "APP_PATHS",
        "APP_NI_PATHS",
        "NATIVE_DLL_SEARCH_DIRECTORIES",
        "APP_CONTEXT_BASE_DIRECTORY",
        "RUNTIME_IDENTIFIER",
    };
    char runtime_contract_pointer[2 + sizeof(void *) * 2 + 1]{};
    std::snprintf(runtime_contract_pointer, sizeof(runtime_contract_pointer), "%p", &runtime_contract);
    property_values = {
        runtime_contract_pointer,
        get_tpa_list(dotnet_dir),
        dotnet_dir,
        dotnet_dir,
        native_library_dir,
        dotnet_dir + "/",
        "android-arm64",
    };
    property_name_ptrs.clear();
    property_value_ptrs.clear();
    for (const auto &value : property_names)
        property_name_ptrs.push_back(value.c_str());
    for (const auto &value : property_values)
        property_value_ptrs.push_back(value.c_str());
}

}

extern "C" JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *)
{
    NetLift_AbiSetJavaVm(vm);
    return JNI_VERSION_1_6;
}

extern "C" JNIEXPORT jint JNICALL
Java_com_netlift_androidhost_MainActivity_nativeInitialize(
    JNIEnv *env,
    jobject,
    jbyteArray files_dir_utf8,
    jbyteArray native_library_dir_utf8)
{
    std::lock_guard<std::mutex> lock(initialize_mutex);
    if (place_order != nullptr)
        return 0;

    auto get_byte_array_string = [env](jbyteArray value) {
        const jsize length = env->GetArrayLength(value);
        std::string result(static_cast<size_t>(length), '\0');
        if (length != 0)
            env->GetByteArrayRegion(value, 0, length, reinterpret_cast<jbyte *>(result.data()));
        return result;
    };
    std::string dotnet_dir = get_byte_array_string(files_dir_utf8) + "/dotnet";
    std::string native_dir = get_byte_array_string(native_library_dir_utf8);
    std::string coreclr_path = native_dir + "/libcoreclr.so";
    coreclr_library = dlopen(coreclr_path.c_str(), RTLD_NOW | RTLD_GLOBAL);
    if (coreclr_library == nullptr)
    {
        __android_log_print(ANDROID_LOG_ERROR, log_tag, "dlopen failed: %s", dlerror());
        return -2;
    }

    auto initialize = reinterpret_cast<coreclr_initialize_fn>(dlsym(coreclr_library, "coreclr_initialize"));
    auto set_error_writer = reinterpret_cast<coreclr_set_error_writer_fn>(dlsym(coreclr_library, "coreclr_set_error_writer"));
    auto create_delegate = reinterpret_cast<coreclr_create_delegate_fn>(dlsym(coreclr_library, "coreclr_create_delegate"));
    if (initialize == nullptr || set_error_writer == nullptr || create_delegate == nullptr)
    {
        log_error("CoreCLR hosting exports are missing", -3);
        return -3;
    }

    set_error_writer(coreclr_error_writer);
    corelib_directory = dotnet_dir;
    runtime_contract = {
        sizeof(host_runtime_contract),
        &corelib_directory,
        get_runtime_property,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
    };
    set_properties(dotnet_dir, native_dir);
    constexpr size_t tpa_property_index = 1;
    if (property_values.size() <= tpa_property_index || property_values[tpa_property_index].empty())
    {
        log_error("No managed assemblies were found in the staged runtime directory", -5);
        return -5;
    }
    __android_log_print(ANDROID_LOG_INFO, log_tag, "CoreCLR TPA list has %zu characters", property_values[tpa_property_index].size());
    int result = initialize(
        dotnet_dir.c_str(),
        "NetLift.GradleHost",
        static_cast<int>(property_name_ptrs.size()),
        property_name_ptrs.data(),
        property_value_ptrs.data(),
        &coreclr_host,
        &coreclr_domain);
    if (result < 0)
    {
        log_error("coreclr_initialize failed", result);
        return result;
    }

    void *delegate = nullptr;
    result = create_delegate(
        coreclr_host,
        coreclr_domain,
        "NetLift.Managed",
        "NetLift.Managed.AndroidExports",
        "GetRuntimeVersionMajor",
        &delegate);
    if (result < 0 || delegate == nullptr)
    {
        log_error("coreclr_create_delegate failed for runtime version probe", result);
        return result < 0 ? result : -4;
    }
    runtime_version_major = reinterpret_cast<runtime_version_major_fn>(delegate);

    delegate = nullptr;
    result = create_delegate(
        coreclr_host,
        coreclr_domain,
        "NetLift.Managed",
        "NetLift.Managed.AndroidExports",
        "PlaceSampleOrder",
        &delegate);
    if (result < 0 || delegate == nullptr)
    {
        log_error("coreclr_create_delegate failed", result);
        return result < 0 ? result : -4;
    }

    place_order = reinterpret_cast<managed_place_order_fn>(delegate);
    NetLift_AbiSetPlaceOrderDelegate(place_order);
    __android_log_print(ANDROID_LOG_INFO, log_tag, "CoreCLR and managed delegate initialized");
    return 0;
}

extern "C" JNIEXPORT jint JNICALL
Java_com_netlift_androidhost_MainActivity_nativeGetRuntimeVersionMajor(JNIEnv *, jobject)
{
    if (runtime_version_major == nullptr)
        return -1;
    return static_cast<jint>(runtime_version_major());
}
