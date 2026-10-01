#include <android/log.h>
#include <unistd.h>

#define LOG_TAG "AndroidFirstNativeHost"

__attribute__((constructor))
static void
bootstrap_dependency_initializer(void)
{
    __android_log_print(
        ANDROID_LOG_INFO,
        LOG_TAG,
        "pid=%d EVENT=DEPENDENCY_INITIALIZER",
        getpid());
}

int
bootstrap_dependency_value(void)
{
    return 73;
}
