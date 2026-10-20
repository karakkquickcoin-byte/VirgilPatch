from pathlib import Path
import sys, json, tempfile, unittest, subprocess, shutil
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import virgil_core as vc
import virgil_publish as vp
import virgil_integrity as vi
import virgil_versions as vv

class VersionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.root = Path(self.temp.name); self.repo = self.root/'repo'; self.repo.mkdir()
        self.source = self.root/'Test(3.2.0)'; self.source.mkdir()
        self.meta = {'name':'Test source','version':'4.0.0','files':[{'file':'a.ogg','id':100}]}
        (self.source/'patch.json').write_text(json.dumps(self.meta)); (self.source/'a.ogg').write_bytes(b'original')
        self.git('init','-q'); self.git('config','user.name','Fixture'); self.git('config','user.email','test@example.invalid')
        (self.repo/'index.json').write_text(json.dumps({'format':2,'published':'test','patches':{}}))
        self.git('add','index.json');self.git('commit','-qm','empty baseline')
        self.guards = [patch.object(vc,'REPO',str(self.repo)), patch.object(vc,'REPO_PATCHES',str(self.repo/'patches')),
            patch.object(vc,'load_published',lambda:[str(self.source)]),patch.object(vp,'HASH_CACHE',str(self.root/'cache.json')),
            patch.object(vp,'quick_check',lambda *a,**k:{'ok':True,'errors':[],'warnings':[]})]
        for guard in self.guards: guard.start()
        vp._hash_cache=None;vp._index_cache.clear()
    def tearDown(self):
        for guard in reversed(self.guards): guard.stop()
        vp._hash_cache=None;vp._index_cache.clear();self.temp.cleanup()
    def git(self,*args):
        p=subprocess.run(['git','-c','safe.directory='+str(self.repo),'-C',str(self.repo),*args],capture_output=True)
        if p.returncode:raise RuntimeError(p.stderr.decode())
        return p.stdout
    def push(self):
        value=vp.push_folder(str(self.source),push=False,say=lambda *a:None);self.assertTrue(value['ok'],value);return value
    def entry(self):return vp.published_index()['patches'][self.source.name]
    def test_first_push_changed_push_and_noop_keep_source_and_local_identity(self):
        original=(self.source/'patch.json').read_bytes();self.push()
        entry=self.entry();self.assertEqual(entry['version'],'5.0.0');self.assertEqual(entry['repo_dir'],'patches/Test(5.0.0)')
        self.assertEqual(self.push()['commits'],0)
        (self.source/'a.ogg').write_bytes(b'changed asset');self.push()
        entry=self.entry();self.assertEqual(entry['version'],'5.0.0.1');self.assertEqual(set(vp.published_index()['patches']),{'Test(3.2.0)'})
        self.assertEqual((self.source/'patch.json').read_bytes(),original)
        metadata=json.loads((self.repo/entry['repo_dir']/'patch.json').read_text());self.assertEqual(metadata['version'],'5.0.0.1');self.assertEqual(metadata['name'],'Test(5.0.0.1)')
        self.assertFalse((self.repo/'patches/Test(5.0.0)').exists());vi.validate_head(str(self.repo))
    def test_manifest_change_bumps_but_source_version_and_formatting_do_not(self):
        self.push();self.meta['version']='9.9.9';self.meta['name']='renamed locally'
        (self.source/'patch.json').write_text(json.dumps(self.meta,indent=3));self.assertEqual(self.push()['commits'],0)
        self.meta['url']='https://example.invalid';(self.source/'patch.json').write_text(json.dumps(self.meta));self.push()
        self.assertEqual(self.entry()['version'],'5.0.0.1')
    def test_full_publish_noop_and_changed_revision(self):
        vp.publish(push=False,skip_check=True,say=lambda *a:None)
        self.assertEqual(self.entry()['version'],'5.0.0');self.assertEqual(vp.publish(push=False,skip_check=True,say=lambda *a:None)['commits'],0)
        (self.source/'a.ogg').write_bytes(b'full update')
        vp.publish(push=False,skip_check=True,say=lambda *a:None);self.assertEqual(self.entry()['version'],'5.0.0.1')
    def test_interrupted_before_commit_reuses_next_version(self):
        self.push();(self.source/'a.ogg').write_bytes(b'pending')
        original=vi.canonicalize
        with patch.object(vi,'canonicalize',side_effect=RuntimeError('interrupted')):
            with self.assertRaises(RuntimeError):self.push()
        self.push();self.assertEqual(self.entry()['version'],'5.0.0.1');vi.validate_head(str(self.repo))
    def test_invalid_versions_and_paths_rejected(self):
        for version in ['5.0.0/evil','05.0.0','5.0','5.0.0.-1','5.0.0.1.2','5.0.0\\evil',None]:
            with self.assertRaises(ValueError):vv.display_name('Test',version)
        for path in ['patches/Other(5.0.0)','patches/../Test(5.0.0)','patches/Test']:
            with self.assertRaises(ValueError):vv.repo_dir('Test',{'version':'5.0.0','repo_dir':path})
    def test_revision_ten_and_metadata_mismatch(self):
        self.assertEqual(vv.next_version({'version':'5.0.0.9'}),'5.0.0.10')
        self.push();entry=self.entry();meta=json.loads((self.repo/entry['repo_dir']/'patch.json').read_text());meta['version']='4.0.0'
        (self.repo/entry['repo_dir']/'patch.json').write_text(json.dumps(meta));self.git('add','-A')
        with self.assertRaises(ValueError):vi.finalize(str(self.repo),vp.published_index())
    def test_case_only_metadata_does_not_repeat_release_and_changed_asset_keeps_git_casing(self):
        self.push();self.meta['files'][0]['file']='A.ogg'
        (self.source/'patch.json').write_text(json.dumps(self.meta))
        self.assertEqual(self.push()['commits'],0)
        self.assertEqual(vp.publish(push=False,skip_check=True,say=lambda *a:None)['commits'],0)
        (self.source/'a.ogg').write_bytes(b'case variation plus actual change');self.push()
        self.assertEqual(self.entry()['version'],'5.0.0.1');self.assertEqual(set(self.entry()['files']),{'a.ogg'})
        self.assertEqual(self.push()['commits'],0);vi.validate_head(str(self.repo))

    def test_only_selected_patch_advances_and_removal_preserves_source(self):
        self.push()
        other=self.root/'Other';shutil.copytree(self.source,other)
        with patch.object(vc,'load_published',lambda:[str(self.source),str(other)]):
            vp.push_folder(str(other),push=False,say=lambda *a:None)
            (self.source/'a.ogg').write_bytes(b'only selected changes');self.push()
            self.assertEqual(vp.published_index()['patches']['Other']['version'],'5.0.0')
            self.assertEqual(self.entry()['version'],'5.0.0.1')
            with patch.object(vp,'remove_published',lambda n:(True,'removed')):
                vp.remove_folder('Other',push=False,say=lambda *a:None)
            self.assertTrue((other/'patch.json').is_file());self.assertNotIn('Other',vp.published_index()['patches'])
            vi.validate_head(str(self.repo))
    def test_failed_validation_does_not_assign_release(self):
        self.push();(self.source/'a.ogg').write_bytes(b'blocked')
        with patch.object(vp,'quick_check',lambda *a,**k:{'ok':False,'errors':['invalid'],'warnings':[]}):
            self.assertFalse(vp.push_folder(str(self.source),push=False,say=lambda *a:None)['ok'])
        self.assertEqual(self.entry()['version'],'5.0.0');self.push();self.assertEqual(self.entry()['version'],'5.0.0.1')

class RemoteVersionTests(unittest.TestCase):
    def test_refresh_fast_forwards_only_and_preserves_pending_commits(self):
        for counts, merge, reject in [('1 0',True,False),('0 1',False,False),('0 0',False,False),('1 1',False,True)]:
            calls=[]
            def git(*args,**kwargs):
                calls.append(args)
                if args[0]=='rev-parse':return 'main'
                if args[0]=='rev-list':return counts
                return ''
            with patch.object(vp,'git',git):
                if reject:
                    with self.assertRaises(ValueError):vp.refresh_remote(lambda *a:None)
                else:vp.refresh_remote(lambda *a:None)
            self.assertEqual(any(c[0]=='merge' for c in calls),merge)
            if merge:self.assertIn(('merge','--ff-only','origin/main'),calls)
            self.assertEqual(calls[0],('fetch','-q','origin'))

class SparseVersionTests(VersionTests):
    def setUp(self):
        super().setUp()
        self.git('sparse-checkout','init','--no-cone')
        self.git('sparse-checkout','set','--no-cone','/index.json','/patches/*/patch.json')
    def push(self):
        result = super().push()
        self.git('sparse-checkout','reapply')
        return result
if __name__ == '__main__':unittest.main(verbosity=2)
