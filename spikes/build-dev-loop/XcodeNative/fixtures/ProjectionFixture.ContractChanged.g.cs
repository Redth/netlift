namespace NetLift.Spikes.BuildDevLoop;

internal static class ProjectionFixture
{
    public const string CanonicalJson = """{"schemaVersion":2,"schemaHash":"09245cb24d5b16974fb11d690a3de388373395b78446a847c50fbebd75d423c3","services":[{"id":1,"name":"fixture","version":1,"kind":"managed","methods":[{"id":1,"name":"count","kind":"sync","supportsCancellation":false,"parameters":[],"returnType":{"kind":"int32","name":null,"nullable":false,"typeId":null}}]}],"dtos":[{"id":1,"name":"point","version":1,"fields":[{"id":1,"name":"x","type":{"kind":"int32","name":null,"nullable":false,"typeId":null}}]},{"id":2,"name":"vector","version":1,"fields":[{"id":1,"name":"x","type":{"kind":"int32","name":null,"nullable":false,"typeId":null}}]}]}""";
    public const string SchemaHash = "09245cb24d5b16974fb11d690a3de388373395b78446a847c50fbebd75d423c3";
}
