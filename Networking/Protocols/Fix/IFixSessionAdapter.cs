using Networking.Duplex;

namespace Networking.Fix;

/// <summary>
/// Marker contract for a QuickFIX/n or custom FIX client adapter.
/// The adapter should expose encoded inbound and outbound FIX messages through IDuplexSession.
/// </summary>
public interface IFixSessionAdapter : IDuplexSession
{
}
