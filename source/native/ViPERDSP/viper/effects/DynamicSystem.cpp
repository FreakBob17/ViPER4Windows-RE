#include "DynamicSystem.h"
#include "../constants.h"

DynamicSystem::DynamicSystem() :
    enable_(false),
    sampling_rate_(VIPER_DEFAULT_SAMPLING_RATE) {
    dynamic_bass_.SetSamplingRate(sampling_rate_);
    dynamic_bass_.Reset();
}

void DynamicSystem::Process(float *samples, const uint32_t size) {
    if (!enable_) return;

    dynamic_bass_.FilterSamples(samples, size);
}

void DynamicSystem::Reset() {
    dynamic_bass_.SetSamplingRate(sampling_rate_);
    dynamic_bass_.Reset();
}

void DynamicSystem::SetEnable(const bool enable) {
    if (enable_ != enable) {
        if (enable) {
            Reset();
        }
        enable_ = enable;
    }
}

void DynamicSystem::SetBassGain(const float gain) {
    dynamic_bass_.SetBassGain(gain);
}

void DynamicSystem::SetSamplingRate(const uint32_t sampling_rate) {
    if (sampling_rate_ != sampling_rate) {
        sampling_rate_ = sampling_rate;
        dynamic_bass_.SetSamplingRate(sampling_rate);
    }
}

void DynamicSystem::SetSideGain(const float gain_low, const float gain_high) {
    if (gain_low >= 0.0f) side_gain_low_ = gain_low;
    if (gain_high >= 0.0f) side_gain_high_ = gain_high;
    dynamic_bass_.SetSideGain(side_gain_low_, side_gain_high_);
}

void DynamicSystem::SetXCoeffs(const int low, const int high) {
    if (low >= 0) x_low_ = low;
    if (high >= 0) x_high_ = high;
    dynamic_bass_.SetFilterXPassFrequency(x_low_, x_high_);
}

void DynamicSystem::SetYCoeffs(const int low, const int high) {
    if (low >= 0) y_low_ = low;
    if (high >= 0) y_high_ = high;
    dynamic_bass_.SetFilterYPassFrequency(y_low_, y_high_);
}
