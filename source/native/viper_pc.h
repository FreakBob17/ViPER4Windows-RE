#pragma once
#include <stdint.h>
#ifdef _WIN32
#define VP_API __declspec(dllexport)
#else
#define VP_API
#endif
#ifdef __cplusplus
extern "C" {
#endif
// Stereo interleaved IEEE float32. All calls on a handle are serialized internally.
VP_API void *vp_create(uint32_t sample_rate);
VP_API void vp_destroy(void *handle);
// Original 0.6.1 integer parameter IDs and units (100 = 1.0; volume is percent).
// Returns 1 applied, 0 unsupported, -1 invalid argument/error.
VP_API int vp_set(void *handle, int parameter, int value1, int value2, int value3, int value4);
VP_API int vp_process(void *handle, float *stereo_interleaved, uint32_t frames);
VP_API int vp_load_ir(void *handle, const float *interleaved, uint32_t frames, uint32_t channels);
// Arrays comprise b0,b1,b2,a1,a2 per section. Original VDC convention.
VP_API int vp_load_ddc(void *handle, const float *coefficients44100, const float *coefficients48000, uint32_t sections);
VP_API int vp_reset(void *handle);
VP_API const char *vp_engine_version(void);
// Exact ViPERDSP db1c11c native-layout structure for ViPER4Windows 2.0.1 APO.
VP_API uint32_t vp_param_size(void);
// Returns required size; copies only if capacity is sufficient.
VP_API uint32_t vp_snapshot(void *handle, uint8_t *destination, uint32_t capacity);
VP_API uint32_t vp_latency_frames(void);
VP_API uint64_t vp_processed_frames(void *handle);
VP_API uint32_t vp_convolver_kernel_id(void *handle);
#ifdef __cplusplus
}
#endif
