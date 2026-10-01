using System.Reflection;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: IdentityCheck <old-assembly> <new-assembly>");
    return 2;
}

AssemblyName oldIdentity = AssemblyName.GetAssemblyName(args[0]);
AssemblyName newIdentity = AssemblyName.GetAssemblyName(args[1]);
Console.WriteLine($"old_identity={oldIdentity.FullName}");
Console.WriteLine($"new_identity={newIdentity.FullName}");
if (!string.Equals(oldIdentity.FullName, newIdentity.FullName, StringComparison.Ordinal))
{
    Console.Error.WriteLine("OLD and NEW fixture assembly identities differ.");
    return 1;
}

return 0;
