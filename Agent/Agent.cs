using API;
using Brokers.Brokers;

namespace Agent;

public class Agent
{
    private readonly Rest _rest;
    private readonly Instrument _instrument;

    public Agent()
    {
        _rest = new Rest();
        _instrument = new Instrument("BTCUSDT", "5m", "500");
    }

    public async Task InitAsync()
    {
        try
        {
            List<List<object>> data = await _rest.Get(_instrument.GetFinalUrl());

            if (data.Count == 0)
            {
                Console.WriteLine("No data received.");
                return;
            }

            foreach (var item in data)
            {
                Console.WriteLine(item.FirstOrDefault() ?? "Empty Item");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching data: {ex.Message}");
        }
    }
}