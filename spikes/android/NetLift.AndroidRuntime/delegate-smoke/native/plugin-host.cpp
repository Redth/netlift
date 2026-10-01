#include <dlfcn.h>
#include <dirent.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace
{
using coreclr_initialize_fn = int (*)(
    const char *, const char *, int, const char **, const char **, void **, unsigned int *);
using coreclr_set_error_writer_fn = int (*)(void (*)(const char *));
using coreclr_create_delegate_fn = int (*)(
    void *, unsigned int, const char *, const char *, const char *, void **);
using describe_fn = uint32_t (*)(void *, uint32_t);
using register_fn = uint32_t (*)();
using set_mismatch_fn = uint32_t (*)(uint32_t);
using factory_count_fn = uint32_t (*)();
struct LiftContractDescriptorV1;
using initialize_fn = uint32_t (*)(const LiftContractDescriptorV1 *);
using commit_fn = uint32_t (*)();
using invoke_fn = uint32_t (*)(
    uint32_t, uint32_t, const void *, void *, void *);
using free_fn = uint32_t (*)(void *);

struct LiftContractDescriptorV1
{
    uint32_t struct_size;
    uint32_t descriptor_version;
    uint32_t abi_version;
    uint32_t required_feature_bits;
    uint8_t schema_hash[32];
};

struct LiftBytesView
{
    const uint8_t *data;
    uint32_t length;
};

struct LiftOwnedBytes
{
    uint8_t *data;
    uint32_t length;
};

struct LiftStatus
{
    uint32_t code;
    LiftOwnedBytes message;
};

static_assert(sizeof(LiftContractDescriptorV1) == 48);

void error_writer(const char *message)
{
    std::fprintf(stderr, "CoreCLR: %s\n", message == nullptr ? "(null)" : message);
}

void append_dlls(const std::string &directory, std::vector<std::string> &paths)
{
    DIR *dir = opendir(directory.c_str());
    if (dir == nullptr)
        return;
    while (dirent *entry = readdir(dir))
    {
        std::string name(entry->d_name);
        if (name.size() > 4 && name.compare(name.size() - 4, 4, ".dll") == 0)
            paths.push_back(directory + "/" + name);
    }
    closedir(dir);
}

std::string tpa_list(const std::string &runtime_dir, const std::string &managed_dir)
{
    std::vector<std::string> paths;
    append_dlls(runtime_dir, paths);
    append_dlls(managed_dir, paths);
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
    const char *assembly,
    const char *type,
    const char *method,
    void **delegate)
{
    const int status = create(host, domain, assembly, type, method, delegate);
    std::printf("coreclr_create_delegate(%s.%s)=%d pointer=%p\n", type, method, status, *delegate);
    return status >= 0 && *delegate != nullptr;
}

bool descriptor_matches(
    const LiftContractDescriptorV1 &descriptor,
    const LiftContractDescriptorV1 &expected)
{
    return std::memcmp(&descriptor, &expected, sizeof(descriptor)) == 0;
}
}

