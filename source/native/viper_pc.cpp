#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cmath>
#include <algorithm>
#include <mutex>
#include <vector>
#include "viper_pc.h"
#include "viper/ViPER.h"

namespace {
struct PcEngine {
    std::mutex mutex;
    ViPER engine;
    viper::ViPERParams params;
    std::vector<float> scratch;
    uint32_t kernel_id = 1;
    explicit PcEngine(uint32_t rate) {
        // Disabled effects still receive sensible settings, so enabling one is safe.
        params.master_limiter = {1.0f,1.0f,0.0f};
        params.playback_gain_control = {false,1.0f,4.0f,1.0f};
        params.equalizer.band_count = 10;
        params.bass = {false,0,60,1.0f,false};
        params.bass_mono = {false,0,60,1.0f,false};
        params.spectrum_extension = {false,7600,0.0f};
        params.field_surround = {false,0.0f,1.0f,200};
        params.diff_surround = {false,10.0f,false,1.0f,0.0f};
        params.reverb = {false,0.5f,1.0f,0.5f,0.0f,1.0f};
        params.dynamic_system = {false,100,5600,40,80,0.1f,0.1f,1.0f};
        params.fet_compressor = {false,0.0f,0.0f,0.0f,true,0.0f,true,0.514679f,true,0.384311f,true,0.5f,0.879450f,0.884311f,0.615689f,0.660964f,true};
        params.lufs = {false,-16.0f,6.0f,1};
        params.psychoacoustic_bass = {false,100,50,3,100};
        params.stereo_imager = {false,1.0f,1.0f,1.0f,200.0f,4000.0f};
        params.multiband_compressor.band_count=5;
        params.multiband_compressor.crossover_frequencies={200,600,1600,4000,10000};
        for (auto &b : params.multiband_compressor.bands)
            b={false,0,0,0,true,0,true,0.514679f,true,0.384311f,true,0.5f,0.879450f,0.884311f,0.615689f,0.660964f,true};
        params.dynamic_eq.band_count=0;
        for(auto &b:params.dynamic_eq.bands) b={1000.0f,0.707f,0.0f,-24.0f,10.0f,100.0f,0};
        engine.SetSamplingRate(rate);
        engine.ResetAllEffects();
        engine.ApplyParams(params);
        scratch.reserve(8192);
    }
};
}

extern "C" VP_API void *vp_create(uint32_t rate) {
    if(rate<8000 || rate>384000) return nullptr;
    try {return new PcEngine(rate);} catch(...) {return nullptr;}
}
extern "C" VP_API void vp_destroy(void *handle) {delete static_cast<PcEngine*>(handle);}
extern "C" VP_API const char *vp_engine_version(void) {return "ViPERDSP db1c11c / Windows x64; V4A RE 0.6.1-compatible commands, completed convolution/VHE/FET";}
extern "C" VP_API uint32_t vp_param_size(void) {return sizeof(viper::ViPERParams);}
extern "C" VP_API uint32_t vp_latency_frames(void) {return 256;}
extern "C" VP_API uint64_t vp_processed_frames(void *handle) {
    if(!handle)return 0;
    auto &c=*static_cast<PcEngine*>(handle);
    std::lock_guard<std::mutex> lock(c.mutex);
    return c.engine.GetProcessedFrames();
}
extern "C" VP_API uint32_t vp_convolver_kernel_id(void *handle) {
    if(!handle)return 0;
    auto &c=*static_cast<PcEngine*>(handle);
    std::lock_guard<std::mutex> lock(c.mutex);
    return c.engine.GetConvolverKernelID();
}
extern "C" VP_API uint32_t vp_snapshot(void *handle,uint8_t *dst,uint32_t capacity) {
    if(!handle) return 0;
    auto &c=*static_cast<PcEngine*>(handle);
    std::lock_guard<std::mutex> lock(c.mutex);
    if(dst && capacity>=sizeof(c.params)) std::memcpy(dst,&c.params,sizeof(c.params));
    return sizeof(c.params);
}
extern "C" VP_API int vp_reset(void *handle) {
    if(!handle)return -1;
    auto &c=*static_cast<PcEngine*>(handle);
    std::lock_guard<std::mutex> lock(c.mutex);
    try {c.engine.ResetAllEffects();return 1;} catch(...) {return -1;}
}
extern "C" VP_API int vp_process(void *handle,float *samples,uint32_t frames) {
    if(!handle || (!samples && frames) || frames>10000000)return -1;
    auto &c=*static_cast<PcEngine*>(handle);
    std::lock_guard<std::mutex> lock(c.mutex);
    try {
        for(uint32_t off=0;off<frames;) {
            uint32_t n=std::min<uint32_t>(4096,frames-off);
            c.scratch.assign(samples+off*2,samples+(off+n)*2);
            c.engine.Process(c.scratch,n);
            for(float &f:c.scratch) if(!std::isfinite(f)) f=0.0f;
            std::memcpy(samples+off*2,c.scratch.data(),n*2*sizeof(float));
            off+=n;
        }
        return 1;
    } catch(...) {return -1;}
}
extern "C" VP_API int vp_load_ir(void *handle,const float *samples,uint32_t frames,uint32_t channels) {
    if(!handle || (channels!=1 && channels!=2) || frames>4000000 || (frames && !samples))return -1;
    auto &c=*static_cast<PcEngine*>(handle);
    std::lock_guard<std::mutex> lock(c.mutex);
    try {
        if(!frames) {c.engine.UnloadConvolverKernel();return 1;}
        // ViPER's native kernel minimum is sixteen frames.
        std::vector<float> padded;
        if(frames<16) {padded.assign(16*channels,0.0f);std::copy(samples,samples+frames*channels,padded.begin());samples=padded.data();frames=16;}
        return c.engine.LoadConvolverKernel(samples,frames,channels,c.kernel_id++).has_value()?1:0;
    } catch(...) {return -1;}
}
extern "C" VP_API int vp_load_ddc(void *handle,const float *a,const float *b,uint32_t sections) {
    if(!handle || sections>8192 || (sections && (!a || !b)))return -1;
    auto &c=*static_cast<PcEngine*>(handle);
    std::lock_guard<std::mutex> lock(c.mutex);
    try {c.engine.LoadDdcCoefficients(reinterpret_cast<const viper::BiquadSection*>(a),reinterpret_cast<const viper::BiquadSection*>(b),sections);return 1;}catch(...){return -1;}
}

