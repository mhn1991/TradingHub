using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Binance;
using Brokers.Ig;
using Brokers.Oanda;

namespace Brokers.Infrastructure;

[JsonSerializable(typeof(JsonElement[][]))]
[JsonSerializable(typeof(BinanceKlineRow[]))]
[JsonSerializable(typeof(BinanceAccountResponse))]
[JsonSerializable(typeof(BinanceOrderDto[]))]
[JsonSerializable(typeof(BinanceCommissionResponse))]
[JsonSerializable(typeof(OandaCandlesResponse))]
[JsonSerializable(typeof(OandaAccountSummaryResponse))]
[JsonSerializable(typeof(OandaPendingOrdersResponse))]
[JsonSerializable(typeof(OandaOpenPositionsResponse))]
[JsonSerializable(typeof(OandaOpenTradesResponse))]
[JsonSerializable(typeof(OandaInstrumentsResponse))]
[JsonSerializable(typeof(OandaCreateOrderEnvelope))]
[JsonSerializable(typeof(OandaOrderMutationResponse))]
[JsonSerializable(typeof(OandaTradeCloseRequest))]
[JsonSerializable(typeof(OandaTradeDependentOrdersRequest))]
[JsonSerializable(typeof(OandaTradeDependentOrdersResponse))]
[JsonSerializable(typeof(OandaPricingStreamMessage))]
[JsonSerializable(typeof(OandaTransaction))]
[JsonSerializable(typeof(OandaTransactionsResponse))]
[JsonSerializable(typeof(IgLoginRequest))]
[JsonSerializable(typeof(IgLoginResponse))]
[JsonSerializable(typeof(IgSwitchAccountRequest))]
[JsonSerializable(typeof(IgSwitchAccountResponse))]
[JsonSerializable(typeof(IgAccountDto[]))]
[JsonSerializable(typeof(IgPricesResponse))]
[JsonSerializable(typeof(IgPositionsResponse))]
[JsonSerializable(typeof(IgWorkingOrdersResponse))]
internal partial class BrokerJsonSerializerContext : JsonSerializerContext;
