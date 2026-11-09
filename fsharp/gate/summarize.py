#!/usr/bin/env python3
"""summarize.py RUN... - status counts, models and summed per-model seconds of gate runs (gate/results/RUN.json),
and the five slowest models. Written by Claude (Anthropic) for Robert Dickinson."""
import json, os, sys, collections
G = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'gate', 'results')
allr = []
for name in sys.argv[1:]:
    r = json.load(open(os.path.join(G, name + '.json')))
    c = collections.Counter(x['status'] for x in r)
    print('%-8s %3d models  %s  %7.0f s' % (name, len(r), ', '.join('%s %d' % kv for kv in c.most_common()), sum(x['sec'] for x in r)))
    allr += r
ids = [x['id'] for x in allr]
c = collections.Counter(x['status'] for x in allr)
print('all      %3d models (%d distinct)  %s  %7.0f s' % (len(allr), len(set(ids)), ', '.join('%s %d' % kv for kv in c.most_common()), sum(x['sec'] for x in allr)))
for x in sorted(allr, key=lambda x: -x['sec'])[:5]:
    print('  %8.1f s  %s' % (x['sec'], x['path'].split('2030/')[-1]))
