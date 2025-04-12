using Brokers.Brokers;

namespace Utility.Services;

public class FindHighLowService : IServices
{
    private FindHighLow _finder;

    public FindHighLowService()
    {
        _finder = new FindHighLow();
    }

    public void Run(in CircularLinkedList<CandleData> chart, out CircularLinkedList<CandleData> results)
    {
        throw new NotImplementedException();
    }
}