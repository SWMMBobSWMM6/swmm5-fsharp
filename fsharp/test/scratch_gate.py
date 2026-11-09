#!/usr/bin/env python3
"""scratch_gate.py ENGINE OUTDIR [IDS] - a development check that never runs inside the corpus: each gate deck is
copied alone into OUTDIR/<id>/ (under its own name) and run there by ENGINE; its .out and .rpt are compared with
EPA's reference outputs (gate/ref_outputs). A deck that reads other files (rain files, climate files, SAVEd RDII,
...) cannot work alone; when the engine differs from the reference, EPA's own runswmm is run on the same lone copy
and the engine is compared with that instead ("PASS-alone": the engine equals EPA under the same conditions).
Outputs of passing models are deleted. Written by Claude (Anthropic) for Robert Dickinson."""
import os, re, shutil, subprocess, sys, time
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
TS = re.compile(rb'Analysis begun|Analysis ended|Total elapsed time')
engine, outdir = sys.argv[1], sys.argv[2]
ids = set(open(sys.argv[3]).read().split()) if len(sys.argv) > 3 else None
EPA = os.path.join(ROOT, 'ref', 'runswmm')


def rpt(p):
    try:
        return [l for l in open(p, 'rb').read().split(b'\n') if not TS.search(l)]
    except FileNotFoundError:
        return None


def same(d1, o1, d2, o2):
    if rpt(d1) != rpt(d2):
        return False
    a = open(o1, 'rb').read() if os.path.exists(o1) else b''
    b = open(o2, 'rb').read() if os.path.exists(o2) else b''
    return a == b


def run(cmd, cwd, name, tag):
    try:
        p = subprocess.run(cmd + [name, tag + '.rpt', tag + '.out'], cwd=cwd, capture_output=True, timeout=1800)
        return p.returncode, p.stderr.decode(errors='replace')[-300:]
    except subprocess.TimeoutExpired:
        return 'timeout', ''


counts = {}
for line in open(os.path.join(ROOT, 'gate', 'models_all.tsv')):
    mid, path = line.rstrip('\n').split('\t', 1)
    if ids is not None and mid not in ids:
        continue
    d = os.path.join(outdir, mid)
    os.makedirs(d, exist_ok=True)
    name = os.path.basename(path)
    shutil.copy(path, os.path.join(d, name))
    t0 = time.time()
    rc, err = run(engine.split(), d, name, 'e')
    sec = time.time() - t0
    ref = os.path.join(ROOT, 'gate', 'ref_outputs', mid)
    if rc == 0 and same(os.path.join(d, 'e.rpt'), os.path.join(d, 'e.out'), ref + '/r.rpt', ref + '/r.out'):
        st = 'PASS'
    else:
        run([EPA], d, name, 'c')
        if rc == 0 and same(os.path.join(d, 'e.rpt'), os.path.join(d, 'e.out'), os.path.join(d, 'c.rpt'), os.path.join(d, 'c.out')):
            st = 'PASS-alone'
        else:
            st = 'FAIL' if rc == 0 else 'CRASH(%s)' % rc
    counts[st] = counts.get(st, 0) + 1
    print('%-12s %-14s %7.1fs  %s %s' % (st, mid, sec, path.split('2030/')[-1], err.strip().replace('\n', ' | ')[:200] if st.startswith('CRASH') else ''), flush=True)
    if st.startswith('PASS'):
        shutil.rmtree(d, ignore_errors=True)
print('summary:', ', '.join('%s %d' % kv for kv in sorted(counts.items())))
