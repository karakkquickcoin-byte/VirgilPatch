"""Smoke test for the Update-button gate (2026-11-19). Runs the real publisher against a throwaway Git repo and
private record under E:\\Temp, never the real mirror or weekly-passwords.json. Leaves week1/week2 snapshots for
updater/tests/GateTests.cs:   py -3.10 gate_publish_smoke.py   (prints FIXTURES=<folder>)"""
import datetime as dt, json, os, pathlib, sys, uuid, zipfile
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))  # publisher/ in the release repo
import virgil_core as vc, virgil_publish as pub, virgil_lock as lock

base = pathlib.Path(os.environ.get('VIRGIL_TEST_ROOT', r'E:\Temp\DylanGreenway\virgil-tests'))
root = base / ('gate-' + uuid.uuid4().hex[:8]); root.mkdir(parents=True)
repo = root / 'repo'; repo.mkdir(); work = root / 'private'; work.mkdir()
vc.REPO = str(repo.resolve()); vc.REPO_PATCHES = str(repo / 'patches'); vc.WORK = str(work.resolve())
pub.HASH_CACHE = str(work / 'hash_cache.json')
# Every module-level path computed from the real WORK must be redirected (remove_folder rewrites PUBLISHED).
vc.PUBLISHED = str(work / 'published.json'); vc.BACKUPS = str(work / 'backups')
for value in (lock.secret_path(), vc.PUBLISHED, vc.BACKUPS, pub.HASH_CACHE, pub.merge_result_path(str(root / 'Test'))):
    assert os.path.abspath(value).startswith(str(work.resolve())), 'test would touch real publisher data: ' + value
pub.git('init', '-q', '-b', 'main'); pub.git('config', 'user.name', 'Gate tests'); pub.git('config', 'user.email', 'test@invalid.local')
(repo / 'README.md').write_text('Synthetic gate fixture')
pub.git('add', 'README.md'); pub.git('commit', '-qm', 'fixture')
a = root / 'Test'; b = root / 'Other'; a.mkdir(); b.mkdir()

def patch(path, files):
    entries = []
    for i, (name, data) in enumerate(files.items(), 1):
        (path / name).write_bytes(data); entries.append({'file': name, 'id': i + (100 if path.name == 'Other' else 0)})
    (path / 'patch.json').write_text(json.dumps({'name': path.name, 'version': '1', 'files': entries}))

def rejects(fn):
    try: fn()
    except Exception: return
    raise AssertionError('Expected rejection')

def snapshot(name):
    target = root / name; target.mkdir(); archive = root / (name + '.zip')
    pub.git('archive', '--format=zip', '-o', str(archive.resolve()), 'HEAD')
    with zipfile.ZipFile(archive) as z: z.extractall(target)
    return target

def head_index(): return json.loads(pub.git('show', 'HEAD:index.json'))

patch(a, {'spoiler.ogg': b'first secret', 'same.ogg': b'unchanged'}); patch(b, {'other.ogg': b'another patch'})
vc.save_published([str(a.resolve()), str(b.resolve())])

# No weekly password configured: plain publishing exactly as before, no gate.
assert pub.push_folder(str(b.resolve()), push=False)['ok']
assert 'gate' not in head_index(); print('PASS no-password-publishes-without-gate')

vc.atomic_json(lock.secret_path(), {'format': 1, 'recovery_password': 'fixture-recovery-only', 'history': []})
rejects(lambda: pub.push_folder(str(a.resolve()), push=False)); print('PASS unset-weekly-password-blocks-publication')

lock.set_password('fixture-week-one')
assert pub.push_folder(str(a.resolve()), push=False)['ok']
idx = head_index(); g = idx['gate']
assert not idx.get('locked') and g['pass_number'] == '0.1' and g['iterations'] == 600000
tracked = pub.vi.records(str(repo), 'HEAD')
assert any(p.startswith('patches/') and p.endswith('/spoiler.ogg') for p in tracked), 'files must stay plain'
assert not any(p.startswith('locked/') for p in tracked), 'no encrypted objects'
assert 'fixture-week-one' not in json.dumps(idx) and 'fixture-recovery-only' not in json.dumps(idx)
assert lock._check('fixture-week-one', g['weekly']['salt']) == g['weekly']['check']
assert lock._check('fixture-recovery-only', g['recovery']['salt']) == g['recovery']['check']
assert set(idx['patches']) == {'Test', 'Other'}
print('PASS gate-added-files-plain-codes-not-published')
pub.vi.validate_head(str(repo)); print('PASS gated-snapshot-validates')
week1 = snapshot('week1')

# Same Pass, another push: the check value must not change (players stay unlocked all week).
patch(b, {'other.ogg': b'another patch v2'})
assert pub.push_folder(str(b.resolve()), push=False)['ok']
assert head_index()['gate'] == g; print('PASS gate-stable-within-a-pass')

lock.set_password('fixture-week-two')
patch(a, {'spoiler.ogg': b'second secret', 'same.ogg': b'unchanged'})
same_sha = head_index()['patches']['Test']['files']['same.ogg']['sha']
assert pub.push_folder(str(a.resolve()), push=False)['ok']
idx2 = head_index(); g2 = idx2['gate']
assert g2['pass_number'] == '0.2' and g2['weekly'] != g['weekly'] and g2['recovery'] == g['recovery']
assert lock._check('fixture-week-one', g2['weekly']['salt']) != g2['weekly']['check']
assert idx2['patches']['Test']['files']['same.ogg']['sha'] == same_sha
print('PASS rotation-new-check-old-code-fails-unchanged-files-same')
week2 = snapshot('week2')

real_now = lock.now; lock.now = lambda: real_now() + dt.timedelta(days=8)
rejects(lambda: pub.push_folder(str(a.resolve()), push=False)); lock.now = real_now
print('PASS expired-pass-blocks-publication')
assert pub.remove_folder('Other', push=False)['ok']
assert 'Other' not in head_index()['patches'] and head_index()['gate'] == g2; print('PASS removal-keeps-gate')
print('FIXTURES=' + str(root.resolve()))