extern "C" VP_API int vp_set(void *handle,int id,int v1,int v2,int v3,int v4) {
    (void)v3;(void)v4;
    if(!handle)return -1;
    auto &c=*static_cast<PcEngine*>(handle);
    std::lock_guard<std::mutex> lock(c.mutex);
    auto &p=c.params;
    const float f=static_cast<float>(v1)/100.0f;
    try {
        switch(id) {
        case 0x9002:return 1;
        case 0x9003:c.engine.ResetAllEffects();return 1;
        case 65538:p.convolver.enable=v1!=0;c.engine.ApplyConvolver(p.convolver);break;
        case 65543:p.convolver.cross_channel=std::clamp(f,0.0f,1.0f);c.engine.ApplyConvolver(p.convolver);break;
        case 65544:p.headphone_surround.enable=v1!=0;c.engine.ApplyHeadphoneSurround(p.headphone_surround);break;
        case 65545:p.headphone_surround.quality=std::clamp(v1,0,4);c.engine.ApplyHeadphoneSurround(p.headphone_surround);break;
        case 65546:p.ddc.enable=v1!=0;c.engine.ApplyDdc(p.ddc);break;
        case 65548:p.spectrum_extension.enable=v1!=0;c.engine.ApplySpectrumExtension(p.spectrum_extension);break;
        case 65549:p.spectrum_extension.strength=v1;c.engine.ApplySpectrumExtension(p.spectrum_extension);break;
        case 65550:p.spectrum_extension.exciter=f;c.engine.ApplySpectrumExtension(p.spectrum_extension);break;
        case 65551:p.equalizer.enable=v1!=0;c.engine.ApplyEqualizer(p.equalizer);break;
        case 65552:if(v1<0 || v1>=10)return -1;p.equalizer.band_levels[v1]=v2/100.0f;c.engine.ApplyEqualizer(p.equalizer);break;
        case 65553:p.field_surround.enable=v1!=0;c.engine.ApplyFieldSurround(p.field_surround);break;
        case 65554:p.field_surround.widening=f;c.engine.ApplyFieldSurround(p.field_surround);break;
        case 65555:p.field_surround.mid_image=f;c.engine.ApplyFieldSurround(p.field_surround);break;
        case 65556:p.field_surround.depth=static_cast<short>(v1);c.engine.ApplyFieldSurround(p.field_surround);break;
        case 65557:p.diff_surround.enable=v1!=0;c.engine.ApplyDiffSurround(p.diff_surround);break;
        case 65558:p.diff_surround.delay=f;c.engine.ApplyDiffSurround(p.diff_surround);break;
        case 65559:p.reverb.enable=v1!=0;c.engine.ApplyReverb(p.reverb);break;
        case 65560:p.reverb.room_size=f;c.engine.ApplyReverb(p.reverb);break;
        case 65561:p.reverb.width=f;c.engine.ApplyReverb(p.reverb);break;
        case 65562:p.reverb.damp=f;c.engine.ApplyReverb(p.reverb);break;
        case 65563:p.reverb.wet=f;c.engine.ApplyReverb(p.reverb);break;
        case 65564:p.reverb.dry=f;c.engine.ApplyReverb(p.reverb);break;
        case 65565:p.playback_gain_control.enable=v1!=0;c.engine.ApplyPlaybackGainControl(p.playback_gain_control);break;
        case 65566:p.playback_gain_control.strength=f;c.engine.ApplyPlaybackGainControl(p.playback_gain_control);break;
        case 65567:p.playback_gain_control.output_threshold=f;c.engine.ApplyPlaybackGainControl(p.playback_gain_control);break;
        case 65568:p.playback_gain_control.max_gain=f;c.engine.ApplyPlaybackGainControl(p.playback_gain_control);break;
        case 65569:p.dynamic_system.enable=v1!=0;c.engine.ApplyDynamicSystem(p.dynamic_system);break;
        case 65570:p.dynamic_system.x_coeff_low=v1;p.dynamic_system.x_coeff_high=v2;c.engine.ApplyDynamicSystem(p.dynamic_system);break;
        case 65571:p.dynamic_system.y_coeff_low=v1;p.dynamic_system.y_coeff_high=v2;c.engine.ApplyDynamicSystem(p.dynamic_system);break;
        case 65572:p.dynamic_system.side_gain_low=f;p.dynamic_system.side_gain_high=v2/100.0f;c.engine.ApplyDynamicSystem(p.dynamic_system);break;
        case 65573:p.dynamic_system.strength=f;c.engine.ApplyDynamicSystem(p.dynamic_system);break;
        case 65574:p.bass.enable=v1!=0;c.engine.ApplyBass(p.bass);break;
        case 65575:p.bass.mode=std::clamp(v1,0,2);c.engine.ApplyBass(p.bass);break;
        case 65576:p.bass.frequency=std::clamp(v1,15,150);c.engine.ApplyBass(p.bass);break;
        case 65577:p.bass.gain=f;c.engine.ApplyBass(p.bass);break;
        case 65578:p.clarity.enable=v1!=0;c.engine.ApplyClarity(p.clarity);break;
        case 65579:p.clarity.mode=std::clamp(v1,0,2);c.engine.ApplyClarity(p.clarity);break;
        case 65580:p.clarity.gain=f;c.engine.ApplyClarity(p.clarity);break;
        case 65581:p.cure.enable=v1!=0;c.engine.ApplyCure(p.cure);break;
        case 65582:p.cure.crossfeed_preset=std::clamp(v1,0,2);c.engine.ApplyCure(p.cure);break;
        case 65583:p.tube_simulator.enable=v1!=0;c.engine.ApplyTubeSimulator(p.tube_simulator);break;
        case 65584:p.analog_x.enable=v1!=0;c.engine.ApplyAnalogX(p.analog_x);break;
        case 65585:p.analog_x.mode=std::clamp(v1,0,2);c.engine.ApplyAnalogX(p.analog_x);break;
        case 65586:p.master_limiter.output_volume=std::clamp(f,0.0f,2.0f);c.engine.ApplyMasterLimiter(p.master_limiter);break;
        case 65587:p.master_limiter.channel_pan=std::clamp(f,-1.0f,1.0f);c.engine.ApplyMasterLimiter(p.master_limiter);break;
        case 65588:p.master_limiter.threshold=std::clamp(f,0.01f,1.0f);c.engine.ApplyMasterLimiter(p.master_limiter);break;
        case 65603:p.speaker_correction.enable=v1!=0;c.engine.ApplySpeakerCorrection(p.speaker_correction);break;
        // Pinned db1c11c applies the original log/exp transforms inside FET setters.
        // Snapshots must retain normalized values for that release's APO.
        case 65610:p.fet_compressor.enable=v1!=0;c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65611:p.fet_compressor.threshold=std::clamp(f,0.0f,1.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65612:p.fet_compressor.ratio=std::clamp(f,0.0f,1.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65613:p.fet_compressor.knee=std::clamp(f,0.0f,1.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65614:p.fet_compressor.knee_auto=v1!=0;c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65615:p.fet_compressor.gain=std::clamp(f,0.0f,1.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65616:p.fet_compressor.gain_auto=v1!=0;c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65617:p.fet_compressor.attack=std::clamp(f,0.0f,1.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65618:p.fet_compressor.attack_auto=v1!=0;c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65619:p.fet_compressor.release=std::clamp(f,0.0f,1.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65620:p.fet_compressor.release_auto=v1!=0;c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65621:p.fet_compressor.knee_multi=std::clamp(f,0.0f,1.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65622:p.fet_compressor.max_attack=std::clamp(f,0.0f,1.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65623:p.fet_compressor.max_release=std::clamp(f,0.0f,1.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65624:p.fet_compressor.crest=std::clamp(f,0.0f,3.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65625:p.fet_compressor.adapt=std::clamp(f,0.0f,1.0f);c.engine.ApplyFetCompressor(p.fet_compressor);break;
        case 65626:p.fet_compressor.no_clip=v1!=0;c.engine.ApplyFetCompressor(p.fet_compressor);break;
        default:return 0;
        }
        return 1;
    } catch(...) {return -1;}
}
