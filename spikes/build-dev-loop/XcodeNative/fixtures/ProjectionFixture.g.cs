namespace NetLift.Spikes.BuildDevLoop;

internal static class ProjectionFixture
{
    public const string CanonicalJson = """{"schemaVersion":2,"schemaHash":"743d759cbe27492fc58aa8628b083d3ea275bed9d22006abef4b51445ab9b376","services":[{"id":1,"name":"fixture","version":1,"kind":"managed","methods":[{"id":1,"name":"count","kind":"sync","supportsCancellation":false,"parameters":[],"returnType":{"kind":"int32","name":null,"nullable":false,"typeId":null}}]}],"dtos":[{"id":1,"name":"point","version":1,"fields":[{"id":1,"name":"x","type":{"kind":"int32","name":null,"nullable":false,"typeId":null}}]}]}""";
    public const string SchemaHash = "743d759cbe27492fc58aa8628b083d3ea275bed9d22006abef4b51445ab9b376";
}
