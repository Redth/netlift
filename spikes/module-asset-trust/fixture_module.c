#ifndef FIXTURE_PROVIDER
#define FIXTURE_PROVIDER "fixture.provider.v1"
#endif

#ifndef FIXTURE_BUILD
#define FIXTURE_BUILD 1
#endif

const char *
netlift_module_provider_v1(void)
{
    return FIXTURE_PROVIDER;
}

int
netlift_module_build(void)
{
    return FIXTURE_BUILD;
}
