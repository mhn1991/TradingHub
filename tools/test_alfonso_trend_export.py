import json
import tempfile
import unittest
from pathlib import Path
from alfonso_trend_export import read_rows, summarize


def row(i, close, labels=(1, 1, 3, 3)):
    return {'t': i*900, 'at': (i+1)*900, 'o': close, 'h': close+0.2, 'l': close-0.2,
            'c': close, 's': [[s, 0, 0, False] for s in labels]}


class TrendExportTests(unittest.TestCase):
    def test_neutral_is_not_correct_and_future_tail_is_excluded(self):
        rows = [row(i, 100+i) for i in range(30)]
        stats, events = summarize(rows)
        self.assertEqual(stats[0]['forwardMatchPercent'], 100)
        self.assertEqual(stats[0]['scoredBars'], 26)
        self.assertIsNone(stats[2]['forwardMatchPercent'])
        self.assertEqual(stats[2]['directionalPercent'], 0)
        self.assertEqual(stats[2]['disagreementPercent'], 100)
        self.assertEqual(stats[0]['commonBars'], 0)
        self.assertEqual(events, [])  # Initial direction is not a reversal.

    def test_breakout_recognition_includes_misses_and_neutral_delay(self):
        closes = list(range(100, 130)) + list(range(120, 70, -1))
        rows = [row(i, c, (1, 2 if i >= 32 else 3, 3, 2 if i >= 35 else 3))
                for i, c in enumerate(closes)]
        stats, events = summarize(rows)
        self.assertEqual(len(events), 1)
        event = events[0]
        self.assertEqual(event['direction'], -1)
        self.assertEqual(event['delays'], [None, max(0, 32-event['index']), None, max(0, 35-event['index'])])
        self.assertEqual(stats[0]['breakoutsRecognized'], 0)
        self.assertIsNone(stats[0]['medianRecognitionBars'])
        self.assertEqual(stats[1]['breakoutEvents'], 1)

    def test_common_score_uses_identical_directional_candles(self):
        rows = [row(i, 100+i, (1, 2, 1, 2)) for i in range(20)]
        stats, _ = summarize(rows)
        self.assertEqual([s['commonBars'] for s in stats], [16]*4)
        self.assertEqual([s['commonMatchPercent'] for s in stats], [100, 0, 100, 0])

    def test_rejects_unclosed_candle(self):
        raw = {'intervalMinutes': 15, 'instrument': 'test',
               'openTime': '2026-01-01T00:00:00Z', 'availableAt': '2026-01-01T00:01:00Z',
               'states': [{}]*4}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'audit.jsonl'
            path.write_text(json.dumps(raw)+'\n')
            with self.assertRaisesRegex(ValueError, 'Unclosed candle'):
                read_rows(path, 'test', 0, 9999999999)


if __name__ == '__main__':
    unittest.main()
