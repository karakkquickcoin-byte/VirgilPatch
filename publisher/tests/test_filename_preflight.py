from pathlib import Path
import json,unittest,os
from unittest.mock import patch
from test_versions import VersionTests
import virgil_core as vc,virgil_publish as vp,virgil_integrity as vi
class FilenameTests(VersionTests):
 def setUp(self):
  super().setUp();self.backups=patch.object(vc,'BACKUPS',str(self.root/'backups'));self.backups.start()
 def tearDown(self):
  self.backups.stop();super().tearDown()
 def source_files(self,names):
  for p in self.source.rglob('*'):
   if p.is_file():p.unlink()
  self.meta['files']=[{'file':n,'id':100+i} for i,n in enumerate(names)]
  for n in names:
   p=self.source/n.lower();p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b'asset '+n.encode())
  (self.source/'patch.json').write_bytes(json.dumps(self.meta).encode())
 def exact(self,name):return (vc.tree(str(self.source)).get(name.casefold()) or [None])[0]==name
 def test_new_models_nested_directories_fixed_before_first_publish(self):
  self.source_files(['World/Castle.wmo','Creature/Dragon.m2','Textures/Stone.blp']);before={n:vc.git_blob_sha(str(self.source/n.lower())) for n in [e['file'] for e in self.meta['files']]};self.push()
  for n,h in before.items():self.assertTrue(self.exact(n));self.assertEqual(vc.git_blob_sha(str(self.source/n)),h)
  vi.validate_head(str(self.repo));self.assertTrue(list((self.root/'backups').glob('*/receipt.json')))
 def test_stable_published_spelling_repairs_metadata_and_noop_path(self):
  self.push();head=self.git('rev-parse','HEAD');p=self.source/'a.ogg';p.rename(self.source/'temporary');(self.source/'temporary').rename(self.source/'A.ogg');self.meta['files'][0]['file']='A.ogg';(self.source/'patch.json').write_text(json.dumps(self.meta));result=self.push();self.assertEqual(result['commits'],0);self.assertEqual(self.git('rev-parse','HEAD'),head);self.assertTrue(self.exact('a.ogg'));self.assertEqual(json.loads((self.source/'patch.json').read_text())['files'][0]['file'],'a.ogg')
 def test_missing_file_blocks_before_any_filename_changes(self):
  self.source_files(['Castle.wmo','Missing.wmo']);(self.source/'missing.wmo').unlink();original=(self.source/'patch.json').read_bytes()
  with self.assertRaises(ValueError):self.push()
  self.assertTrue(self.exact('castle.wmo'));self.assertEqual((self.source/'patch.json').read_bytes(),original)
 def test_full_publish_cannot_skip_filename_preflight(self):
  self.source_files(['Arbitrary.ogg']);result=vp.publish(push=False,skip_check=True,say=lambda m:None);self.assertTrue(result['ok']);self.assertTrue(self.exact('Arbitrary.ogg'));vi.validate_head(str(self.repo))
 def test_readonly_scan_reports_case_without_repairing(self):
  self.source_files(['Arbitrary.ogg']);scan=vp.scan_folder(str(self.source));self.assertEqual(len(scan['filename_mismatches']),1);self.assertIn('filename casing',vp.scan_summary(scan));self.assertTrue(self.exact('arbitrary.ogg'))
 def test_rename_failure_rolls_back_names_and_exact_metadata(self):
  self.source_files(['A.ogg','B.ogg']);original=(self.source/'patch.json').read_bytes();rename=os.rename;failed=[False]
  def injected(a,b):
   if str(b).endswith('B.ogg') and not failed[0]:failed[0]=True;raise OSError('injected destination failure')
   return rename(a,b)
  with patch('os.rename',side_effect=injected):
   with self.assertRaises(OSError):vi.repair_source_filenames(str(self.source))
  self.assertTrue(self.exact('a.ogg'));self.assertTrue(self.exact('b.ogg'));self.assertEqual((self.source/'patch.json').read_bytes(),original);receipts=list((self.root/'backups').glob('*/receipt.json'));self.assertEqual(json.loads(receipts[0].read_text())['status'],'rolled back')
 def test_unmanaged_extra_files_and_metadata_spelling_preserved(self):
  self.source_files(['Castle.wmo']);(self.source/'Keep.txt').write_bytes(b'private');p=self.source/'patch.json';p.rename(self.source/'temporary');(self.source/'temporary').rename(self.source/'Patch.JSON');vi.repair_source_filenames(str(self.source));self.assertTrue(self.exact('Castle.wmo'));self.assertTrue(self.exact('patch.json'));self.assertEqual((self.source/'Keep.txt').read_bytes(),b'private')
 def test_inconsistent_directory_names_block_without_mutation(self):
  self.source_files(['World/A.ogg','world/B.ogg']);original=(self.source/'patch.json').read_bytes()
  with self.assertRaises(ValueError):vi.repair_source_filenames(str(self.source))
  self.assertEqual((self.source/'patch.json').read_bytes(),original)
 def test_resumed_upload_rejects_committed_metadata_case_mismatch(self):
  self.push();entry=self.entry();p=self.repo/entry['repo_dir']/'patch.json';m=json.loads(p.read_text());m['files'][0]['file']='A.ogg';p.write_text(json.dumps(m));self.git('add','-A');self.git('commit','-qm','invalid filename snapshot');calls=[];original=vp.git
  def traced(*a,**k):calls.append(a);return original(*a,**k)
  with patch.object(vp,'git',side_effect=traced),patch.object(vp,'ensure_credential_helper',lambda:None):
   with self.assertRaises(ValueError):vp.push_pending(say=lambda m:None)
  self.assertFalse(any(a[0]=='push' for a in calls))
def load_tests(loader,tests,pattern):return loader.loadTestsFromTestCase(FilenameTests)
if __name__=='__main__':unittest.main(verbosity=2)
