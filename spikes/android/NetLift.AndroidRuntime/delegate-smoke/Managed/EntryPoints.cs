using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NetLift.AndroidRuntime.DelegateSmoke;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftContractDescriptorV1
{
    public uint StructSize;
    public uint DescriptorVersion;
    public uint AbiVersion;
    public uint RequiredFeatureBits;
    public fixed byte SchemaHash[32];
}

public static unsafe class EntryPoints
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint DescribeV1(LiftContractDescriptorV1* output, uint outputCapacity)
    {
        if (output is null)
            return 1;
        if (outputCapacity < (uint)sizeof(LiftContractDescriptorV1))
            return 7;

        output->StructSize = (uint)sizeof(LiftContractDescriptorV1);
        output->DescriptorVersion = 1;
        output->AbiVersion = 1;
        output->RequiredFeatureBits = 0x6;
        for (var index = 0; index < 32; index++)
            output->SchemaHash[index] = (byte)index;
        return 0;
    }

    public static uint DescribeV1Ordinary(LiftContractDescriptorV1* output, uint outputCapacity)
    {
        if (output is null)
            return 1;
        if (outputCapacity < (uint)sizeof(LiftContractDescriptorV1))
            return 7;

        output->StructSize = (uint)sizeof(LiftContractDescriptorV1);
        output->DescriptorVersion = 1;
        output->AbiVersion = 1;
        output->RequiredFeatureBits = 0x6;
        for (var index = 0; index < 32; index++)
            output->SchemaHash[index] = (byte)index;
        return 0;
    }

    public static uint RegisterV1() => 0x51;
}
