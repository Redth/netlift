namespace NetLift;

[AttributeUsage(AttributeTargets.Assembly, Inherited = false)]
public sealed class LiftContractRevisionAttribute(int major, int minor) : Attribute
{
    public int Major { get; } = major;
    public int Minor { get; } = minor;
}

public enum LiftContractProfile
{
    AsyncCallbackV1 = 1
}

[AttributeUsage(AttributeTargets.Assembly, Inherited = false)]
public sealed class LiftContractProfileAttribute(LiftContractProfile profile) : Attribute
{
    public LiftContractProfile Profile { get; } = profile;
}

[AttributeUsage(AttributeTargets.Assembly, Inherited = false)]
public sealed class LiftHostModuleAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class LiftServiceAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public int Id { get; set; }
    public int Version { get; set; } = 1;
}

[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class LiftNativeServiceAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public int Id { get; set; }
    public int Version { get; set; } = 1;
    public LiftNativeReplyProtocol ReplyProtocol { get; set; }
}

public enum LiftNativeReplyProtocol
{
    LegacyBoolean,
    TypedV1
}

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class LiftMethodAttribute(int id) : Attribute
{
    public int Id { get; } = id;
}

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class LiftStreamAttribute(int id) : Attribute
{
    public int Id { get; } = id;
}

[AttributeUsage(AttributeTargets.Event, Inherited = false)]
public sealed class LiftEventAttribute(int id) : Attribute
{
    public int Id { get; } = id;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class LiftDtoAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public int Id { get; set; }
    public int Version { get; set; } = 1;
}

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class LiftFieldAttribute(int id) : Attribute
{
    public int Id { get; } = id;
    public bool Optional { get; set; }
    public object? DefaultValue { get; set; }
}
