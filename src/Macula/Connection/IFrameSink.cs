using Macula.Frame;

namespace Macula.Connection;

/// <summary>
/// Anything that can send a signed PUBLISH on behalf of an identity:
/// <see cref="Session"/> itself, or a test double. Lets
/// <see cref="SupervisedPubSub"/>'s publisher run against a sink whose sends
/// can be observed without a station.
/// </summary>
public interface IFrameSink
{
    Task PublishAsync(PublishSpec spec, CancellationToken ct = default);
}
