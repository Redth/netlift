#ifndef FIXTURE_BUILD
#define FIXTURE_BUILD 1
#endif

extern int netlift_fixture_dependency_value(void);

const char *
netlift_fixture_provider(void)
{
    return "fixture.provider.apple-trust-chain";
}

int
netlift_fixture_build(void)
{
    return FIXTURE_BUILD;
}

int
netlift_fixture_dependency(void)
{
    return netlift_fixture_dependency_value();
}
