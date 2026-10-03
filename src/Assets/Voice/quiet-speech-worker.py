"""Offline JSON-lines speech worker. Stdout is reserved for responses."""
import sys, os, json, io, wave, base64, re, subprocess, contextlib, atexit
from pathlib import Path
os.environ['HF_HUB_OFFLINE']='1'
os.environ['HF_HUB_DISABLE_TELEMETRY']='1'
VOICE_ROOT=Path(os.environ.get('QUIET_VOICE_RUNTIME',str(Path(__file__).resolve().parents[1]/'VoiceRuntime')))
models={}; voices={}
EVENT_TAGS=re.compile(r'\[(?:clear throat|sigh|shush|cough|groan|sniff|gasp|chuckle|laugh)\]',re.I)
def spoken_text(text):return EVENT_TAGS.sub('',text).strip()
def wave_bytes(samples,rate):
 import numpy as np
 pcm=(np.asarray(samples).clip(-1,1)*32767).astype('<i2');buf=io.BytesIO()
 with wave.open(buf,'wb') as f:f.setnchannels(1);f.setsampwidth(2);f.setframerate(rate);f.writeframes(pcm.tobytes())
 return buf.getvalue()

MAP={**dict.fromkeys(['P','B','M'],1),**dict.fromkeys(['F','V'],6),**dict.fromkeys(['TH','DH'],8),'L':7,**dict.fromkeys(['AA','AE','AH','EH'],2),**dict.fromkeys(['AO','ER','UH'],4),**dict.fromkeys(['UW','W'],5),**dict.fromkeys(['SIL','SP','SPN'],0)}

