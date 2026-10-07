#include "VHE.h"
#include "../../include/log.h"
#include "../constants.h"
#include "VHE_L0.h"
#include "VHE_L1.h"
#include "VHE_L2.h"
#include "VHE_L3.h"
#include "VHE_L4.h"
#include <cstring>

VHE::VHE() :
    enable_(false),
    sampling_rate_(VIPER_DEFAULT_SAMPLING_RATE),
    effect_level_(0),
    conv_size_(0) {
    Reset();
}

uint32_t VHE::Process(const float *source, float *dest, const uint32_t frame_size) {
    if (enable_ && conv_left_.InstanceUsable() && conv_right_.InstanceUsable()) {
        for (uint32_t off = 0; off < frame_size; off += conv_size_) {
            const uint32_t n =
                frame_size - off < conv_size_ ? frame_size - off : conv_size_;

            if (source != dest) {
                memcpy(dest + off * 2, source + off * 2, n * 2 * sizeof(float));
            }
            float *buffer = dest + off * 2;

            conv_left_.ConvolveInterleaved(buffer, 0, n);
            conv_right_.ConvolveInterleaved(buffer, 1, n);
        }
        return frame_size;
    }
    return frame_size;
}

void VHE::Reset() {
    conv_left_.Reset();
    conv_left_.UnloadKernel();
    conv_right_.Reset();
    conv_right_.UnloadKernel();

    if (effect_level_ > 4) {
        VIPER_LOGD("VHE: Unsupported effect level %d", effect_level_);
        return;
    }

    if (sampling_rate_ != 44100 && sampling_rate_ != 48000) {
        VIPER_LOGD("VHE: Unsupported sampling rate %d", sampling_rate_);
        return;
    }

    const float *arr_left;
    const float *arr_right;
    uint32_t arr_size;
    float gain;

    switch (effect_level_) {
        case 0: {
            switch (sampling_rate_) {
                case 44100: {
                    arr_left = kVheL0_44100_L;
                    arr_right = kVheL0_44100_R;
                    gain = 2.94595f;
                    break;
                }
                case 48000: {
                    arr_left = kVheL0_48000_L;
                    arr_right = kVheL0_48000_R;
                    gain = 2.94595f;
                    break;
                }
                default: {
                    VIPER_LOGD("VHE: Unsupported sampling rate %d", sampling_rate_);
                    return;
                }
            }
            arr_size = 4096;
            break;
        }
        case 1: {
            switch (sampling_rate_) {
                case 44100: {
                    arr_left = kVheL1_44100_L;
                    arr_right = kVheL1_44100_R;
                    gain = 0.944061f;
                    break;
                }
                case 48000: {
                    arr_left = kVheL1_48000_L;
                    arr_right = kVheL1_48000_R;
                    gain = 0.944061f;
                    break;
                }
                default: {
                    VIPER_LOGD("VHE: Unsupported sampling rate %d", sampling_rate_);
                    return;
                }
            }
            arr_size = 2047;
            break;
        }
        case 2: {
            switch (sampling_rate_) {
                case 44100: {
                    arr_left = kVheL2_44100_L;
                    arr_right = kVheL2_44100_R;
                    gain = 1.544582f;
                    break;
                }
                case 48000: {
                    arr_left = kVheL2_48000_L;
                    arr_right = kVheL2_48000_R;
                    gain = 1.531516f;
                    break;
                }
                default: {
                    VIPER_LOGD("VHE: Unsupported sampling rate %d", sampling_rate_);
                    return;
                }
            }
            arr_size = 4096;
            break;
        }
        case 3: {
            switch (sampling_rate_) {
                case 44100: {
                    arr_left = kVheL3_44100_L;
                    arr_right = kVheL3_44100_R;
                    gain = 1.584257f;
                    break;
                }
                case 48000: {
                    arr_left = kVheL3_48000_L;
                    arr_right = kVheL3_48000_R;
                    gain = 1.567789f;
                    break;
                }
                default: {
                    VIPER_LOGD("VHE: Unsupported sampling rate %d", sampling_rate_);
                    return;
                }
            }
            arr_size = 4096;
            break;
        }
        case 4: {
            switch (sampling_rate_) {
                case 44100: {
                    arr_left = kVheL4_44100_L;
                    arr_right = kVheL4_44100_R;
                    gain = 1.466681f;
                    break;
                }
                case 48000: {
                    arr_left = kVheL4_48000_L;
                    arr_right = kVheL4_48000_R;
                    gain = 1.487227f;
                    break;
                }
                default: {
                    VIPER_LOGD("VHE: Unsupported sampling rate %d", sampling_rate_);
                    return;
                }
            }
            arr_size = 4096;
            break;
        }
        default: {
            VIPER_LOGD("VHE: Unsupported effect level %d", effect_level_);
            return;
        }
    }

    conv_left_.LoadKernel(arr_left, gain, arr_size, 4096);
    conv_right_.LoadKernel(arr_right, gain, arr_size, 4096);
    conv_size_ = 4096;
}

bool VHE::GetEnable() const {
    return enable_;
}

void VHE::SetEnable(const bool enable) {
    if (enable_ != enable) {
        enable_ = enable;
        if (enable_) {
            Reset();
        }
    }
}

void VHE::SetEffectLevel(const uint32_t value) {
    if (effect_level_ != value) {
        if (value < 5) {
            effect_level_ = value;
            Reset();
        }
    }
}

void VHE::SetSamplingRate(const uint32_t sampling_rate) {
    if (sampling_rate_ != sampling_rate) {
        sampling_rate_ = sampling_rate;
        Reset();
    }
}
