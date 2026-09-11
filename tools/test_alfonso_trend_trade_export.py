import unittest

from alfonso_dashboard_export import stamp
from alfonso_trend_trade_export import match_decision


class DecisionMatchTests(unittest.TestCase):
    def setUp(self):
        self.trade = {'signalCreatedAt': '2025-12-23T15:45:00Z', 'side': 'Sell',
                      'entryPrice': 70.4958, 'setupId': 'example'}
        self.row = {'side': 'Supply', 'proximal': '70.4958', 'topTrend': 'Downtrend'}
        self.at = stamp(self.trade['signalCreatedAt'])

    def test_exact_recorded_decision(self):
        self.assertIs(match_decision(self.trade, {self.at: [self.row]}), self.row)

    def test_rejects_missing_or_wrong_direction_price_time(self):
        for rows in ({}, {self.at: [{**self.row, 'side': 'Demand'}]},
                     {self.at: [{**self.row, 'proximal': '70.6'}]},
                     {self.at + 60: [self.row]}):
            with self.subTest(rows=rows), self.assertRaises(ValueError):
                match_decision(self.trade, rows)

    def test_rejects_ambiguous(self):
        with self.assertRaises(ValueError):
            match_decision(self.trade, {self.at: [self.row, self.row]})

    def test_buy_requires_demand(self):
        trade = {**self.trade, 'side': 'Buy'}
        row = {**self.row, 'side': 'Demand'}
        self.assertIs(match_decision(trade, {self.at: [row]}), row)


if __name__ == '__main__':
    unittest.main()
