using Brokers.Brokers;

namespace Utility.Indicators
{
    public class BollingerBand : Indicator
    {
        public int WindowSize { get; set; } = 20;
        private CircularLinkedList<CandleData>.Node _currentCandle;
        private decimal _sma = 0m;              // Simple Moving Average
        private decimal _std = 0m;              // Standard Deviation
        private int _stdDevMultiplier = 2;      // Multiplier for bands (typically 2)
        private decimal _upperBand = 0m;
        private decimal _lowerBand = 0m;
        private int _candleCount = 0;           // Track number of candles processed

        public decimal UpperBand => _upperBand;
        public decimal LowerBand => _lowerBand;
        public decimal MiddleBand => _sma;

        public void Calculate(CircularLinkedList<CandleData>.Node candle)
        {
            _currentCandle = candle;
            _candleCount++;

            // Wait until we have enough candles
            if (_candleCount < WindowSize)
            {
                _sma = 0m;
                _std = 0m;
                _upperBand = 0m;
                _lowerBand = 0m;
                candle.Data.BollingerBandMiddleband = _sma;
                candle.Data.BollingerBandUpperband = _upperBand;
                candle.Data.BollingerBandLowerband = _lowerBand;
                return;
            }

            // Calculate SMA and STD
            CalcSMA();
            CalcSTD();

            // Calculate bands
            _upperBand = _sma + (_stdDevMultiplier * _std);
            _lowerBand = _sma - (_stdDevMultiplier * _std);

            // Assign to CandleData
            candle.Data.BollingerBandMiddleband = _sma;
            candle.Data.BollingerBandUpperband = _upperBand;
            candle.Data.BollingerBandLowerband = _lowerBand;
        }

        private void CalcSMA()
        {
            var candle = _currentCandle;
            decimal sum = 0m;
            int count = 0;

            for (int i = 0; i < WindowSize && candle != null; i++)
            {
                sum += candle.Data.Close;
                candle = candle.Previous;
                count++;
            }

            _sma = count == WindowSize ? sum / WindowSize : 0m; // Only calculate if full window
        }

        private void CalcSTD()
        {
            var candle = _currentCandle;
            decimal sumSquaredDiff = 0m;
            int count = 0;

            for (int i = 0; i < WindowSize && candle != null; i++)
            {
                decimal diff = candle.Data.Close - _sma;
                sumSquaredDiff += diff * diff;
                candle = candle.Previous;
                count++;
            }

            _std = count == WindowSize ? (decimal)Math.Sqrt((double)(sumSquaredDiff / WindowSize)) : 0m;
        }
    }
}