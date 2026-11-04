using System;
using System.Runtime.InteropServices;

namespace DeathNewspaper
{
    // Own the pinned PCM WAV until asynchronous playback has stopped.
    internal sealed class NativeSong : IDisposable
    {
        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
        GCHandle data;
        bool playing;
        readonly byte[] original;
        readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        public NativeSong(byte[] wave) { original = wave; data = GCHandle.Alloc(wave, GCHandleType.Pinned); }
        public void Play()
        {
            if (!data.IsAllocated) throw new ObjectDisposedException(nameof(NativeSong));
            Stop(); data.Free(); data = GCHandle.Alloc(original, GCHandleType.Pinned);
            // ASYNC | NODEFAULT | MEMORY; no looping and no system alert fallback.
            if (!PlaySound(data.AddrOfPinnedObject(), IntPtr.Zero, 0x7))
                throw new InvalidOperationException("Windows could not start WAV playback.");
            playing = true;
            clock.Restart();
        }
        public void FadeOut(float seconds)
        {
            if (!playing) return;
            var fade = MakeFade(original, clock.Elapsed.TotalSeconds, seconds);
            Stop();
            if (fade.Length <= 44) return;
            data.Free(); data = GCHandle.Alloc(fade, GCHandleType.Pinned);
            playing = PlaySound(data.AddrOfPinnedObject(), IntPtr.Zero, 0x7);
        }
        internal static byte[] MakeFade(byte[] wave, double elapsed, float seconds)
        {
            int rate = BitConverter.ToInt32(wave, 24), channels = BitConverter.ToInt16(wave, 22);
            int frames = (wave.Length - 44) / (channels * 2);
            int start = (int)Math.Min(frames, Math.Max(0, elapsed * rate));
            int count = Math.Min(frames - start, Math.Max(1, (int)(seconds * rate)));
            byte[] fade = new byte[44 + count * channels * 2];
            Array.Copy(wave, fade, 44);
            Array.Copy(BitConverter.GetBytes(fade.Length - 8), 0, fade, 4, 4);
            Array.Copy(BitConverter.GetBytes(fade.Length - 44), 0, fade, 40, 4);
            for (int frame = 0; frame < count; frame++)
            {
                double gain = 1.0 - (double)frame / Math.Max(1, count - 1);
                for (int channel = 0; channel < channels; channel++)
                {
                    short sample = BitConverter.ToInt16(wave, 44 + ((start + frame) * channels + channel) * 2);
                    short scaled = (short)Math.Round(sample * gain);
                    int at = 44 + (frame * channels + channel) * 2;
                    fade[at] = (byte)scaled; fade[at + 1] = (byte)(scaled >> 8);
                }
            }
            return fade;
        }
        public void Stop()
        {
            if (playing) PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            playing = false;
        }
        public void Dispose() { Stop(); if (data.IsAllocated) data.Free(); }
    }
}
