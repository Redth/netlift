namespace NetLift.HotReloadTarget;

public static class Probe
{
    private static int _calls;

    public static int Observe()
    {
        _calls++;
        return 100 + _calls;
    }
}
