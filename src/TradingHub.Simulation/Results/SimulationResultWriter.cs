using System.Globalization;
using System.Text.Json;

namespace TradingHub.Simulation.Results;

public sealed class SimulationResultWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task WriteAsync(
        SimulationResult result,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        await WriteSummaryAsync(result, outputDirectory, cancellationToken);
        await WriteOrdersAsync(result, outputDirectory, cancellationToken);
        await WriteExecutionsAsync(result, outputDirectory, cancellationToken);
        await WriteEquityAsync(result, outputDirectory, cancellationToken);
    }

    private static async Task WriteSummaryAsync(
        SimulationResult result,
        string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "summary.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, result.Summary, JsonOptions, cancellationToken);
    }

    private static async Task WriteOrdersAsync(
        SimulationResult result,
        string directory,
        CancellationToken cancellationToken)
    {
        await using var writer = CreateWriter(directory, "orders.csv");
        await writer.WriteLineAsync("order_id,client_order_id,intent_id,instrument,side,type,quantity,status,broker_order_id,reason");
        foreach (var item in result.Orders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = new[]
            {
                item.Request.OrderId,
                item.Request.ClientOrderId,
                item.Request.IntentId,
                item.Request.InstrumentId,
                item.Request.Side.ToString(),
                item.Request.OrderType.ToString(),
                Format(item.Request.Quantity),
                item.Result.Status.ToString(),
                item.Result.BrokerOrderId ?? string.Empty,
                item.Result.Reason ?? string.Empty
            };
            await writer.WriteLineAsync(string.Join(',', fields.Select(Escape)));
        }
    }

    private static async Task WriteExecutionsAsync(
        SimulationResult result,
        string directory,
        CancellationToken cancellationToken)
    {
        await using var writer = CreateWriter(directory, "fills.csv");
        await writer.WriteLineAsync("time,order_id,broker_order_id,instrument,side,status,quantity,price,fee,reason");
        foreach (var item in result.Executions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = new[]
            {
                item.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
                item.OrderId,
                item.BrokerOrderId,
                item.InstrumentId,
                item.Side.ToString(),
                item.Status.ToString(),
                Format(item.LastFillQuantity),
                Format(item.LastFillPrice),
                Format(item.Fee),
                item.Reason ?? string.Empty
            };
            await writer.WriteLineAsync(string.Join(',', fields.Select(Escape)));
        }
    }

    private static async Task WriteEquityAsync(
        SimulationResult result,
        string directory,
        CancellationToken cancellationToken)
    {
        await using var writer = CreateWriter(directory, "equity-curve.csv");
        await writer.WriteLineAsync("time,balance,unrealized_pnl,equity");
        foreach (var item in result.EquityCurve)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = new[]
            {
                item.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                Format(item.Balance),
                Format(item.UnrealizedPnl),
                Format(item.Equity)
            };
            await writer.WriteLineAsync(string.Join(',', fields));
        }
    }

    private static StreamWriter CreateWriter(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true));
    }

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Escape(string value)
    {
        return value.ContainsAny([',', '"', '\n', '\r'])
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
