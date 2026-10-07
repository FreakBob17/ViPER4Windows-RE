from pathlib import Path
import subprocess, re, concurrent.futures, sys, os

base=Path(__file__).resolve().parent
root=base.parents[1]
tool=Path(sys.argv[1]) if len(sys.argv)>1 else Path(os.environ.get('VIPER_PC_COMPILER',str(root/'work/compiler/llvm-mingw-20260922-ucrt-x86_64/bin')))
src=base/'ViPERDSP' if (base/'ViPERDSP').exists() else base/'pinned/ViPERDSP-db1c11cbe377eb35af6574abe75e3bb009910d88'
if not (tool/'clang++.exe').exists():raise SystemExit('Pass the llvm-mingw bin folder: python build_native.py C:/path/llvm-mingw/bin')
obj=base/'native-obj'
obj.mkdir(exist_ok=True)
files=re.findall(r'(?m)^\s*(viper/[^\s)]+\.(?:cpp|c))\s*$',(src/'CMakeLists.txt').read_text())
files=[src/f for f in files]+[base/'viper_pc.cpp']
flags=['-O3','-flto','-DNDEBUG','-DVERSION_CODE=20260922','-DVERSION_NAME="V4A-PC"','-I'+str(src/'include'),'-I'+str(src),'-include',str(base/'portable.h')]

def compile(f):
 out=obj/(str(f.relative_to(src) if f.is_relative_to(src) else f.name).replace('\\','_').replace('/','_')+'.o')
 compiler=tool/('clang.exe' if f.suffix=='.c' else 'clang++.exe')
 cmd=[str(compiler),'--target=x86_64-w64-windows-gnu',*flags]
 if f.suffix=='.cpp':cmd+=['-std=c++17']
 else:cmd+=['-std=c11']
 if f.suffix=='.c':
  cmd.remove('-include');cmd.remove(str(base/'portable.h'))
 cmd+=['-c',str(f),'-o',str(out)]
 r=subprocess.run(cmd,capture_output=True,text=True)
 if r.returncode:print(f.name+'\n'+r.stderr,flush=True)
 return out,r.returncode

with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:results=list(pool.map(compile,files))
if any(err for _,err in results):sys.exit(1)
output=base/'ViperDsp.dll'
cmd=[str(tool/'clang++.exe'),'--target=x86_64-w64-windows-gnu','-shared','-static','-flto','-O3',*[str(o) for o,_ in results],'-o',str(output)]
r=subprocess.run(cmd,capture_output=True,text=True)
print(r.stdout+r.stderr)
if r.returncode:sys.exit(r.returncode)
print('Built',output,output.stat().st_size)
