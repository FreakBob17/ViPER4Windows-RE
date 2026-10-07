using System;
using System.IO;
using System.Text;

namespace ViperPc;

public sealed record WaveData(int SampleRate, float[] Samples)
{
    public int FrameCount => Samples.Length / 2;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / SampleRate);
}

/// <summary>RIFF WAV/IRS decoding into interleaved stereo float samples.</summary>
public static class WaveFile
{
    public static WaveData Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        if (stream.Length < 12 || Tag(reader) != "RIFF") throw new InvalidDataException("Ожидается файл WAV/IRS RIFF.");
        uint riffSize = reader.ReadUInt32();
        if (Tag(reader) != "WAVE") throw new InvalidDataException("Файл не содержит WAV-аудио.");
        long riffEnd = Math.Min(stream.Length, (long)riffSize + 8);
        byte[]? format = null;
        long dataOffset = -1, dataSize = 0;
        while (stream.Position + 8 <= riffEnd)
        {
            string tag = Tag(reader);
            uint length = reader.ReadUInt32();
            long start = stream.Position;
            if (length > riffEnd - start) throw new InvalidDataException("Повреждён размер WAV-блока.");
            if (tag == "fmt ")
            {
                if (length < 16 || length > 65536) throw new InvalidDataException("Некорректный формат WAV.");
                format = reader.ReadBytes((int)length);
            }
            else if (tag == "data" && dataOffset < 0) { dataOffset = start; dataSize = length; }
            stream.Position = start + length + (length & 1);
        }
        if (format == null || dataOffset < 0) throw new InvalidDataException("В WAV отсутствует описание формата или аудиоданные.");
        ushort encoding = U16(format, 0), channels = U16(format, 2), align = U16(format, 12), bits = U16(format, 14);
        uint rate = U32(format, 4);
        uint channelMask = 0;
        if (encoding == 0xfffe)
        {
            if (format.Length < 40 || U16(format, 16) < 22) throw new InvalidDataException("Некорректный WAV extensible.");
            channelMask = U32(format, 20);
            var subformat = new Guid(format.AsSpan(24, 16));
            if (subformat == new Guid("00000001-0000-0010-8000-00aa00389b71")) encoding = 1;
            else if (subformat == new Guid("00000003-0000-0010-8000-00aa00389b71")) encoding = 3;
            else throw new InvalidDataException("WAV содержит неподдерживаемый сжатый формат.");
        }
        bool pcm = encoding == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32);
        bool ieee = encoding == 3 && (bits == 32 || bits == 64);
        if (!pcm && !ieee) throw new InvalidDataException("Поддерживаются PCM WAV 8/16/24/32 бит и float WAV 32/64 бит.");
        if (channels < 1 || channels > 8 || rate < 8000 || rate > 384000 || align != channels * (bits / 8))
            throw new InvalidDataException("Некорректные параметры аудио WAV.");
        if (dataSize % align != 0) throw new InvalidDataException("Неполный последний WAV-кадр.");
        long frames = dataSize / align;
        if (frames > int.MaxValue / 2) throw new InvalidDataException("WAV слишком велик для обработки в памяти.");
        var samples = new float[checked((int)frames * 2)];
        stream.Position = dataOffset;
        var values = new float[channels];
        for (int frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                float sample = (encoding, bits) switch
                {
                    (1, 8) => (reader.ReadByte() - 128) / 128f,
                    (1, 16) => reader.ReadInt16() / 32768f,
                    (1, 24) => Read24(reader) / 8388608f,
                    (1, 32) => (float)(reader.ReadInt32() / 2147483648.0),
                    (3, 32) => reader.ReadSingle(),
                    (3, 64) => (float)reader.ReadDouble(),
                    _ => 0
                };
                values[channel] = float.IsFinite(sample) ? sample : 0;
            }
            Downmix(values, channelMask, out samples[frame * 2], out samples[frame * 2 + 1]);
        }
        return new WaveData((int)rate, samples);
    }

    // IEEE float WAV preserves headroom and avoids another quantization step.
    public static void Write(string path, WaveData data) => Write(path, data.SampleRate, data.Samples);
    public static void Write(string path, int sampleRate, float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate < 8000 || sampleRate > 384000 || samples.Length % 2 != 0) throw new ArgumentException("Ожидается стереобуфер и корректная частота дискретизации.");
        uint bytes = checked((uint)samples.LongLength * 4);
        if (bytes > uint.MaxValue - 50) throw new ArgumentException("WAV слишком велик.");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        PutTag(writer, "RIFF"); writer.Write(50u + bytes); PutTag(writer, "WAVE");
        PutTag(writer, "fmt "); writer.Write(18u); writer.Write((ushort)3); writer.Write((ushort)2);
        writer.Write((uint)sampleRate); writer.Write((uint)sampleRate * 8); writer.Write((ushort)8); writer.Write((ushort)32); writer.Write((ushort)0);
        PutTag(writer, "fact"); writer.Write(4u); writer.Write((uint)(samples.Length / 2));
        PutTag(writer, "data"); writer.Write(bytes);
        foreach (float sample in samples) writer.Write(float.IsFinite(sample) ? sample : 0);
    }

    public static void Process(string inputPath, string outputPath, Action<float[]> process, int latencyFrames = 0)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentOutOfRangeException.ThrowIfNegative(latencyFrames);
        var data = Read(inputPath);
        const int blockSamples = 2048 * 2;
        long delay = (long)latencyFrames * 2;
        long totalSamples = data.Samples.LongLength + delay;
        for (long offset = 0; offset < totalSamples; offset += blockSamples)
        {
            int length = (int)Math.Min(blockSamples, totalSamples - offset);
            var block = new float[length];
            int sourceLength = (int)Math.Min(length, Math.Max(0, data.Samples.LongLength - offset));
            if (sourceLength > 0) Array.Copy(data.Samples, offset, block, 0, sourceLength);
            process(block);
            // Flush the processor's delay with zeros, trim its initial delay,
            // and keep precisely the original number of frames.
            long first = Math.Max(offset, delay);
            long last = Math.Min(offset + length, delay + data.Samples.LongLength);
            if (last > first) Array.Copy(block, first - offset, data.Samples, first - delay, last - first);
        }
        Write(outputPath, data);
    }

    static void Downmix(float[] source, uint mask, out float left, out float right)
    {
        if (source.Length == 1) { left = right = source[0]; return; }
        if (source.Length == 2) { left = source[0]; right = source[1]; return; }
        // Conventional WAV channel order if the older PCM header lacks a mask.
        if (mask == 0) mask = source.Length switch { 3 => 0x7u, 4 => 0x33u, 5 => 0x37u, 6 => 0x3fu, 7 => 0x13fu, 8 => 0x63fu, _ => 3u };
        left = right = 0;
        int index = 0;
        for (int position = 0; position < 32 && index < source.Length; position++)
        {
            if ((mask & (1u << position)) == 0) continue;
            float value = source[index++];
            switch (position)
            {
                case 0: left += value; break;
                case 1: right += value; break;
                case 2: left += value * 0.70710678f; right += value * 0.70710678f; break;
                case 3: left += value * 0.5f; right += value * 0.5f; break;
                case 4: case 6: case 9: left += value * 0.70710678f; break;
                case 5: case 7: case 10: right += value * 0.70710678f; break;
                default: left += value * 0.5f; right += value * 0.5f; break;
            }
        }
    }

    static int Read24(BinaryReader reader)
    {
        int value = reader.ReadByte() | (reader.ReadByte() << 8) | (reader.ReadByte() << 16);
        return (value << 8) >> 8;
    }
    static ushort U16(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    static uint U32(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    static string Tag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
    static void PutTag(BinaryWriter writer, string tag) => writer.Write(Encoding.ASCII.GetBytes(tag));
}
