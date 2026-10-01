using System;
using System.Runtime.InteropServices;

namespace RuntimeProbe;

public static class Entry
{
    [UnmanagedCallersOnly(EntryPoint = "Invoke")]
    public static int Invoke()
    {
        return 202611;
    }
}

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("This FDD component is invoked by the native host.");
    }
}
