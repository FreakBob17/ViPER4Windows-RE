#include "TubeSimulator.h"
#include "../constants.h"

static constexpr float kTubeHarmonics[] = {
    0.5f, 0.3f, 0.12f, 0.05f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f
};

TubeSimulator::TubeSimulator() :
    enable_(false),
    sampling_rate_(VIPER_DEFAULT_SAMPLING_RATE) {
    Reset();
}

void TubeSimulator::Process(float *buffer, const uint32_t size) {
    if (!enable_) return;

    for (uint32_t i = 0; i < size; i++) {
        const double in_l = buffer[i * 2];
        double harm_l = high_pass_[0].ProcessSample(in_l);
        harm_l = harmonic_[0].Process(harm_l);
        harm_l = low_pass_[0].ProcessSample(harm_l);
        buffer[i * 2] = static_cast<float>(in_l + harm_l);

        const double in_r = buffer[i * 2 + 1];
        double harm_r = high_pass_[1].ProcessSample(in_r);
        harm_r = harmonic_[1].Process(harm_r);
        harm_r = low_pass_[1].ProcessSample(harm_r);
        buffer[i * 2 + 1] = static_cast<float>(in_r + harm_r);
    }
}

void TubeSimulator::Reset() {
    const float lp_cutoff = static_cast<float>(sampling_rate_) / 2.0f - 2000.0f;

    for (uint32_t ch = 0; ch < 2; ch++) {
        high_pass_[ch].RefreshFilter(
            MultiBiquad::FilterType::HIGH_PASS,
            0.0f,
            120.0f,
            sampling_rate_,
            0.717f,
            false
        );
        low_pass_[ch].RefreshFilter(
            MultiBiquad::FilterType::LOW_PASS,
            0.0f,
            lp_cutoff,
            sampling_rate_,
            0.717f,
            false
        );
        harmonic_[ch].SetHarmonics(kTubeHarmonics);
    }
}

void TubeSimulator::SetEnable(const bool enable) {
    if (enable_ != enable) {
        if (!enable_) {
            Reset();
        }
        enable_ = enable;
    }
}

void TubeSimulator::SetSamplingRate(const uint32_t sampling_rate) {
    if (sampling_rate_ != sampling_rate) {
        sampling_rate_ = sampling_rate;
        Reset();
    }
}
