#pragma once

#include "../utils/DynamicBass.h"
#include <cstdint>

class DynamicSystem {
public:
    DynamicSystem();

    void Process(float *samples, uint32_t size);
    void Reset();

    void SetEnable(bool enable);
    void SetBassGain(float gain);
    void SetSamplingRate(uint32_t sampling_rate);
    void SetSideGain(float gain_low, float gain_high);
    void SetXCoeffs(int low, int high);
    void SetYCoeffs(int low, int high);

private:
    bool enable_;

    uint32_t sampling_rate_;

    int x_low_ = 0;
    int x_high_ = 0;
    int y_low_ = 0;
    int y_high_ = 0;
    float side_gain_low_ = 0.0f;
    float side_gain_high_ = 0.0f;

    DynamicBass dynamic_bass_;
};
