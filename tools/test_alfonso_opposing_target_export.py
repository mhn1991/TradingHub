import unittest

from alfonso_opposing_target_export import validate_trade


class TargetProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.trade = {
            'setupId': 'example', 'side': 'Sell', 'signalPrice': 100,
            'takeProfitPrice': 99, 'stopLossPrice': 110, 'stopAmendmentCount': 0,
            'signalCreatedAt': '2026-07-17T03:00:00+00:00',
            'openedAt': '2026-07-17T03:21:00+00:00',
            'stopSource': 'structural 15m: swing high 110 at 2026-07-17 01:00 UTC',
            'targetSource': 'opposing Demand 15m: proximal 99, distal 98; '
                            'base 2026-07-17T02:00:00+00:00 to 2026-07-17T02:15:00+00:00; '
                            'confirmed close 2026-07-17T03:00:00+00:00; state Tested'
        }
        self.decision = {'target': '99', 'stop': '110'}

    def test_variable_r_at_exact_confirmation_close(self):
        self.assertEqual(validate_trade(self.trade, self.decision), 0.1)

    def test_buy_is_supply_and_target_above_entry(self):
        trade = {**self.trade, 'side': 'Buy', 'stopLossPrice': 90, 'takeProfitPrice': 101,
                 'targetSource': self.trade['targetSource'].replace('Demand', 'Supply')
                 .replace('proximal 99, distal 98', 'proximal 101, distal 102')}
        self.assertEqual(validate_trade(trade, {'target': '101', 'stop': '90'}), 0.1)

    def test_rejects_future_confirmation_wrong_kind_timeframe_and_eliminated_zone(self):
        for original, replacement in [('close 2026-07-17T03:00', 'close 2026-07-17T03:15'),
                                      ('Demand', 'Supply'), ('15m', '1m'), ('Tested', 'Eliminated')]:
            with self.subTest(replacement=replacement), self.assertRaises((AssertionError, ValueError)):
                validate_trade({**self.trade, 'targetSource': self.trade['targetSource']
                                .replace(original, replacement)}, self.decision)

    def test_rejects_modified_or_missing_bracket_provenance(self):
        for change in [{'takeProfitPrice': 98}, {'targetSource': None},
                       {'stopAmendmentCount': 1}, {'stopLossPrice': 101},
                       {'stopSource': self.trade['stopSource'].replace('01:00', '02:30')}]:
            with self.subTest(change=change), self.assertRaises((AssertionError, ValueError)):
                validate_trade({**self.trade, **change}, self.decision)


if __name__ == '__main__':
    unittest.main()
