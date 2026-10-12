import importlib.util, pathlib, unittest
p=pathlib.Path(__file__).resolve().parents[1]/'scripts/scan-public.py'
s=importlib.util.spec_from_file_location('scan',p);scan=importlib.util.module_from_spec(s);s.loader.exec_module(scan)
class ScanTests(unittest.TestCase):
 def test_reports_category_without_secret(self):
  value='ghp_'+'a'*36
  result=scan.inspect('source.txt',value.encode())
  self.assertEqual(result,[('source.txt',1,'credential-token')]);self.assertNotIn(value,str(result))
 def test_excluded_file_and_nontext(self):
  self.assertIn(('releases/client.exe',0,'excluded-file'),scan.inspect('releases/client.exe',b'\xff'))
 def test_public_placeholders(self):
  self.assertEqual(scan.inspect('config.txt',b'relay.example.invalid <device-id> <token>'),[])
