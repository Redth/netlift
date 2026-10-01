#include <android/log.h>
#include <stdlib.h>

#define LOG_TAG "AndroidSignerFdChainMarker"

__attribute__((constructor))
static void
fixture_b_initializer(void)
{
    const char *run_id = getenv("NETLIFT_RUN_ID");
    __android_log_print(
        ANDROID_LOG_INFO,
        LOG_TAG,
        "runId=%s EVENT=FIXTURE_INIT marker=B",
        run_id == NULL ? "missing" : run_id);
}

int
fixture_marker_id(void)
{
    return 202;
}
