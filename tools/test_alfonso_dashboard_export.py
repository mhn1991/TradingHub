"""Focused checks for the saved dashboard's higher-timeframe candle export."""
import unittest
from datetime import datetime, timezone

from alfonso_dashboard_export import aggregate, stamp, trade_context


def iso(seconds):
    return datetime.fromtimestamp(seconds, timezone.utc).isoformat()


def minutes(count):
    return [{'openTime': iso(i * 60), 'closeTime': iso((i + 1) * 60),
             'open': i, 'high': i + 2, 'low': i - 1, 'close': i + 1, 'volume': 3}
            for i in range(count)]


class TimeframeExportTests(unittest.TestCase):
    def test_optional_five_minute_context_uses_complete_buckets(self):
        rows = minutes(16)
        result = aggregate(rows[:7] + rows[8:], periods={'5m': 300})
        self.assertEqual([c['availableAt'] for c in result['5m']], [iso(300), iso(900)])
        self.assertEqual(result['5m'][0]['close'], 5)
        self.assertNotIn('5m', aggregate(rows))

    def test_complete_ohlcv_and_boundaries(self):
        result = aggregate(minutes(240))
        self.assertEqual({key: len(value) for key, value in result.items()},
                         {'15m': 16, '1h': 4, '4h': 1})
        self.assertEqual(result['4h'][0], {'openTime': iso(0), 'availableAt': iso(14400),
                                         'open': 0, 'high': 241, 'low': -1,
                                         'close': 240, 'volume': 720})

    def test_missing_first_minute_discards_bucket(self):
        result = aggregate(minutes(480)[1:])
        self.assertEqual([c['openTime'] for c in result['4h']], [iso(14400)])

    def test_gap_discards_incomplete_bucket_and_recovers(self):
        rows = minutes(480)
        result = aggregate(rows[:80] + rows[81:])
        self.assertEqual([c['openTime'] for c in result['4h']], [iso(14400)])
        self.assertEqual(len(result['1h']), 7)
        self.assertEqual(len(result['15m']), 31)

    def test_no_trailing_partial_candle(self):
        result = aggregate(minutes(239))
        self.assertEqual(len(result['4h']), 0)
        self.assertEqual(len(result['1h']), 3)
        self.assertEqual(len(result['15m']), 15)

    def test_duplicate_or_out_of_order_rejected(self):
        with self.assertRaises(ValueError):
            aggregate(minutes(3) + minutes(1))

    def test_context_preserves_200_completed_bars_and_separates_future(self):
        candles = aggregate(minutes(15 * 450))['15m']
        history = {'15m': (candles, [stamp(c['availableAt']) for c in candles])}
        for offset in (0, 1, 899):
            decision = 220 * 900 + offset
            trade = {'signalCreatedAt': iso(decision), 'openedAt': iso(decision + 60),
                     'closedAt': iso(decision + 1800)}
            context = trade_context(history, trade)['15m']
            visible = [c for c in context if stamp(c['availableAt']) <= decision]
            self.assertEqual(len(visible), 200)
            self.assertEqual(visible[-1]['availableAt'], iso(220 * 900))
            self.assertGreater(stamp(context[-1]['availableAt']), decision)
            after_exit = [c for c in context if stamp(c['openTime']) >= stamp(trade['closedAt'])]
            self.assertEqual(len(after_exit), 200)

    def test_post_exit_count_skips_weekend_and_handles_short_history(self):
        candles = aggregate(minutes(600), include_minutes=True)['1m']
        # Simulate a weekend: the next saved bar is much later, but still counts as one.
        for candle in candles[300:]:
            candle['availableAt'] = iso(stamp(candle['availableAt']) + 172800)
            candle['openTime'] = iso(stamp(candle['openTime']) + 172800)
        history = {'1m': (candles, [stamp(c['availableAt']) for c in candles])}
        trade = {'signalCreatedAt': iso(250 * 60), 'openedAt': iso(251 * 60), 'closedAt': iso(300 * 60)}
        context = trade_context(history, trade)['1m']
        self.assertEqual(len([c for c in context if stamp(c['availableAt']) > 300 * 60]), 200)
        trade['closedAt'] = candles[-21]['availableAt']
        context = trade_context(history, trade)['1m']
        self.assertEqual(len([c for c in context if stamp(c['availableAt']) > stamp(trade['closedAt'])]), 20)


if __name__ == '__main__':
    unittest.main()
