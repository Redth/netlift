#ifndef FIXTURE_BUILD
#define FIXTURE_BUILD 1
#endif

extern int netlift_module_dependency_value(void);

const char *
netlift_module_provider_v1(void)
{
    return "fixture.provider.v1";
}

int
netlift_module_build(void)
{
    return FIXTURE_BUILD;
}

int
netlift_module_dependency_probe(void)
{
    return netlift_module_dependency_value();
}
