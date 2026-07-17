namespace Brokers.Models;

public static class InstrumentGroupResolver
{
    public static string Resolve(InstrumentKey instrument)
    {
        int separator = instrument.Value.IndexOf(':');
        return separator > 0 ? instrument.Value[..separator].ToUpperInvariant() : "Unknown";
    }
}
