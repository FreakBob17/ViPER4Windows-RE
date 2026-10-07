#pragma once

#include "../utils/Harmonic.h"
#include "../utils/MultiBiquad.h"
#include <array>

class TubeSimulator {
public:
    TubeSimulator();

    void Process(float *buffer, uint32_t size);
    void Reset();

    void SetEnable(bool enable);
    void SetSamplingRate(uint32_t sampling_rate);

private:
    bool enable_;

    uint32_t sampling_rate_;

    std::array<MultiBiquad, 2> high_pass_;
    std::array<Harmonic, 2> harmonic_;
    std::array<MultiBiquad, 2> low_pass_;
};
