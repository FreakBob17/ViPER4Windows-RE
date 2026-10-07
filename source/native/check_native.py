from pathlib import Path
import ctypes as C,math,json,hashlib,struct
base=Path(__file__).resolve().parent
dll_path=base/'ViperDsp.dll' if (base/'ViperDsp.dll').exists() else base.parents[1]/'ViperDsp.dll'
dll=C.CDLL(str(dll_path))
dll.vp_create.argtypes=[C.c_uint32];dll.vp_create.restype=C.c_void_p
dll.vp_destroy.argtypes=[C.c_void_p]
dll.vp_set.argtypes=[C.c_void_p,C.c_int,C.c_int,C.c_int,C.c_int,C.c_int]
dll.vp_process.argtypes=[C.c_void_p,C.POINTER(C.c_float),C.c_uint32]
dll.vp_load_ir.argtypes=[C.c_void_p,C.POINTER(C.c_float),C.c_uint32,C.c_uint32]
dll.vp_load_ddc.argtypes=[C.c_void_p,C.POINTER(C.c_float),C.POINTER(C.c_float),C.c_uint32]
dll.vp_param_size.restype=C.c_uint32
dll.vp_snapshot.argtypes=[C.c_void_p,C.POINTER(C.c_uint8),C.c_uint32];dll.vp_snapshot.restype=C.c_uint32
dll.vp_engine_version.restype=C.c_char_p
dll.vp_convolver_kernel_id.argtypes=[C.c_void_p];dll.vp_convolver_kernel_id.restype=C.c_uint32
rate=48000;frames=65536
source=[0.15*math.sin(2*math.pi*173*i/rate)+0.05*math.sin(2*math.pi*4231*i/rate) for i in range(frames)]
stereo=[x for x in source for _ in range(2)]

def process(commands=(),ir=None,ddc=None):
 h=dll.vp_create(rate);assert h
 try:
  for id,a,b in commands:assert dll.vp_set(h,id,a,b,0,0)==1,(id,a,b)
  if ir is not None:
   arr=(C.c_float*len(ir))(*ir);assert dll.vp_load_ir(h,arr,len(ir),1)==1
  if ddc is not None:
   arr=(C.c_float*len(ddc))(*ddc);assert dll.vp_load_ddc(h,arr,arr,len(ddc)//5)==1
  output=(C.c_float*len(stereo))(*stereo);assert dll.vp_process(h,output,frames)==1
  snap=(C.c_uint8*dll.vp_param_size())();assert dll.vp_snapshot(h,snap,len(snap))==len(snap)
  assert all(math.isfinite(x) for x in output)
  return list(output)
 finally:dll.vp_destroy(h)

baseline=process()
expected=[0.0]*512+stereo[:-512]
assert max(abs(a-b) for a,b in zip(baseline,expected))<1e-6,'Bypass delay mismatch'
tests={
 'volume_half':([(65586,50,0)],None,None),
 'pan_left':([(65587,-100,0)],None,None),
 'limiter':([(65588,10,0)],None,None),
 'eq':([(65552,2,600),(65551,1,0)],None,None),
 'bass':([(65576,80,0),(65577,300,0),(65574,1,0)],None,None),
 'clarity':([(65580,200,0),(65578,1,0)],None,None),
 'surround':([(65554,200,0),(65555,150,0),(65553,1,0)],None,None),
 'diff_surround':([(65558,1000,0),(65557,1,0)],None,None),
 'reverb':([(65563,50,0),(65559,1,0)],None,None),
 'agc':([(65566,100,0),(65568,400,0),(65565,1,0)],None,None),
 'dynamic':([(65573,200,0),(65569,1,0)],None,None),
 'cure':([(65581,1,0)],None,None),
 'tube':([(65583,1,0)],None,None),
 'analog':([(65584,1,0)],None,None),
 'vhe':([(65545,2,0),(65544,1,0)],None,None),
 'convolver':([(65538,1,0)], [0.5]+[0.0]*31,None),
 'ddc':([(65546,1,0)],None,[0.5,0,0,0,0]),
 'fet':([(65611,25,0),(65612,75,0),(65614,0,0),(65616,0,0),(65618,0,0),(65620,0,0),(65610,1,0)],None,None),
 'fet_auto':([(65611,60,0),(65612,80,0),(65610,1,0)],None,None),
 'speaker':([(65603,1,0)],None,None),
 'spectrum':([(65550,200,0),(65548,1,0)],None,None),
}
results={}
for name,(commands,ir,ddc) in tests.items():
 out=process(commands,ir,ddc)
 diff=(sum((a-b)**2 for a,b in zip(out,baseline))/len(out))**0.5
 rms=(sum(x*x for x in out)/len(out))**0.5
 results[name]={'rms':rms,'difference_rms':diff,'peak':max(abs(x) for x in out)}
 assert diff>1e-7,(name,'no measurable effect')
 assert rms>1e-6,(name,'silent output')
 if name in ('ddc','convolver'):assert max(abs(a-0.5*b) for a,b in zip(out,baseline))<1e-6,name
 if name=='pan_left':assert max(abs(x) for x in out[1::2])<1e-9,name
 if name=='limiter':assert max(abs(x) for x in out)<=0.100001,name
assert abs(results['volume_half']['rms']/(sum(x*x for x in baseline)/len(baseline))**0.5-0.5)<0.001
# Decode the prefix and FET section using the release's documented native layout.
fet_offsets={'threshold':48,'ratio':52,'knee':56,'gain':64,'attack':72,'release':80,'knee_multi':88,'max_attack':92,'max_release':96,'crest':100,'adapt':104}
h=dll.vp_create(rate)
try:
 for id,val in [(65611,25),(65612,75),(65613,10),(65615,5),(65617,20),(65619,50),(65621,15),(65622,80),(65623,90),(65624,100),(65625,50)]:assert dll.vp_set(h,id,val,0,0,0)==1
 snap=(C.c_uint8*dll.vp_param_size())();dll.vp_snapshot(h,snap,len(snap))
 decoded={k:struct.unpack_from('<f',bytes(snap),off)[0] for k,off in fet_offsets.items()}
 expected={'threshold':0.25,'ratio':0.75,'knee':0.1,'gain':0.05,'attack':0.2,'release':0.5,'knee_multi':0.15,'max_attack':0.8,'max_release':0.9,'crest':1.0,'adapt':0.5}
 for k,v in expected.items():assert abs(decoded[k]-v)<1e-6,(k,decoded[k],v)
 for id,val in [(65611,25),(65612,75),(65613,0),(65615,0),(65614,0),(65616,0),(65618,0),(65620,0),(65610,1)]:assert dll.vp_set(h,id,val,0,0,0)==1
 constant=(C.c_float*(frames*2))(*([0.5]*(frames*2)))
 assert dll.vp_process(h,constant,frames)==1
 steady=sum(constant[-8192:])/8192
 ideal=0.5*((10**(-15/20))/0.5)**0.75
 assert abs(steady-ideal)<0.01,(steady,ideal)
 compression={'input':0.5,'threshold_db':-15,'ratio':4,'steady_output':steady,'expected_output':ideal}
finally:dll.vp_destroy(h)
report={'engine':dll.vp_engine_version().decode(),'params_size':dll.vp_param_size(),'dll_sha256':hashlib.sha256(dll_path.read_bytes()).hexdigest(),'sample_rate':rate,'frames':frames,'bypass_delay_samples':256,'decoded_fet_snapshot':decoded,'compression_response':compression,'tests':results}
(base/'native-verification.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
