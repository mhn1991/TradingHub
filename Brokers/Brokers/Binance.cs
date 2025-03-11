using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brokers.Brokers;

public class Binance : Broker
{
    private string? _apiKey;
    private string? _secretKey;

    public Binance(string apiKey, string secretKey)
    {
        this._apiKey = apiKey;
        this._secretKey = secretKey;
        this.Init("Binance","Rest","https://api.binance.com/api/v3/");
    }

    public Binance()
    {
        this.Init("Binance", "Rest", "https://api.binance.com/api/v3/");
    }
    
}

public class BinanceKlineConverter : JsonConverter<BinanceKline>
{
    public override BinanceKline Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a JSON array for BinanceKline.");
        }

        var kline = new BinanceKline();

        reader.Read(); kline.OpenTime = reader.GetInt64();  // 0
        reader.Read(); kline.Open = reader.GetString();     // 1
        reader.Read(); kline.High = reader.GetString();     // 2
        reader.Read(); kline.Low = reader.GetString();      // 3
        reader.Read(); kline.Close = reader.GetString();    // 4
        reader.Read(); kline.Volume = reader.GetString();   // 5

        // Skip any remaining fields (Binance API provides 12 fields per Kline)
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) { }

        // Ensure we reached the end of the array
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("Expected EndArray token for BinanceKline.");
        }

        return kline;
    }

    public override void Write(Utf8JsonWriter writer, BinanceKline value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.OpenTime);
        writer.WriteStringValue(value.Open);
        writer.WriteStringValue(value.High);
        writer.WriteStringValue(value.Low);
        writer.WriteStringValue(value.Close);
        writer.WriteStringValue(value.Volume);
        writer.WriteEndArray();
    }

    // Helper method to read long values safely
    private static long ReadLong(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long value))
        {
            throw new JsonException("Invalid format for long value.");
        }
        reader.Read();
        return value;
    }

    // Helper method to read string values safely
    private static string ReadString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Invalid format for string value.");
        }
        string value = reader.GetString();
        reader.Read();
        return value;
    }
}