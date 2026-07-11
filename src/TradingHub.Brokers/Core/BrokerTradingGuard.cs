using TradingHub.Domain.Trading;

namespace TradingHub.Brokers;

public sealed class BrokerTradingGuard
{
    private readonly TradingEnvironment _environment;
    private readonly string _configuredAccountId;
    private readonly string? _liveAccountConfirmation;

    public BrokerTradingGuard(
        TradingEnvironment environment,
        string configuredAccountId,
        string? liveAccountConfirmation)
    {
        _environment = environment;
        _configuredAccountId = configuredAccountId;
        _liveAccountConfirmation = liveAccountConfirmation;
    }

    public bool CanSubmit(string targetAccountId, out string reason)
    {
        if (!string.Equals(targetAccountId, _configuredAccountId, StringComparison.Ordinal))
        {
            reason = "The request account does not match the configured broker account.";
            return false;
        }

        if (_environment == TradingEnvironment.Live
            && !string.Equals(_liveAccountConfirmation, _configuredAccountId, StringComparison.Ordinal))
        {
            reason = "Live trading is locked. Confirm the exact live account ID in configuration.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
