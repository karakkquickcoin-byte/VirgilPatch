# Local audit of plaintext migration: begin with a committed legacy patch and migrate it.
import pathlib, json
import sys
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import virgil_core as vc, virgil_publish as pub, virgil_lock as lock
root=pathlib.Path('test-results')/('migration-'+lock.uuid.uuid4().hex[:8]);root.mkdir(parents=True)
repo=root/'repo';repo.mkdir();source=root/'Legacy';source.mkdir();work=root/'private';work.mkdir()
vc.REPO=str(repo.resolve());vc.REPO_PATCHES=str(repo/'patches');vc.WORK=str(work.resolve());pub.HASH_CACHE=str(work/'hash.json')
vc.load_published=lambda:[str(source.resolve())]
(source/'secret.ogg').write_bytes(b'legacy-public')
(source/'patch.json').write_text(json.dumps({'name':'Legacy','version':'1','files':[{'file':'secret.ogg','id':1}]}))
pub.git('init','-q','-b','main');pub.git('config','user.name','fixture');pub.git('config','user.email','fixture@invalid.local')
assert pub.push_folder(str(source.resolve()),push=False)['ok']
assert any(p.startswith('patches/') for p in pub.vi.records(str(repo),'HEAD'))
lock.set_password('fixture-migration',master='fixture-recovery')
assert pub.push_folder(str(source.resolve()),push=False)['ok']
assert not any(p.startswith('patches/') for p in pub.vi.records(str(repo),'HEAD'))
lock.verify_head(str(repo))
print('PASS legacy-plaintext-to-encrypted-snapshot; source-preserved')
# Deleted local record must fail closed for an already encrypted repository.
pathlib.Path(lock.secret_path()).rename(pathlib.Path(lock.secret_path()).with_suffix('.backup'))
reject=False
try:pub.push_folder(str(source.resolve()),push=False)
except ValueError:reject=True
assert reject
print('PASS missing-local-password-record-fails-closed')
