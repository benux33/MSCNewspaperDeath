using System;
using System.IO;
using System.Text;

namespace DeathNewspaper
{
    // Pure managed decoder: no Unity request, coroutine, or platform codec required.
    internal sealed class WaveData
    {
        public int Channels, Rate;
        public float[] Samples;
        public byte[] ToWave(int trimSeconds, float volume)
        {
            int skip = checked(trimSeconds * Rate * Channels);
            if (skip < 0 || skip >= Samples.Length) throw new InvalidDataException("Invalid trim duration.");
            int length = checked((Samples.Length - skip) * 2);
            using (var stream = new MemoryStream(44 + length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + length);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)Channels); writer.Write(Rate);
                writer.Write(Rate * Channels * 2); writer.Write((short)(Channels * 2)); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(length);
                float gain = Math.Max(0, Math.Min(1, volume));
                for (int i = skip; i < Samples.Length; i++)
                    writer.Write((short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(Samples[i] * gain * 32768))));
                return stream.ToArray();
            }
        }
        public static WaveData Read(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Expected RIFF WAV.");
                reader.ReadUInt32();
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Expected WAVE.");
                int format = 0, channels = 0, rate = 0, bits = 0;
                byte[] bytes = null;
                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    uint length = reader.ReadUInt32();
                    long end = reader.BaseStream.Position + length;
                    if (end > reader.BaseStream.Length) throw new InvalidDataException("Truncated WAV chunk.");
                    if (id == "fmt ")
                    {
                        if (length < 16) throw new InvalidDataException("Invalid WAV format chunk.");
                        format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                        reader.ReadUInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
                    }
                    else if (id == "data") bytes = reader.ReadBytes(checked((int)length));
                    reader.BaseStream.Position = end + (length & 1);
                }
                if (format != 1 || bits != 16 || channels < 1 || channels > 2 || rate <= 0 || bytes == null || bytes.Length == 0 || bytes.Length % (2 * channels) != 0)
                    throw new InvalidDataException("Use a 16-bit PCM mono/stereo WAV, like the supplied death song.");
                var samples = new float[bytes.Length / 2];
                for (int i = 0, j = 0; i < samples.Length; i++, j += 2)
                    samples[i] = (short)(bytes[j] | bytes[j + 1] << 8) / 32768f;
                return new WaveData { Channels = channels, Rate = rate, Samples = samples };
            }
        }
    }
}
