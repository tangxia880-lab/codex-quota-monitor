import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
spec = importlib.util.spec_from_file_location('monitor', Path(__file__).parents[1]/'scripts/monitor.py')
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

class MonitorTests(unittest.TestCase):
    def test_quota_multibucket_and_missing(self):
        result = m.normalize({'rateLimitsByLimitId': {'codex': {'primary': {'usedPercent': 23.5}}, 'spark': {'primary': {}}}})
        self.assertEqual(result['buckets'][0]['windows'][0]['remaining'], 76.5)
        self.assertIsNone(result['buckets'][1]['windows'][0]['remaining'])
    def test_legacy_clamp(self):
        result = m.normalize({'rate_limits': {'primary': {'used_percent': 130}}})
        self.assertEqual(result['buckets'][0]['windows'][0]['remaining'], 0)
    def test_weighted_cache_and_dedupe(self):
        def row(key, i, c): return {'key': key, 'usage': {'input_tokens': i, 'cached_input_tokens': c}}
        result = m.summarize([row('a', 100, 0), row('a', 100, 50), row('b', 900, 900), row('c', 0, 0), row('d', None, None), row('e', 10, 20)])
        self.assertEqual(result, {'percent': 95, 'included': 2, 'missing': 2, 'zero': 1})
    def parse(self, events):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/'session.jsonl'
            path.write_text('\n'.join(json.dumps(e) for e in events))
            return m.parse_session(path)
    def event(self, kind, payload, order=None):
        r = {'type': kind, 'payload': payload, 'timestamp': '2026-10-20T12:00:00Z'}
        if order is not None: r['ordinal'] = order
        return r
    def test_turn_not_last_response_or_cumulative(self):
        e = self.event
        rows = self.parse([e('session_meta', {'id': 't'}), e('event_msg', {'type': 'task_started', 'turn_id': 'r'}),
            e('token_usage_record', {'thread_id': 't', 'turn_id': 'r', 'turn_token_usage': {'total_tokens': 1000}, 'usage': {'total_tokens': 30}, 'thread_token_usage': {'total_tokens': 9000}}),
            e('event_msg', {'type': 'task_complete', 'turn_id': 'r'})])
        self.assertEqual(rows[0]['usage']['total_tokens'], 1000)
    def test_fork_inherited_records_excluded(self):
        e = self.event
        rows = self.parse([e('session_meta', {'id': 'child', 'forked_from_id': 'parent'}), e('event_msg', {'type': 'task_started', 'turn_id': 'old'}),
            e('token_usage_record', {'thread_id': 'parent', 'turn_id': 'old', 'turn_token_usage': {'total_tokens': 9000}}),
            e('event_msg', {'type': 'task_complete', 'turn_id': 'old'})])
        self.assertEqual(rows, [])
    def test_child_cutoff_and_late_usage(self):
        e = self.event
        rows = self.parse([e('session_meta', {'id': 'child', 'parent_thread_id': 'parent', 'subagent_history_start_ordinal': 10}),
            e('event_msg', {'type': 'task_started', 'turn_id': 'old'}, 2), e('event_msg', {'type': 'task_complete', 'turn_id': 'old'}, 3),
            e('event_msg', {'type': 'task_started', 'turn_id': 'new'}, 11), e('event_msg', {'type': 'task_complete', 'turn_id': 'new'}, 12),
            e('token_usage_record', {'thread_id': 'child', 'turn_id': 'new', 'turn_token_usage': {'total_tokens': 42}}, 13)])
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]['usage']['total_tokens'], 42)
        self.assertEqual(rows[0]['parent'], 'parent')
    def test_old_logs_unknown(self):
        e = self.event
        rows = self.parse([e('session_meta', {'id': 't'}), e('event_msg', {'type': 'task_started', 'turn_id': 'r'}), e('event_msg', {'type': 'task_complete', 'turn_id': 'r'})])
        self.assertIsNone(rows[0]['usage']['total_tokens'])

if __name__ == '__main__': unittest.main()
