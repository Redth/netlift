using System.Collections.Generic;
using System.Threading;
using NetLift;

namespace NetLift.ManagedBridge.StreamV4.Contracts;

[LiftDto("stream-order-update", Id = 43, Version = 1)]
public sealed record StreamOrderUpdate(
    [property: LiftField(1)] long Id,
    [property: LiftField(2)] string State);

[LiftService("orders-updates", Id = 42, Version = 1)]
public interface IOrdersUpdatesService
{
    [LiftStream(1)]
    IAsyncEnumerable<StreamOrderUpdate> Watch(CancellationToken cancellationToken);
}
