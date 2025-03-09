using Brokers.Brokers;

namespace Utility.Indicators
{
    public class StochRSI : Indicator
    {
        private CircularLinkedList<CandleData>.Node _currentCandle;
        public decimal WindowSize { get; set; } = 14; // Lookback period for StochRSI
        public long NumberOfCandles { get; set; } = 0; // Renamed for clarity
        public bool CalcStochRsi { get; set; } = false;
        public int KPeriod { get; set; } = 3; // Smoothing period for %K
        public int DPeriod { get; set; } = 3; // For %D (not implemented yet)

        public void Calculate(CircularLinkedList<CandleData>.Node candle)
        {
            _currentCandle = candle;
            NumberOfCandles++;

            // Wait until we have enough candles for StochRSI
            if (NumberOfCandles < WindowSize)
            {
                return;
            }

            // Calculate StochRSI for the current candle
            decimal rsi = candle.Data.RSI;
            decimal minRSI = GetMinRSI();
            decimal maxRSI = GetMaxRSI();
            decimal stochRSI = (maxRSI == minRSI) ? 0m : (rsi - minRSI) / (maxRSI - minRSI);
            candle.Data.StochRSI = stochRSI;

            // Calculate %K only when we have enough StochRSI values
            if (NumberOfCandles >= WindowSize + KPeriod - 1)
            {
                CalcStochRsi = true;
                candle.Data.StochRSIK = GetKline();
            }
        }

        private decimal GetMinRSI()
        {
            var candle = _currentCandle;
            if (candle == null) return 0m; // Safety check
            decimal minRSI = candle.Data.RSI;
            for (int i = 0; i < WindowSize - 1 && candle.Previous != null; i++)
            {
                candle = candle.Previous;
                if (candle.Data.RSI < minRSI)
                {
                    minRSI = candle.Data.RSI;
                }
            }
            return minRSI;
        }

        private decimal GetMaxRSI()
        {
            var candle = _currentCandle;
            if (candle == null) return 0m; // Safety check
            decimal maxRSI = candle.Data.RSI;
            for (int i = 0; i < WindowSize - 1 && candle.Previous != null; i++)
            {
                candle = candle.Previous;
                if (candle.Data.RSI > maxRSI)
                {
                    maxRSI = candle.Data.RSI;
                }
            }
            return maxRSI;
        }

        public decimal GetKline()
        {
            decimal sum = 0m;
            int count = 0;
            var candle = _currentCandle;
            for (int i = 0; i < KPeriod && candle != null; i++)
            {
                sum += candle.Data.StochRSI;
                candle = candle.Previous;
                count++;
            }
            return count == 0 ? 0m : sum / count; // Avoid division by zero
        }
    }
}