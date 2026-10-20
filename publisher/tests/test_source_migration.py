import unittest,json,tempfile,sys
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import virgil_core as vc
import virgil_publish as vp
class SourceMigrationTests(unittest.TestCase):
 def test_source_paths_follow_rename_without_rewriting_configuration(self):
  with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as tmp:
   root=Path(tmp);old=root/'Test(3.2.0)';new=root/'Test(5.0.0)';old.mkdir();config=root/'published.json';config.write_text(json.dumps({'patches':[str(old)]}));original=config.read_bytes()
   with patch.object(vc,'PUBLISHED',str(config)):
    self.assertEqual(vc.load_published(),[str(old)]);old.rename(new);self.assertEqual(vc.load_published(),[str(new)]);self.assertEqual(config.read_bytes(),original)
 def test_migrated_source_retains_stable_key_and_alternative_boundaries(self):
  vp._source_key_aliases.clear()
  with patch.object(vp,'published_index',lambda:{'patches':{'!Chronicles-TexturePatch(3.2.0)':{},'Chronicles-Classic':{}}}):
   self.assertEqual(vp.pname(r'E:\test\!Chronicles-TexturePatch(5.0.0)'),'!Chronicles-TexturePatch(3.2.0)')
   self.assertEqual(vp.pname(r'E:\test\Chronicles-Classic(5.0.0)'),'Chronicles-Classic')
  self.assertTrue(vc.exclusive_alternatives('Chronicles-Classic(5.0.0)','!Chronicles-TexturePatch(5.0.0)'))
  self.assertFalse(vc.exclusive_alternatives('Other(5.0.0)','!Chronicles-TexturePatch(5.0.0)'))
  vp._source_key_aliases.clear()
if __name__=='__main__':unittest.main(verbosity=2)