int main(int argc, char **argv)
{
    if (argc != 3 && argc != 4)
    {
        std::fprintf(
            stderr,
            "usage: plugin-host <runtime-dir> <managed-dir> "
            "[--mismatch=version|abi|features|hash|"
            "--register-mismatch=version|abi|features|hash]\n");
        return 2;
    }

    const std::string runtime_dir(argv[1]);
    const std::string managed_dir(argv[2]);
    const std::string mismatch_mode = argc == 4 ? argv[3] : "";
    void *coreclr = dlopen((runtime_dir + "/libcoreclr.dylib").c_str(), RTLD_NOW | RTLD_GLOBAL);
    if (coreclr == nullptr)
    {
        std::fprintf(stderr, "dlopen failed: %s\n", dlerror());
        return 3;
    }

    auto initialize = reinterpret_cast<coreclr_initialize_fn>(dlsym(coreclr, "coreclr_initialize"));
    auto set_error_writer =
        reinterpret_cast<coreclr_set_error_writer_fn>(dlsym(coreclr, "coreclr_set_error_writer"));
    auto create =
        reinterpret_cast<coreclr_create_delegate_fn>(dlsym(coreclr, "coreclr_create_delegate"));
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
        "NetLift.AndroidRuntime.StubContract",
        static_cast<int>(names.size()),
        name_ptrs.data(),
        value_ptrs.data(),
        &host,
        &domain);
    std::printf("coreclr_initialize=%d\n", initialized);
    if (initialized < 0)
        return 5;

    void *initialize_delegate = nullptr;
    void *describe_delegate = nullptr;
    void *register_delegate = nullptr;
    void *commit_delegate = nullptr;
    void *invoke_delegate = nullptr;
    void *free_delegate = nullptr;
    void *factory_count_delegate = nullptr;
    void *set_mismatch_delegate = nullptr;
    const bool initialize_resolved = create_delegate(
        create, host, domain, "NetLift.AndroidRuntime.Managed",
        "NetLift.AndroidRuntime.Managed.NetLiftManagedHost", "InitializeV1", &initialize_delegate);
    const bool describe_resolved = create_delegate(
        create, host, domain, "NetLift.AndroidRuntime.StubContract",
        "NetLift.Generated.AndroidContractBootstrap", "DescribeV1", &describe_delegate);
    const bool register_resolved = create_delegate(
        create, host, domain, "NetLift.AndroidRuntime.StubContract",
        "NetLift.Generated.AndroidContractBootstrap", "RegisterV1", &register_delegate);
    const bool commit_resolved = create_delegate(
        create, host, domain, "NetLift.AndroidRuntime.Managed",
        "NetLift.AndroidRuntime.Managed.NetLiftManagedHost", "CommitRoutesV1", &commit_delegate);
    const bool invoke_resolved = create_delegate(
        create, host, domain, "NetLift.AndroidRuntime.Managed",
        "NetLift.AndroidRuntime.Managed.NetLiftManagedHost", "InvokeV1", &invoke_delegate);
    const bool free_resolved = create_delegate(
        create, host, domain, "NetLift.AndroidRuntime.Managed",
        "NetLift.AndroidRuntime.Managed.NetLiftManagedHost", "FreeV1", &free_delegate);
    const bool factory_count_resolved = create_delegate(
        create, host, domain, "NetLift.AndroidRuntime.StubContract",
        "NetLift.Generated.AndroidContractBootstrap", "FactoryCreationCountV1",
        &factory_count_delegate);
    const bool set_mismatch_resolved = create_delegate(
        create, host, domain, "NetLift.AndroidRuntime.StubContract",
        "NetLift.Generated.AndroidContractBootstrap", "SetRegistrationMismatchV1",
        &set_mismatch_delegate);
    if (!initialize_resolved || !describe_resolved || !register_resolved ||
        !commit_resolved || !invoke_resolved || !free_resolved || !factory_count_resolved ||
        !set_mismatch_resolved)
        return 6;

    LiftContractDescriptorV1 short_descriptor{};
    const auto short_capacity_status =
        reinterpret_cast<describe_fn>(describe_delegate)(&short_descriptor, sizeof(short_descriptor) - 1);
    if (short_capacity_status != 7)
    {
        std::fprintf(
            stderr,
            "DescribeV1 accepted short capacity: status=%u\n",
            short_capacity_status);
        return 7;
    }

    LiftContractDescriptorV1 descriptor{};
    const auto describe_status =
        reinterpret_cast<describe_fn>(describe_delegate)(&descriptor, sizeof(descriptor));
    LiftContractDescriptorV1 expected{};
    expected.struct_size = sizeof(expected);
    expected.descriptor_version = 1;
    expected.abi_version = 1;
    expected.required_feature_bits = 1;
    for (size_t index = 0; index < sizeof(expected.schema_hash); ++index)
        expected.schema_hash[index] = static_cast<uint8_t>(index + 1);
    if (mismatch_mode == "--mismatch=version")
        expected.descriptor_version++;
    else if (mismatch_mode == "--mismatch=abi")
        expected.abi_version++;
    else if (mismatch_mode == "--mismatch=features")
        expected.required_feature_bits++;
    else if (mismatch_mode == "--mismatch=hash")
        expected.schema_hash[0] ^= 0xff;
    else if (!mismatch_mode.empty() &&
             mismatch_mode.rfind("--register-mismatch=", 0) != 0)
    {
        std::fprintf(stderr, "Unknown mismatch mode: %s\n", mismatch_mode.c_str());
        return 8;
    }
    const bool descriptor_valid =
        describe_status == 0 && descriptor_matches(descriptor, expected);
    std::printf(
        "DescribeV1 status=%u size=%u version=%u abi=%u features=0x%x valid=%s\n",
        describe_status,
        descriptor.struct_size,
        descriptor.descriptor_version,
        descriptor.abi_version,
        descriptor.required_feature_bits,
        descriptor_valid ? "true" : "false");
    if (!descriptor_valid && !mismatch_mode.empty())
    {
        const auto factory_count = reinterpret_cast<register_fn>(factory_count_delegate)();
        const bool rejected_before_factory = factory_count == 0;
        std::printf(
            "Descriptor mismatch rejected before RegisterV1; factoryCalls=%u\n",
            factory_count);
        return rejected_before_factory ? 0 : 9;
    }
    if (!descriptor_valid)
        return 8;

    if (reinterpret_cast<initialize_fn>(initialize_delegate)(&descriptor) != 0)
    {
        std::fprintf(stderr, "Managed host initialization failed.\n");
        return 10;
    }

    if (mismatch_mode.rfind("--register-mismatch=", 0) == 0)
    {
        const std::string mismatch = mismatch_mode.substr(20);
        const std::array<const char *, 4> names{"version", "abi", "features", "hash"};
        const std::array<uint32_t, 4> kinds{1, 2, 3, 4};
        const std::array<uint32_t, 4> expected_statuses{3, 4, 6, 5};
        size_t case_index = names.size();
        for (size_t index = 0; index < names.size(); ++index)
        {
            if (mismatch == names[index])
            {
                case_index = index;
                break;
            }
        }
        if (case_index == names.size() ||
            reinterpret_cast<set_mismatch_fn>(set_mismatch_delegate)(kinds[case_index]) != 0)
            return 11;

        const auto register_status = reinterpret_cast<register_fn>(register_delegate)();
        const auto factory_count = reinterpret_cast<factory_count_fn>(factory_count_delegate)();
        const auto commit_status = reinterpret_cast<commit_fn>(commit_delegate)();
        const std::array<uint8_t, 1> request{0x4e};
        LiftBytesView input{request.data(), static_cast<uint32_t>(request.size())};
        LiftOwnedBytes output{};
        LiftStatus status{};
        const auto invoke_status = reinterpret_cast<invoke_fn>(invoke_delegate)(
            0x51, 1, &input, &output, &status);
        const bool rejected_before_factory =
            register_status == expected_statuses[case_index] &&
            factory_count == 0 && commit_status == 9 && invoke_status == 2 && status.code == 2;
        std::printf(
            "Register mismatch=%s status=%u factoryCalls=%u commit=%u invoke=%u\n",
            mismatch.c_str(),
            register_status,
            factory_count,
            commit_status,
            invoke_status);
        if (status.message.data != nullptr)
            reinterpret_cast<free_fn>(free_delegate)(status.message.data);
        return rejected_before_factory ? 0 : 12;
    }

    const auto register_status = reinterpret_cast<register_fn>(register_delegate)();
    const auto commit_status = reinterpret_cast<commit_fn>(commit_delegate)();
    std::printf("RegisterV1=%u CommitRoutesV1=%u\n", register_status, commit_status);
    if (register_status != 0 || commit_status != 0)
        return 11;

    const std::array<uint8_t, 4> request{0x4e, 0x4c, 0x01, 0xff};
    LiftBytesView input{request.data(), static_cast<uint32_t>(request.size())};
    LiftOwnedBytes output{};
    LiftStatus status{};
    const auto invoke_status = reinterpret_cast<invoke_fn>(invoke_delegate)(
        0x51, 1, &input, &output, &status);
    const bool round_trip =
        invoke_status == 0 && status.code == 0 && output.length == request.size() &&
        output.data != nullptr && std::memcmp(output.data, request.data(), request.size()) == 0;
    std::printf(
        "InvokeV1 status=%u managedStatus=%u resultBytes=%u echo=%s\n",
        invoke_status,
        status.code,
        output.length,
        round_trip ? "true" : "false");
    if (output.data != nullptr)
        reinterpret_cast<free_fn>(free_delegate)(output.data);
    if (status.message.data != nullptr)
        reinterpret_cast<free_fn>(free_delegate)(status.message.data);
    return round_trip ? 0 : 12;
}
