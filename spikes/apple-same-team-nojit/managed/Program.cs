using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RuntimeProbe;

public static class Entry
{
    [UnmanagedCallersOnly(EntryPoint = "Invoke")]
    public static int Invoke()
    {
        int[] values = [202, 611];
        return Fold(values);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Fold(int[] values)
    {
        int result = 0;
        foreach (int value in values)
            result = result * 1000 + value;
        return result;
    }
}

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("This component is invoked by the native host.");
    }
}
