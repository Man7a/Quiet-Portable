"""Download the pinned Turbo files and verify inference before marking setup ready."""
import os,sys,json,hashlib,contextlib,ssl,urllib.request,time,shutil
from pathlib import Path

REVISION='749d1c1a46eb10492095d68fbcf55691ccf137cd'
REPO='ResembleAI/chatterbox-turbo'

def digest(path,entry):
 size=path.stat().st_size
 if size!=entry['size']:return False
 lfs=entry.get('lfs')
 h=hashlib.sha256() if lfs else hashlib.sha1()
 if not lfs:h.update(('blob '+str(size)+'\0').encode())
 with path.open('rb') as f:
  while block:=f.read(4*1024*1024):h.update(block)
 return h.hexdigest()==(lfs['sha256'] if lfs else entry['blobId'])

def download_models(root):
 import certifi
 context=ssl.create_default_context(cafile=certifi.where())
 base='https://huggingface.co/'
 def open_url(url,start=None,end=None):
  headers={'User-Agent':'Quiet-Portable-2.8.0'}
  if start is not None:headers['Range']=f'bytes={start}-{end}'
  response=urllib.request.urlopen(urllib.request.Request(url,headers=headers),timeout=30,context=context)
  if not response.geturl().startswith('https://'):response.close();raise RuntimeError('Download redirected away from HTTPS')
  return response
 with open_url(base+'api/models/'+REPO+'/revision/'+REVISION+'?blobs=true') as response:manifest=json.load(response)
 if manifest['sha']!=REVISION:raise RuntimeError('Model revision did not match')
 files=[f for f in manifest['siblings'] if f['rfilename'] in ('t3_turbo_v1.safetensors','s3gen_meanflow.safetensors','ve.safetensors') or f['rfilename'].endswith(('.json','.txt','.pt','.model')) or f['rfilename'] in ('README.md','LICENSE')]
 destination=root/'models/turbo';destination.mkdir(parents=True,exist_ok=True)
 existing=Path(os.environ['QUIET_TURBO_SOURCE']) if os.environ.get('QUIET_TURBO_SOURCE') else None
 for entry in files:
  name=entry['rfilename']
  if '/' in name or '\\' in name or name in ('.','..'):raise RuntimeError('Unexpected model filename')
  target=destination/name
  if target.is_file() and digest(target,entry):print('Verified '+name,flush=True);continue
  if existing and (existing/name).is_file() and digest(existing/name,entry):
   print('Reusing verified '+name,flush=True);shutil.copyfile(existing/name,target);continue
  partial=destination/(name+'.part')
  if partial.exists() and partial.stat().st_size>entry['size']:partial.unlink()
  print('Downloading '+name,flush=True)
  for attempt in range(3):
   try:
    while (offset:=partial.stat().st_size if partial.exists() else 0)<entry['size']:
     end=min(offset+32*1024*1024-1,entry['size']-1)
     with open_url(base+REPO+'/resolve/'+REVISION+'/'+name,offset,end) as response:
      if response.status==206:
       if not response.headers.get('Content-Range','').startswith(f'bytes {offset}-{end}/'):
        raise RuntimeError('Server returned a different download range')
       remaining=end-offset+1
      elif response.status==200:
       if offset:
        partial.unlink();raise RuntimeError('Server requires a fresh download; retrying')
       remaining=entry['size']
      else:raise RuntimeError('Unexpected download response')
      with partial.open('ab') as out:
       while remaining:
        block=response.read(min(1024*1024,remaining))
        if not block:raise RuntimeError('Download interrupted')
        out.write(block);remaining-=len(block)
     print(name+f' · {partial.stat().st_size*100//entry["size"]}%',flush=True)
    if not digest(partial,entry):partial.unlink();raise RuntimeError('Model checksum did not match')
    partial.replace(target);break
   except Exception:
    if attempt==2:raise
    print('Retrying '+name+'…',flush=True);time.sleep(2)
 (destination/'quiet-model-manifest.json').write_text(json.dumps(dict(repo=REPO,revision=REVISION,files=files),indent=2),encoding='utf-8')
 return destination

def main():
 root=Path(os.environ['QUIET_VOICE_RUNTIME']).resolve()
 os.environ.update(HF_HOME=str(root/'hf-cache'),HF_HUB_DISABLE_XET='1',HF_HUB_DISABLE_TELEMETRY='1')
 import torch
 if not torch.cuda.is_available():raise RuntimeError('A supported NVIDIA GPU and driver are required for Turbo. Quiet can still run silently.')
 print('GPU: '+torch.cuda.get_device_name(0),flush=True)
 print('Downloading Chatterbox Turbo weights (several GB). Existing files are reused.',flush=True)
 model_path=download_models(root)
 for required in ('t3_turbo_v1.safetensors','s3gen_meanflow.safetensors','ve.safetensors','conds.pt','vocab.json','merges.txt'):
  if not (model_path/required).is_file():raise RuntimeError('Model file missing: '+required)
 # Validation must work offline; installing alone is not a successful setup.
 os.environ['HF_HUB_OFFLINE']='1'
 print('Testing offline voice generation and mouth timing…',flush=True)
 with contextlib.redirect_stdout(sys.stderr):
  from chatterbox.tts_turbo import ChatterboxTurboTTS
  from pocketsphinx import Decoder
  from scipy.signal import resample_poly
  Decoder(samprate=16000,loglevel='ERROR')
  model=ChatterboxTurboTTS.from_local(model_path,device='cuda')
  if model.conds is None:raise RuntimeError('Default voice is missing from this model revision')
  quiet_voice=Path(__file__).with_name('F_Quiet.wav')
  if quiet_voice.is_file():model.prepare_conditionals(str(quiet_voice),norm_loudness=True)
  with torch.inference_mode():audio=model.generate('Hello there. Your Quiet voice is ready.')
  import numpy as np,wave
  pcm=(audio.detach().cpu().numpy().flatten().clip(-1,1)*32767).astype('<i2')
  if len(pcm)<model.sr*.2 or not np.isfinite(pcm).all():raise RuntimeError('Voice test returned invalid audio')
  with wave.open(str(root/'voice-test.wav'),'wb') as out:
   out.setnchannels(1);out.setsampwidth(2);out.setframerate(model.sr);out.writeframes(pcm.tobytes())
  import importlib.util
  spec=importlib.util.spec_from_file_location('quiet_worker',Path(__file__).with_name('quiet-speech-worker.py'))
  worker=importlib.util.module_from_spec(spec);spec.loader.exec_module(worker)
  bundle=worker.bundle('Hello there. Your Quiet voice is ready.',pcm.astype(float)/32767,model.sr)
  (root/'voice-test.json').write_text(json.dumps(bundle),encoding='utf-8')
 python=Path(sys.executable).resolve().relative_to(root).as_posix()
 ready=dict(schema=1,engine='chatterbox-turbo',python=python,modelRevision=REVISION,
  chatterboxCommit='5de7a54aa4e5e2baadb0182dde554908b48b85c2',gpu=torch.cuda.get_device_name(0))
 temp=root/'ready.tmp';temp.write_text(json.dumps(ready,indent=2),encoding='utf-8');temp.replace(root/'ready.json')
 print('Voice ready. Close setup and press Read in Quiet.',flush=True)

if __name__=='__main__':
 try:main()
 except Exception as error:
  print('Setup failed: '+str(error),file=sys.stderr,flush=True);sys.exit(1)
