from pathlib import Path
import sys,unittest,json,hashlib,subprocess,tempfile
root=Path(__file__).parent.resolve()
sys.path.insert(0,str(root.parent))
import virgil_core as vc
import virgil_publish as vp
import virgil_integrity as vi

def run(repo,*args):
    result=subprocess.run(['git','-C',str(repo),*args],stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    if result.returncode:raise RuntimeError(result.stderr.decode('utf-8','replace'))
    return result.stdout
def sha(data):return hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
class PublisherTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='pub-',dir=root);self.repo=Path(self.temp.name)
        run(self.repo,'init','-q');run(self.repo,'config','user.name','Publisher tests');run(self.repo,'config','user.email','test@example.invalid');run(self.repo,'config','core.autocrlf','false')
        self.patch=self.repo/'patches/Test';self.patch.mkdir(parents=True)
        self.asset=self.patch/'Wallpiece05.wmo';self.asset.write_bytes(b'original asset')
        self.metadata={'name':'Test','version':'1','files':[{'file':'Wallpiece05.wmo','id':107009}]}
        self.write_metadata()
        self.index={'format':2,'published':'test','patches':{'Test':{'repo_dir':'patches/Test','files':{'Wallpiece05.wmo':{'id':107009,'size':len(self.asset.read_bytes()),'sha':sha(self.asset.read_bytes())}},'patch_json':{'size':len((self.patch/'patch.json').read_bytes()),'sha':sha((self.patch/'patch.json').read_bytes())}}}}
        (self.repo/'index.json').write_bytes(json.dumps(self.index).encode());run(self.repo,'add','-A');run(self.repo,'commit','-qm','initial valid release')
    def tearDown(self):self.temp.cleanup()
    def write_metadata(self):(self.patch/'patch.json').write_bytes(json.dumps(self.metadata).encode())
    def test_case_only_name_and_null_name_are_canonicalized(self):
        self.metadata['name']=None;self.metadata['files'][0]['file']='wallpiece05.wmo';self.write_metadata();vi.canonicalize(str(self.repo),['Test']);run(self.repo,'add','-A')
        value=vi.finalize(str(self.repo),self.index);metadata=json.loads((self.patch/'patch.json').read_text())
        self.assertEqual(metadata['name'],'Test');self.assertEqual(metadata['files'][0]['file'],'Wallpiece05.wmo');self.assertEqual(value['patches']['Test']['files']['Wallpiece05.wmo']['sha'],sha(b'original asset'))
    def test_stale_hash_and_size_rebuilt_from_staged_blob(self):
        self.asset.write_bytes(b'changed asset with extra bytes');run(self.repo,'add','--','patches/Test/Wallpiece05.wmo')
        value=vi.finalize(str(self.repo),self.index);self.assertEqual(value['patches']['Test']['files']['Wallpiece05.wmo']['size'],len(self.asset.read_bytes()));self.assertEqual(value['patches']['Test']['files']['Wallpiece05.wmo']['sha'],sha(self.asset.read_bytes()))
    def test_missing_listed_file_is_rejected(self):
        self.metadata['files'].append({'file':'missing.wmo','id':107010});self.write_metadata();run(self.repo,'add','-A')
        with self.assertRaises(ValueError):vi.finalize(str(self.repo),self.index)
    def test_duplicate_ids_are_rejected(self):
        (self.patch/'other.wmo').write_bytes(b'other');self.metadata['files'].append({'file':'other.wmo','id':107009});self.write_metadata();run(self.repo,'add','-A')
        with self.assertRaises(ValueError):vi.finalize(str(self.repo),self.index)
    def test_duplicate_json_keys_and_non_json_numbers_are_rejected(self):
        for raw in [b'{"files":[],"files":[]}',b'{"id":NaN}',b'{"id":Infinity}',b'\xff']:
            with self.assertRaises((ValueError,UnicodeError)):vi.strict_json(raw)
    def test_invalid_head_cannot_be_promoted(self):
        self.asset.write_bytes(b'inconsistent intermediate');run(self.repo,'add','-A');run(self.repo,'commit','-qm','intermediate assets without updated index')
        with self.assertRaises(ValueError):vi.validate_head(str(self.repo))
    def test_correct_hash_with_false_size_is_rejected(self):
        self.index['patches']['Test']['files']['Wallpiece05.wmo']['size']=1
        (self.repo/'index.json').write_bytes(json.dumps(self.index).encode());run(self.repo,'add','index.json');run(self.repo,'commit','-qm','bad declared size')
        with self.assertRaises(ValueError):vi.validate_head(str(self.repo))
    def test_publish_batches_never_expose_intermediate_main(self):
        remote=self.repo/'remote.git';subprocess.run(['git','init','--bare','-q',str(remote)],check=True);run(self.repo,'remote','add','origin',str(remote));run(self.repo,'push','-q','-u','origin','HEAD:main')
        initial=run(self.repo,'rev-parse','HEAD').decode().strip()
        self.asset.write_bytes(b'asset change awaiting complete release');run(self.repo,'add','--','patches/Test/Wallpiece05.wmo');run(self.repo,'commit','-qm','asset batch')
        vi.finalize(str(self.repo),self.index);run(self.repo,'commit','-qm','complete manifest')
        final=run(self.repo,'rev-parse','HEAD').decode().strip();old_repo=vc.REPO;old_git=vp.git;old_helper=vp.ensure_credential_helper;seen=[]
        vc.REPO=str(self.repo);vp.ensure_credential_helper=lambda:None
        def traced(*args,**kw):
            result=old_git(*args,**kw)
            if args[0]=='push':seen.append((args,run(remote,'rev-parse','refs/heads/main').decode().strip()))
            return result
        vp.git=traced
        try:self.assertEqual(vp.push_pending(say=lambda message:None),2)
        finally:vc.REPO=old_repo;vp.git=old_git;vp.ensure_credential_helper=old_helper
        self.assertEqual([value for args,value in seen[:-1]],[initial,initial]);self.assertEqual(seen[-1][1],final)
        self.assertIn('--atomic',seen[-1][0]);self.assertEqual(run(remote,'for-each-ref','--format=%(refname)','refs/heads/virgil-upload-').strip(),b'')

    def test_existing_staged_changes_block_publish(self):
        old=vc.REPO;vc.REPO=str(self.repo)
        try:
            vp.require_clean_stage()
            self.asset.write_bytes(b'unrelated staged edit');run(self.repo,'add','--','patches/Test/Wallpiece05.wmo')
            with self.assertRaises(ValueError):vp.require_clean_stage()
        finally:vc.REPO=old

    def test_full_mirror_preserves_unpublished_directories(self):
        extra=self.repo/'patches/PrivateUnpublished';extra.mkdir();(extra/'keep.bin').write_bytes(b'private')
        saved={key:getattr(vc,key) for key in ['REPO','REPO_PATCHES']};vc.REPO=str(self.repo);vc.REPO_PATCHES=str(self.repo/'patches')
        original=vp.save_hash_cache;vp.save_hash_cache=lambda:None
        try:
            vp.sync_repo([])
            self.assertEqual((extra/'keep.bin').read_bytes(),b'private');self.assertFalse(self.patch.exists())
        finally:
            vp.save_hash_cache=original
            for key,value in saved.items():setattr(vc,key,value)

if __name__=='__main__':unittest.main(verbosity=2)
