from pathlib import Path
import ctypes as C, math, json
base=Path(__file__).resolve().parent
dll_path=base/'ViperDsp.dll' if (base/'ViperDsp.dll').exists() else base.parents[1]/'ViperDsp.dll'
dll=C.CDLL(str(dll_path))
dll.vp_create.argtypes=[C.c_uint32];dll.vp_create.restype=C.c_void_p
dll.vp_destroy.argtypes=[C.c_void_p]
dll.vp_set.argtypes=[C.c_void_p,C.c_int,C.c_int,C.c_int,C.c_int,C.c_int]
dll.vp_process.argtypes=[C.c_void_p,C.POINTER(C.c_float),C.c_uint32]
dll.vp_load_ir.argtypes=[C.c_void_p,C.POINTER(C.c_float),C.c_uint32,C.c_uint32]
dll.vp_processed_frames.argtypes=[C.c_void_p];dll.vp_processed_frames.restype=C.c_uint64
n=24017
signal=[f for i in range(n) for f in (.13*math.sin(i*.137),.09*math.cos(i*.077))]
patterns={'large':[100000], '480':[480], 'odd':[1,2,15,127,257,480,4095,4096,4097]}
def run(mode,pattern):
 h=dll.vp_create(48000)
 try:
  if mode=='convolver':
   ir=(C.c_float*16)(.5,*([0]*15));assert dll.vp_load_ir(h,ir,16,1)==1
   assert dll.vp_set(h,65538,1,0,0,0)==1
  if mode=='vhe': assert dll.vp_set(h,65544,1,0,0,0)==1
  if mode=='analog': assert dll.vp_set(h,65584,1,0,0,0)==1
  if mode=='fet':
   for id,val in [(65611,25),(65612,75),(65614,0),(65616,0),(65618,0),(65620,0),(65610,1)]:assert dll.vp_set(h,id,val,0,0,0)==1
  out=[];off=0;i=0
  while off<n:
   size=min(pattern[i%len(pattern)],n-off);i+=1
   arr=(C.c_float*(size*2))(*signal[off*2:(off+size)*2]);assert dll.vp_process(h,arr,size)==1
   out.extend(arr);off+=size
  assert dll.vp_processed_frames(h)==n
  assert len(out)==n*2 and all(math.isfinite(x) for x in out)
  return out
 finally:dll.vp_destroy(h)
results={}
for mode in ('dry','convolver','vhe','fet','analog'):
 outputs={name:run(mode,p) for name,p in patterns.items()}
 reference=outputs['large']
 for name,data in outputs.items():
  first=next((i//2 for i,x in enumerate(data) if abs(x)>1e-9),None)
  maximum=max(abs(a-b) for a,b in zip(reference,data))
  if mode in ('dry','convolver','vhe','fet'):assert maximum<1e-6,(mode,name,maximum)
  assert max(abs(x) for x in data[13000*2:])>1e-6
  results[f'{mode}/{name}']={'frames':n,'first_nonzero_frame':first,'maximum_difference_from_large_blocks':maximum}
expected=[0.0]*512+signal[:-512]
assert max(abs(a-b) for a,b in zip(run('dry',patterns['odd']),expected))<1e-6
path=base.parents[1]/'outputs/ViPER4Windows/verification/native-blocks.json' if (base.parents[1]/'outputs').exists() else base.parents[1]/'verification/native-blocks.json'
path.write_text(json.dumps({'passed':True,'patterns':patterns,'results':results,'notes':'AnalogX upstream startup mute rounds to processing blocks. Frame counts and finite outputs preserved.'},indent=2))
print(json.dumps(results,indent=2))
