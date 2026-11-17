import datetime as dt, json, os, pathlib, shutil, subprocess, tempfile, zipfile
import sys
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import virgil_core as vc, virgil_publish as pub, virgil_lock as lock
root=pathlib.Path(__file__).parent / 'test-results' / ('lock-' + lock.uuid.uuid4().hex[:8]); root.mkdir(parents=True)
repo=root/'repo'; repo.mkdir(); work=root/'private'; work.mkdir()
vc.REPO=str(repo.resolve()); vc.REPO_PATCHES=str(repo/'patches'); vc.WORK=str(work.resolve()); pub.HASH_CACHE=str(work/'hash_cache.json')
vc.PUBLISHED_FILE=str(work/'published.json')
pub.git('init','-q','-b','main');pub.git('config','user.name','Encryption tests');pub.git('config','user.email','test@invalid.local')
(repo/'README.md').write_text('Synthetic encryption fixture')
pub.git('add','README.md');pub.git('commit','-qm','fixture')
a=root/'Test';b=root/'Other';a.mkdir();b.mkdir()
def patch(path,files):
    entries=[]
    for i,(name,data) in enumerate(files.items(),1):
        (path/name).write_bytes(data); entries.append({'file':name,'id':i})
    (path/'patch.json').write_text(json.dumps({'name':path.name,'version':'1','files':entries}))
patch(a,{'spoiler.ogg':b'first secret','same.ogg':b'unchanged'});patch(b,{'other.ogg':b'another patch'})
vc.load_published=lambda:[str(a.resolve()),str(b.resolve())]
vc.atomic_json(lock.secret_path(),{'format':1,'recovery_password':'fixture-recovery-only','history':[]})
def rejects(fn):
    try:fn()
    except Exception:return
    raise AssertionError('Expected rejection')
rejects(lambda:pub.push_folder(str(a.resolve()),push=False)); print('PASS unset-password-blocks-publication')
lock.set_password('fixture-week-one')
rep=pub.push_folder(str(a.resolve()),push=False); assert rep['ok'],rep
outer=json.loads((repo/'index.json').read_text()); assert b'spoiler' not in (repo/'index.json').read_bytes()
rejects(lambda:lock.unlock(outer,'wrong code'))
cat=lock.unlock(outer,'fixture-week-one'); assert cat==lock.unlock(outer,'fixture-recovery-only')
assert len(cat['index']['patches'])==2
assert not any(p.startswith('patches/') for p in pub.vi.records(str(repo),'HEAD'))
print('PASS encrypted-publication-hides-names-and-payloads; weekly-and-recovery-unlock')
def snapshot(name):
    target=root/name; target.mkdir(); archive=root/(name+'.zip')
    pub.git('archive','--format=zip','-o',str(archive.resolve()),'HEAD')
    with zipfile.ZipFile(archive) as z:z.extractall(target)
    return target
snapshot('week1')
old_other=cat['index']['patches']['Other']; same_key=next(v for k,v in cat['objects'].items() if k.endswith('/same.ogg'))
lock.set_password('fixture-week-two')
patch(a,{'spoiler.ogg':b'second secret','same.ogg':b'unchanged'})
assert pub.push_folder(str(a.resolve()),push=False)['ok']
outer=json.loads((repo/'index.json').read_text());rejects(lambda:lock.unlock(outer,'fixture-week-one'))
cat=lock.unlock(outer,'fixture-week-two'); assert cat['index']['patches']['Other']==old_other
assert next(v for k,v in cat['objects'].items() if k.endswith('/same.ogg'))==same_key
print('PASS weekly-rotation-rejects-old-code; unchanged-assets-reused; other-folder-preserved')
snapshot('week2')
# Test ciphertext corruption rejection at the actual Git publication gate.
obj=next(iter(cat['objects'].values())); path=repo/obj['path']; raw=path.read_bytes(); path.write_bytes(raw[:-1]+bytes([raw[-1]^1]))
pub.git('add',obj['path']);pub.git('commit','-qm','tamper fixture')
rejects(lambda:lock.verify_head(str(repo)))
pub.git('revert','--no-edit','HEAD')
print('PASS publication-gate-rejects-corrupt-ciphertext')
rejects(lambda:lock.set_password('fixture-week-one'))
real_now=lock.now;lock.now=lambda:real_now()+dt.timedelta(days=8)
rejects(lambda:pub.push_folder(str(a.resolve()),push=False));lock.now=real_now
print('PASS expired-and-reused-passwords-rejected')
assert pub.remove_folder('Other',push=False)['ok']; assert 'Other' not in pub.published_index()['patches']
print('PASS encrypted-folder-removal')
(root/'fixture.json').write_text(json.dumps({'root':str(root.resolve())}))
print('FIXTURES='+str(root.resolve()))
