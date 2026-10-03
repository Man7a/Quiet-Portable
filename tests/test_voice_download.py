"""Network-free checks for interrupted downloads, bad files, and model reuse."""
import unittest,tempfile,hashlib,io,json,os,importlib.util
from pathlib import Path
from unittest.mock import patch

script=Path(__file__).resolve().parents[1]/'src/Assets/Voice/bootstrap-voice.py'
spec=importlib.util.spec_from_file_location('bootstrap',script)
bootstrap=importlib.util.module_from_spec(spec);spec.loader.exec_module(bootstrap)
DATA=b'Quiet tokenizer fixture with enough bytes for a resumed request.'
ENTRY=dict(rfilename='vocab.json',size=len(DATA),blobId=hashlib.sha1(b'blob '+str(len(DATA)).encode()+b'\0'+DATA).hexdigest())

class Response(io.BytesIO):
 def __init__(self,data,status=200,headers=None,interrupt=False):
  super().__init__(data);self.status=status;self.headers=headers or {};self.interrupt=interrupt;self.reads=0
 def geturl(self):return 'https://huggingface.co/fixture'
 def read(self,n=-1):
  self.reads+=1
  if self.interrupt and self.reads==2:raise TimeoutError('fixture interruption')
  return super().read(min(n,7) if self.interrupt else n)

class Downloads(unittest.TestCase):
 def fake(self,interrupt=False,corrupt=False,bad_range=False):
  requests=[]
  def request(req,**kwargs):
   if '/api/' in req.full_url:return Response(json.dumps(dict(sha=bootstrap.REVISION,siblings=[ENTRY])).encode())
   offset=int(req.headers['Range'].split('=')[1].split('-')[0]);requests.append(offset)
   data=(b'!'*len(DATA) if corrupt else DATA)[offset:]
   return Response(data,206,{'Content-Range':f'bytes {1 if bad_range else offset}-{len(DATA)-1}/{len(DATA)}'},interrupt and len(requests)==1)
  return requests,request
 def run_download(self,root,request):
  with patch.object(bootstrap.urllib.request,'urlopen',request),patch.object(bootstrap.time,'sleep',lambda _:None),patch.dict(os.environ,{'QUIET_TURBO_SOURCE':''}):
   return bootstrap.download_models(root)
 def test_interrupted_download_resumes_without_duplicate_bytes(self):
  with tempfile.TemporaryDirectory() as folder:
   requests,request=self.fake(interrupt=True);target=self.run_download(Path(folder),request)
   self.assertEqual((target/'vocab.json').read_bytes(),DATA);self.assertEqual(requests,[0,7])
 def test_wrong_checksum_never_commits_model(self):
  with tempfile.TemporaryDirectory() as folder:
   requests,request=self.fake(corrupt=True)
   with self.assertRaisesRegex(RuntimeError,'checksum'):self.run_download(Path(folder),request)
   self.assertEqual(len(requests),3);self.assertFalse((Path(folder)/'models/turbo/vocab.json').exists())
 def test_wrong_server_range_is_rejected(self):
  with tempfile.TemporaryDirectory() as folder:
   requests,request=self.fake(bad_range=True)
   with self.assertRaisesRegex(RuntimeError,'different download range'):self.run_download(Path(folder),request)
   self.assertEqual(len(requests),3);self.assertFalse((Path(folder)/'models/turbo/vocab.json').exists())
 def test_matching_cached_file_is_verified_and_not_downloaded(self):
  with tempfile.TemporaryDirectory() as folder:
   root=Path(folder);target=root/'models/turbo';target.mkdir(parents=True);(target/'vocab.json').write_bytes(DATA)
   requests,request=self.fake();self.run_download(root,request);self.assertEqual(requests,[])
 def test_corrupt_cached_file_is_replaced(self):
  with tempfile.TemporaryDirectory() as folder:
   root=Path(folder);target=root/'models/turbo';target.mkdir(parents=True);(target/'vocab.json').write_bytes(b'?'*len(DATA))
   requests,request=self.fake();self.run_download(root,request);self.assertEqual((target/'vocab.json').read_bytes(),DATA);self.assertEqual(requests,[0])

if __name__=='__main__':unittest.main()
