using System;
using System.Runtime.InteropServices;

namespace DeathNewspaper
{
    // Separate waveOut voice: the Escape cue must not replace PlaySound's music.
    internal sealed class NativeEffect : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct Format { public ushort Tag, Channels; public uint Rate, Bytes; public ushort Align, Bits, Extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct Header { public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
        [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr handle, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr handle);
        IntPtr device, header;
        GCHandle data;
        bool prepared;
        readonly uint size = (uint)Marshal.SizeOf(typeof(Header));
        static void Check(uint result) { if (result != 0) throw new InvalidOperationException("Escape audio error: " + result); }
        public void Play(byte[] wave)
        {
            Dispose();
            try
            {
                var format = new Format { Tag = 1, Channels = BitConverter.ToUInt16(wave, 22), Rate = BitConverter.ToUInt32(wave, 24),
                    Bytes = BitConverter.ToUInt32(wave, 28), Align = BitConverter.ToUInt16(wave, 32), Bits = 16 };
                Check(waveOutOpen(out device, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0));
                data = GCHandle.Alloc(wave, GCHandleType.Pinned);
                header = Marshal.AllocHGlobal((int)size);
                Marshal.StructureToPtr(new Header { Data = IntPtr.Add(data.AddrOfPinnedObject(), 44), Length = (uint)(wave.Length - 44) }, header, false);
                Check(waveOutPrepareHeader(device, header, size)); prepared = true;
                Check(waveOutWrite(device, header, size));
            }
            catch { Dispose(); throw; }
        }
        public void Poll()
        {
            if (header != IntPtr.Zero && (Marshal.PtrToStructure<Header>(header).Flags & 1) != 0) Dispose();
        }
        public void Dispose()
        {
            if (device != IntPtr.Zero)
            {
                waveOutReset(device);
                if (prepared) waveOutUnprepareHeader(device, header, size);
                waveOutClose(device); device = IntPtr.Zero;
            }
            prepared = false;
            if (header != IntPtr.Zero) { Marshal.FreeHGlobal(header); header = IntPtr.Zero; }
            if (data.IsAllocated) data.Free();
        }
    }
}
