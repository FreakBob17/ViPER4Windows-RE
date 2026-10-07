using System;
using System.IO;

namespace ViperPc;

public static class ImpulseResampler
{
    // A 64-tap Blackman-windowed sinc, widened when downsampling for antialiasing.
    // Impulse coefficients carry convolution gain, so retain their sum rather than
    // the amplitude convention used to resample a recorded audio stream.
    public static float[] Resample(float[] stereo, uint sourceRate, uint targetRate)
    {
        ArgumentNullException.ThrowIfNull(stereo);
        if (sourceRate < 8000 || sourceRate > 384000 || targetRate < 8000 || targetRate > 384000 || stereo.Length == 0 || (stereo.Length & 1) != 0)
            throw new InvalidDataException("Некорректный стереоимпульс или частота дискретизации.");
        int inputFrames = stereo.Length / 2;
        if (inputFrames > sourceRate * 20L) throw new InvalidDataException("Импульс должен быть не длиннее 20 секунд.");
        foreach (float sample in stereo) if (!float.IsFinite(sample)) throw new InvalidDataException("Импульс содержит неконечный отсчёт.");
        if (sourceRate == targetRate) return stereo;
        double ratio = (double)targetRate / sourceRate;
        int outputFrames = checked((int)(((long)inputFrames * targetRate + sourceRate - 1) / sourceRate));
        if (outputFrames > targetRate * 20L || outputFrames > 4_000_000) throw new InvalidDataException("Преобразованный импульс слишком длинный.");
        var output = new float[checked(outputFrames * 2)];
        double cutoff = Math.Min(1.0, ratio), radius = 32.0 / cutoff;
        for (int frame = 0; frame < outputFrames; frame++)
        {
            double position = frame / ratio;
            int first = Math.Max(0, (int)Math.Ceiling(position - radius));
            int last = Math.Min(inputFrames - 1, (int)Math.Floor(position + radius));
            double left = 0, right = 0;
            for (int index = first; index <= last; index++)
            {
                double distance = position - index, x = distance * cutoff;
                double sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double phase = Math.PI * distance / radius;
                double window = 0.42 + 0.5 * Math.Cos(phase) + 0.08 * Math.Cos(2 * phase);
                double weight = cutoff * sinc * window / ratio;
                left += stereo[index * 2] * weight;
                right += stereo[index * 2 + 1] * weight;
            }
            output[frame * 2] = (float)left;
            output[frame * 2 + 1] = (float)right;
        }
        for (int channel = 0; channel < 2; channel++)
        {
            double originalSum = 0, resampledSum = 0, magnitude = 0;
            int strongest = channel;
            for (int i = channel; i < stereo.Length; i += 2) originalSum += stereo[i];
            for (int i = channel; i < output.Length; i += 2)
            {
                resampledSum += output[i]; magnitude += Math.Abs(output[i]);
                if (Math.Abs(output[i]) > Math.Abs(output[strongest])) strongest = i;
            }
            // Avoid division by a cancelled sum for zero-DC/high-pass kernels.
            if (Math.Abs(resampledSum) > Math.Max(1e-30, magnitude * 1e-7) && Math.Abs(originalSum) > magnitude * 1e-7 && originalSum * resampledSum > 0)
            {
                double gain = originalSum / resampledSum;
                for (int i = channel; i < output.Length; i += 2) output[i] = (float)(output[i] * gain);
            }
            double finalSum = 0;
            for (int i = channel; i < output.Length; i += 2) finalSum += output[i];
            output[strongest] = (float)(output[strongest] + originalSum - finalSum);
        }
        foreach (float sample in output) if (!float.IsFinite(sample)) throw new InvalidDataException("Преобразование импульса дало неконечный отсчёт.");
        return output;
    }
}
