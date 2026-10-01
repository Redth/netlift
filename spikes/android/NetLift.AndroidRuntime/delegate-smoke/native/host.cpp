#include <dlfcn.h>
#include <dirent.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace
{
using coreclr_initialize_fn = int (*)(
    const char *,
    const char *,
    int,
    const char **,
    const char **,
    void **,
    unsigned int *);
using coreclr_set_error_writer_fn = int (*)(void (*)(const char *));
using coreclr_create_delegate_fn = int (*)(
    void *,
    unsigned int,
    const char *,
    const char *,
    const char *,
    void **);
using describe_fn = uint32_t (*)(void *, uint32_t);
using register_fn = uint32_t (*)();

struct LiftContractDescriptorV1
{
    uint32_t struct_size;
    uint32_t descriptor_version;
    uint32_t abi_version;
    uint32_t required_feature_bits;
    uint8_t schema_hash[32];
};

static_assert(sizeof(LiftContractDescriptorV1) == 48);

void error_writer(const char *message)
{
    std::fprintf(stderr, "CoreCLR: %s\n", message == nullptr ? "(null)" : message);
}

std::string tpa_list(const std::string &runtime_dir, const std::string &managed_dir)
{
    DIR *dir = opendir(runtime_dir.c_str());
    if (dir == nullptr)
        return {};

    std::vector<std::string> paths;
    while (dirent *entry = readdir(dir))
    {
        std::string name(entry->d_name);
        if (name.size() > 4 && name.compare(name.size() - 4, 4, ".dll") == 0)
            paths.push_back(runtime_dir + "/" + name);
    }
    closedir(dir);
    paths.push_back(managed_dir + "/NetLift.AndroidRuntime.DelegateSmoke.dll");
    std::sort(paths.begin(), paths.end());

    std::string result;
    for (const auto &path : paths)
    {
        if (!result.empty())
            result += ':';
        result += path;
    }
    return result;
}

bool create_delegate(
    coreclr_create_delegate_fn create,
    void *host,
    unsigned int domain,
    const char *method,
    void **delegate)
{
    const int status = create(
        host,
        domain,
        "NetLift.AndroidRuntime.DelegateSmoke",
        "NetLift.AndroidRuntime.DelegateSmoke.EntryPoints",
        method,
        delegate);
    std::printf("coreclr_create_delegate(%s)=%d pointer=%p\n", method, status, *delegate);
    return status >= 0 && *delegate != nullptr;
}

bool matches_expected(const LiftContractDescriptorV1 &descriptor)
{
    if (descriptor.struct_size != sizeof(descriptor) ||
        descriptor.descriptor_version != 1 ||
        descriptor.abi_version != 1 ||
        descriptor.required_feature_bits != 0x6)
        return false;
    for (size_t index = 0; index < sizeof(descriptor.schema_hash); ++index)
    {
        if (descriptor.schema_hash[index] != index)
            return false;
    }
    return true;
}
}

int main(int argc, char **argv)
{
    if (argc != 3)
    {
        std::fprintf(stderr, "usage: host <runtime-dir> <managed-dir>\n");
        return 2;
    }

    const std::string runtime_dir(argv[1]);
    const std::string managed_dir(argv[2]);
    const std::string coreclr_path = runtime_dir + "/libcoreclr.dylib";
    void *coreclr = dlopen(coreclr_path.c_str(), RTLD_NOW | RTLD_GLOBAL);
    if (coreclr == nullptr)
    {
        std::fprintf(stderr, "dlopen failed: %s\n", dlerror());
        return 3;
    }

    auto initialize = reinterpret_cast<coreclr_initialize_fn>(
        dlsym(coreclr, "coreclr_initialize"));
    auto set_error_writer = reinterpret_cast<coreclr_set_error_writer_fn>(
        dlsym(coreclr, "coreclr_set_error_writer"));
    auto create = reinterpret_cast<coreclr_create_delegate_fn>(
        dlsym(coreclr, "coreclr_create_delegate"));
    if (initialize == nullptr || set_error_writer == nullptr || create == nullptr)
    {
        std::fprintf(stderr, "Required CoreCLR hosting exports are missing.\n");
        return 4;
    }

    set_error_writer(error_writer);
    const std::string trusted_assemblies = tpa_list(runtime_dir, managed_dir);
    std::array<std::string, 5> names{
        "TRUSTED_PLATFORM_ASSEMBLIES",
        "APP_PATHS",
        "APP_NI_PATHS",
        "NATIVE_DLL_SEARCH_DIRECTORIES",
        "APP_CONTEXT_BASE_DIRECTORY",
    };
    std::array<std::string, 5> values{
        trusted_assemblies,
        managed_dir,
        managed_dir,
        runtime_dir,
        managed_dir + "/",
    };
    std::array<const char *, 5> name_ptrs{};
    std::array<const char *, 5> value_ptrs{};
    for (size_t index = 0; index < names.size(); ++index)
    {
        name_ptrs[index] = names[index].c_str();
        value_ptrs[index] = values[index].c_str();
    }

    void *host = nullptr;
    unsigned int domain = 0;
    const int initialized = initialize(
        managed_dir.c_str(),
        "NetLift.AndroidRuntime.DelegateSmoke",
        static_cast<int>(names.size()),
        name_ptrs.data(),
        value_ptrs.data(),
        &host,
        &domain);
    std::printf("coreclr_initialize=%d\n", initialized);
    if (initialized < 0)
        return 5;

    void *descriptor_delegate = nullptr;
    void *ordinary_descriptor_delegate = nullptr;
    void *register_delegate = nullptr;
    const bool uco_resolved = create_delegate(
        create, host, domain, "DescribeV1", &descriptor_delegate);
    const bool ordinary_resolved = create_delegate(
        create, host, domain, "DescribeV1Ordinary", &ordinary_descriptor_delegate);
    const bool register_resolved = create_delegate(
        create, host, domain, "RegisterV1", &register_delegate);

    bool uco_valid = false;
    if (uco_resolved)
    {
        LiftContractDescriptorV1 descriptor{};
        const auto status = reinterpret_cast<describe_fn>(descriptor_delegate)(
            &descriptor,
            sizeof(descriptor));
        uco_valid = status == 0 && matches_expected(descriptor);
        std::printf(
            "UCO DescribeV1 status=%u struct_size=%u descriptor_version=%u abi=%u features=0x%x valid=%s\n",
            status,
            descriptor.struct_size,
            descriptor.descriptor_version,
            descriptor.abi_version,
            descriptor.required_feature_bits,
            uco_valid ? "true" : "false");
    }

    bool ordinary_valid = false;
    if (ordinary_resolved)
    {
        LiftContractDescriptorV1 descriptor{};
        const auto status = reinterpret_cast<describe_fn>(ordinary_descriptor_delegate)(
            &descriptor,
            sizeof(descriptor));
        ordinary_valid = status == 0 && matches_expected(descriptor);
        std::printf(
            "ordinary DescribeV1 status=%u struct_size=%u descriptor_version=%u abi=%u features=0x%x valid=%s\n",
            status,
            descriptor.struct_size,
            descriptor.descriptor_version,
            descriptor.abi_version,
            descriptor.required_feature_bits,
            ordinary_valid ? "true" : "false");
    }

    bool register_valid = false;
    if (register_resolved)
    {
        const auto status = reinterpret_cast<register_fn>(register_delegate)();
        register_valid = status == 0x51;
        std::printf("RegisterV1 status=0x%x valid=%s\n",
                    status,
                    register_valid ? "true" : "false");
    }

    return uco_valid && ordinary_valid && register_valid ? 0 : 6;
}
