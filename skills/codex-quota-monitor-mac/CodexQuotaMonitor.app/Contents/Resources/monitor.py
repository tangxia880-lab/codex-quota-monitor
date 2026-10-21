#!/usr/bin/env python3
"""Read-only Codex quota and local turn usage; Python standard library only."""
import argparse
import datetime as dt
import json
import math
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import time


def executable():
    candidates = [os.environ.get('CODEX_BINARY', ''),
        '/Applications/Codex.app/Contents/Resources/codex',
        '/Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex',
        str(Path.home() / 'Applications/Codex.app/Contents/Resources/codex'),
        shutil.which('codex') or '', str(Path.home() / '.local/bin/codex')]
    for item in candidates:
        if item and Path(item).is_file() and os.access(item, os.X_OK):
            return item
    raise RuntimeError('未找到 Codex，请安装并登录 Codex，或设置 CODEX_BINARY。')


def query(timeout=35):
    proc = subprocess.Popen([executable(), 'app-server', '--stdio'], stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
    lines = queue.Queue()
    def reader():
        for line in proc.stdout:
            lines.put(line)
        lines.put(None)
    worker = threading.Thread(target=reader, daemon=True)
    worker.start()
    deadline = time.monotonic() + timeout
    def send(obj):
        proc.stdin.write(json.dumps(obj) + '\n')
        proc.stdin.flush()
    def receive(ident):
        while True:
            try:
                line = lines.get(timeout=max(.01, deadline - time.monotonic()))
            except queue.Empty:
                raise RuntimeError('额度查询超时，请检查网络与 Codex 登录状态。') from None
            if line is None:
                raise RuntimeError('Codex 查询进程退出，请检查登录状态。')
            try:
                obj = json.loads(line)
            except ValueError:
                continue
            if obj.get('id') != ident:
                continue
            if obj.get('error'):
                raise RuntimeError('无法读取额度，请确认使用 ChatGPT 账户登录并更新 Codex。')
            return obj.get('result') or {}
    try:
        send({'id': 1, 'method': 'initialize', 'params': {'clientInfo': {
            'name': 'codex_quota_monitor_mac', 'version': '1.0.0'}, 'capabilities': None}})
        receive(1)
        send({'method': 'initialized'})
        send({'id': 2, 'method': 'account/rateLimits/read'})
        return normalize(receive(2))
    finally:
        if proc.poll() is None:
            proc.terminate()
        try:
            proc.wait(timeout=2)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
        proc.stdin.close()
        worker.join(timeout=2)
        proc.stdout.close()


def number(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) else None


def pick(obj, camel, snake):
    return obj.get(camel, obj.get(snake))


def normalize(root):
    buckets = pick(root, 'rateLimitsByLimitId', 'rate_limits_by_limit_id')
    if not buckets:
        legacy = pick(root, 'rateLimits', 'rate_limits')
        buckets = {'codex': legacy} if isinstance(legacy, dict) else {}
    result = []
    for ident, bucket in buckets.items():
        if not isinstance(bucket, dict):
            continue
        windows = []
        for key in ('primary', 'secondary'):
            window = bucket.get(key)
            if not isinstance(window, dict):
                continue
            used = number(pick(window, 'usedPercent', 'used_percent'))
            windows.append({'key': key, 'remaining': None if used is None else max(0, min(100, 100-used)),
                'minutes': number(pick(window, 'windowDurationMins', 'window_duration_mins')),
                'reset': number(pick(window, 'resetsAt', 'resets_at'))})
        result.append({'id': ident, 'name': pick(bucket, 'limitName', 'limit_name') or ident,
                       'windows': windows})
    return {'buckets': result, 'fetchedAt': time.time()}


FIELDS = ('total_tokens', 'input_tokens', 'cached_input_tokens', 'output_tokens', 'reasoning_output_tokens')

def counts(obj):
    obj = obj if isinstance(obj, dict) else {}
    return {key: value if isinstance(value := obj.get(key), int) and not isinstance(value, bool) and value >= 0 else None for key in FIELDS}


def parse_session(path):
    thread = path.stem
    turns = {}
    parent = ''
    cutoff = None
    uncertain = False
    has_meta = False
    sequence = 0
    try:
        stream = path.open(encoding='utf-8')
    except OSError:
        return []
    with stream:
        for line in stream:
            if len(line) > 4*1024*1024:
                continue
            if not any('"'+tag+'"' in line for tag in ('session_meta', 'turn_context', 'token_usage_record', 'event_msg')):
                continue
            try:
                obj = json.loads(line)
                if not isinstance(obj, dict):
                    continue
                p = obj.get('payload')
                if not isinstance(p, dict):
                    continue
                kind = obj.get('type')
                ordinal = obj.get('ordinal')
                sequence += 1
                order = ordinal if isinstance(ordinal, int) else sequence
                if kind == 'session_meta':
                    if not has_meta:
                        thread = p.get('id') or thread
                        parent = p.get('parent_thread_id') or ''
                        cutoff = p.get('subagent_history_start_ordinal')
                        uncertain = cutoff is None and bool(p.get('forked_from_id'))
                        has_meta = True
                    elif p.get('id') and p['id'] != thread and cutoff is None:
                        uncertain = True
                    continue
                if cutoff is not None and (not isinstance(ordinal, int) or ordinal < cutoff):
                    continue
                explicit = p.get('thread_id')
                if explicit and explicit != thread:
                    continue
                turn = p.get('turn_id')
                if not turn:
                    continue
                r = turns.setdefault(turn, {'key': thread+':'+turn, 'thread': thread, 'turn': turn,
                    'parent': parent, 'model': '', 'usage': counts({}), 'completed': '',
                    'own': False, 'started': False, '_order': -1})
                if kind == 'token_usage_record' and explicit == thread and order >= r['_order']:
                    r['usage'] = counts(p.get('turn_token_usage'))
                    r['own'] = True
                    r['_order'] = order
                elif kind == 'turn_context':
                    r['model'] = p.get('model') or r['model']
                elif kind == 'event_msg' and p.get('type') == 'task_started':
                    r['started'] = True
                elif kind == 'event_msg' and p.get('type') == 'task_complete':
                    r['completed'] = p.get('completed_at') or obj.get('timestamp') or '未知时间'
            except (ValueError, TypeError, KeyError):
                continue
    return [{k: v for k, v in r.items() if not k.startswith('_')}
            for r in turns.values() if r['completed'] and (r['own'] or (r['started'] and not uncertain))]


def tasks(home=None, days=7):
    root = Path(home or os.environ.get('CODEX_HOME') or Path.home()/'.codex')
    records = {}
    cutoff = time.time() - days*86400
    for folder in ('sessions', 'archived_sessions'):
        base = root / folder
        if not base.exists():
            continue
        for path in base.rglob('*.jsonl'):
            try:
                if path.stat().st_mtime < cutoff:
                    continue
                for r in parse_session(path):
                    records[r['key']] = r
            except OSError:
                continue
    rows = sorted(records.values(), key=lambda r: str(r['completed']), reverse=True)[:200]
    return {'records': rows, 'cache': summarize(rows)}


def summarize(rows):
    unique = {r['key']: r for r in rows}
    total_input = cached = included = missing = zero = 0
    for r in unique.values():
        u = r['usage']
        inp, hit = u.get('input_tokens'), u.get('cached_input_tokens')
        if inp is None or hit is None or inp < 0 or hit < 0 or hit > inp:
            missing += 1
        elif inp == 0:
            zero += 1
        else:
            total_input += inp
            cached += hit
            included += 1
    return {'percent': cached / total_input * 100 if total_input else None,
            'included': included, 'missing': missing, 'zero': zero}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['quota', 'tasks', 'snapshot'])
    parser.add_argument('--home')
    args = parser.parse_args()
    result = {}
    if args.command in ('quota', 'snapshot'):
        try:
            result.update(query())
        except (OSError, RuntimeError) as exc:
            result['error'] = str(exc) if isinstance(exc, RuntimeError) else '无法启动 Codex 查询进程。'
    if args.command in ('tasks', 'snapshot'):
        result.update(tasks(args.home))
    print(json.dumps(result, ensure_ascii=False))

if __name__ == '__main__':
    main()
