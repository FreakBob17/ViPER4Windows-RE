# Native audio port

The Windows DLL builds the ViPERDSP core at commit `db1c11cbe377eb35af6574abe75e3bb009910d88`, the exact core pinned by the ViPER4Windows 2.0.1 release. The Android attachments are the ViPER4Android-RE 0.6.2 control app and 0.6.1 Magisk module. Their installation scripts were read statically and were never executed.

The Magisk ZIP contains Android ELF libraries for ARMv7, ARM64, x86 and x64. They depend on Android's libc/libm/libdl/liblog and AudioEffect interface. Windows therefore needs a compiled DLL and a Windows audio host/APO. The attached x64 `.so` cannot be loaded as a Windows DLL.

## Fidelity

The native port uses the genuine reverse-engineered ViPER effect implementations. `vp_set` accepts the Android 0.6.1 parameter IDs and integer units. Its full-parameter snapshots have the exact 1144-byte native structure required by the official ViPER4Windows 2.0.1 APO. Input audio is stereo, interleaved float32. GUI slider-to-command transforms are extracted separately from the attached APK.

This is not a bit-identical execution of the Android driver. In the original 0.6.1 source, convolver dispatch was disabled, partitioned convolution was a TODO, and FET processing returned immediately. The later core completes convolution, headphone surround and FET compression and includes other fixes. It also changes the order of some effects, adds optional antipop behavior and uses a different master limiter. All extra modern effects remain disabled in this app. These changes make the requested effects work, while preventing a truthful promise of identical samples to the old module.

VHE uses the embedded ViPER impulse tables at 44.1/48 kHz. DDC uses the supplied 44.1/48 kHz coefficient sets and the original positive-feedback convention `b0,b1,b2,a1,a2`. These two effects do not process other sampling rates. For a full effect chain, choose 44.1 or 48 kHz; the live host defaults to 48 kHz. The DLL receives convolution kernels at the audio host's sample rate. The desktop wrapper automatically adapts a file's rate with a windowed-sinc impulse resampler that preserves each channel's DC gain. The APO controller adapts kernels to the reported 44.1/48 kHz device rate. Mono kernels are accepted by the DLL; the desktop WAV importer expands mono to stereo.

The limiter contributes 256 frames of latency. `vp_latency_frames` exposes that value for offline compensation. AnalogX retains the upstream approximately 250 ms startup mute after enabling/changing its model.

## Build

Use Python 3 and an x64 UCRT llvm-mingw distribution. No Android runtime, Magisk, system installation or Visual Studio is needed for the standalone DLL.

```
python build_native.py C:/path/to/llvm-mingw/bin
python check_native.py
```

The build adds standard C/C++ includes and the usual `M_PI` constant through `portable.h`. It does not rewrite the DSP algorithms. `-static` includes the C++ runtime; remaining imported libraries are Windows UCRT and KERNEL32.

## Provenance and terms

- Original algorithms: Zhuhang and ViPER520.
- Reverse engineering: Martmists, Iscle and likelikeslike.
- Original RE driver source: https://github.com/AndroidAudioMods/ViPERFX_RE/tree/rewrite-continued
- Standalone source: https://github.com/likelikeslike/ViPERDSP/tree/db1c11cbe377eb35af6574abe75e3bb009910d88
- Windows APO and shared-memory protocol: https://github.com/likelikeslike/ViPER4Windows/releases/tag/2.0.1

Preserve the `ViPERDSP/README.md` credits and legal notice. That project describes the code as reverse engineered, for personal use, with no commercial use. The GPLv2 file in the Magisk ZIP belongs to the inherited MMT module template and does not establish a license for the DSP source. The source is therefore not relabeled as GPL or permissively licensed.