def alignment_text(text):
 # Normalize simple English counts only. Currency, decimals, dates and unknown
 # pronunciations still use the explicitly approximate audio-driven fallback.
 small='zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen'.split()
 tens=['','','twenty','thirty','forty','fifty','sixty','seventy','eighty','ninety']
 def count(match):
  value=match.group()
  if len(value)>2 or (len(value)>1 and value.startswith('0')):return value
  n=int(value)
  return small[n] if n<20 else tens[n//10]+(' '+small[n%10] if n%10 else '')
 if re.search(r'[$£€%]|\d[.,/:-]\d',text):return text
 return re.sub(r'\b[0-9]+\b',count,text)

def approximate_cues(samples,rate):
 import numpy as np
 pcm=np.asarray(samples,dtype=float);step=max(1,int(rate*.02));levels=[]
 for i in range(0,len(pcm),step):levels.append(float(np.sqrt(np.mean(pcm[i:i+step]**2))))
 peak=max(.025,float(np.percentile(levels,85))) if levels else .025
 quiet=max(.008,peak*.09);full=max(quiet+.01,peak*.8);cues=[]
 for j,level in enumerate(levels):
  amount=max(0,min(1,(level-quiet)/(full-quiet)))
  amount=amount*amount*(3-2*amount)
  pose=0 if amount<.025 else 3 if amount<.6 else 2
  cues.append(dict(start=j*step/rate,end=min((j+1)*step/rate,len(pcm)/rate),pose=pose,strength=amount if pose else 1))
 return cues
def synth(text,engine,reference):
 if engine!='chatterbox-turbo':raise ValueError('This edition supports Chatterbox Turbo only')
 import torch
 from chatterbox.tts_turbo import ChatterboxTurboTTS
 if not torch.cuda.is_available():raise RuntimeError('Turbo requires a supported NVIDIA GPU and driver. The avatar can still run silently.')
 if engine not in models:
  models[engine]=ChatterboxTurboTTS.from_local(VOICE_ROOT/'models/turbo',device='cuda')
  voices['default']=models[engine].conds
 model=models[engine]
 key=(reference,Path(reference).stat().st_mtime_ns) if reference else None
 if key and voices.get('reference')!=key:
  model.prepare_conditionals(reference,norm_loudness=True);voices['reference']=key
 elif key is None:
  model.conds=voices['default'];voices.pop('reference',None)
 with torch.inference_mode():audio=model.generate(text,temperature=.8,top_p=.95,top_k=1000,repetition_penalty=1.2)
 return audio.detach().cpu().numpy().flatten(),model.sr

def bundle(text,samples,rate,original_wave=None):
 import numpy as np
 pcm=(np.asarray(samples).clip(-1,1)*32767).astype('<i2'); buf=io.BytesIO()
 with wave.open(buf,'wb') as f:f.setnchannels(1);f.setsampwidth(2);f.setframerate(rate);f.writeframes(pcm.tobytes())
 raw=original_wave or buf.getvalue();duration=len(pcm)/rate;warnings=[];cues=[]
 try:
  from pocketsphinx import Decoder
  normalized=alignment_text(text.lower().replace('’',"'"))
  words=re.findall(r"[a-z]+(?:'[a-z]+)?",normalized)
  decoder=Decoder(samprate=16000,frate=100,loglevel='ERROR',beam=1e-100,wbeam=1e-80,cmn='batch')
  if not words or re.search(r'\d',normalized) or any(not decoder.lookup_word(w) for w in words):raise ValueError('Unfamiliar words or numbers')
  from scipy.signal import resample_poly
  from math import gcd
  divisor=gcd(rate,16000)
  data=(resample_poly(np.asarray(samples),16000//divisor,rate//divisor).clip(-1,1)*32767).astype('<i2').tobytes()
  decoder.set_align_text(' '.join(words))
  def run():decoder.start_utt();decoder.process_raw(data,full_utt=True);decoder.end_utt()
  run();segments=[(x.word,x.start_frame/100,(x.end_frame+1)/100) for x in decoder.seg()];phones=[]
  try:
   decoder.set_alignment();run();alignment=decoder.get_alignment()
   if alignment is None:raise RuntimeError('No alignment')
   for word in alignment:
    for phone in word:phones.append((phone.start/100,(phone.start+phone.duration)/100,phone.name))
   method='Aligned speech sounds'
  except RuntimeError:
   method='Aligned words; estimated sound timing'
   for word,a,b in segments:
    if word.startswith('<'):continue
    names=decoder.lookup_word(word).split()
    for i,name in enumerate(names):phones.append((a+(b-a)*i/len(names),a+(b-a)*(i+1)/len(names),name))
  for a,b,name in phones:
   name=re.sub(r'\d','',name.upper());b=min(b,duration);a=max(a,0)
   if b<=a:continue
   seq={'AY':[2,3],'EY':[2,3],'OW':[4,5],'AW':[2,5],'OY':[4,3]}.get(name,[MAP.get(name,0 if name.startswith('<') else 3)])
   for i,pose in enumerate(seq):cues.append(dict(start=a+(b-a)*i/len(seq),end=a+(b-a)*(i+1)/len(seq),pose=pose))
  cues.sort(key=lambda c:c['start'])
  if not cues or any(cues[i]['start']<cues[i-1]['end']-.001 for i in range(1,len(cues))):raise ValueError('Invalid alignment')
 except Exception:
  # Honest amplitude-based fallback for names, numbers and unsupported dictionary words.
  method='Audio-driven mouth opening (approximate)';warnings.append('Precise speech-sound timing was unavailable for this sentence.')
  cues=approximate_cues(samples,rate)
 return dict(schema='quiet-voice-v1',name='Quiet read-aloud',duration=duration,transcript=text,method=method,warnings=warnings,cues=cues,audio='data:audio/wav;base64,'+base64.b64encode(raw).decode())
def handle(request):
 text=request['text'].strip()
 if not text or len(text)>1200:raise ValueError('Sentence must contain 1–1200 characters')
 engine=request.get('engine','chatterbox-turbo')
 if engine!='chatterbox-turbo':raise ValueError('This edition supports Chatterbox Turbo only')
 if request.get('action')=='align':
  import numpy as np
  raw=base64.b64decode(request['audio'],validate=True)
  with wave.open(io.BytesIO(raw),'rb') as f:
   if f.getnchannels()!=1 or f.getsampwidth()!=2:raise ValueError('Expected mono PCM16 audio')
   rate=f.getframerate();samples=np.frombuffer(f.readframes(f.getnframes()),dtype='<i2').astype(float)/32768
  result=bundle(spoken_text(text),samples,rate,original_wave=raw)
 else:
  samples,rate=synth(text,engine,request.get('reference',''))
  result=bundle(spoken_text(text),samples,rate)
 return dict(ok=True,engine=engine,fallback=False,bundle=result)

if __name__=='__main__':
 for line in sys.stdin:
  try:
   with contextlib.redirect_stdout(sys.stderr):result=handle(json.loads(line))
   print(json.dumps(result),flush=True)
  except Exception as e:print(json.dumps(dict(ok=False,error=str(e))),flush=True)
