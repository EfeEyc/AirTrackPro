using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace AirTrackBridge
{
    public sealed class AudioDeviceInfo
    {
        public string Id = "";
        public string Name = "";
        public bool IsDefault;
    }

    public sealed class WasapiLoopbackCapture : IDisposable
    {
        public delegate void PcmStereoChunkHandler(byte[] pcmBytes, int byteCount, int sampleRate);

        private const uint CLSCTX_ALL = 0x17;
        private const int DEVICE_STATE_ACTIVE = 0x00000001;
        private const int STGM_READ = 0x00000000;
        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002;

        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");

        private readonly PcmStereoChunkHandler _onChunk;
        private readonly object _controlLock = new object();
        private Thread _thread;
        private volatile bool _running;
        private string _activeDeviceId = "";
        private volatile float _gain = 1.0f;

        public int CurrentSampleRate { get; private set; }
        public string LastError { get; private set; }
        public bool IsCapturing { get { return _running; } }

        public WasapiLoopbackCapture(PcmStereoChunkHandler onChunk)
        {
            _onChunk = onChunk;
            CurrentSampleRate = 48000;
            LastError = "";
        }

        public void SetGain(double gain)
        {
            if (gain < 0.0) gain = 0.0;
            if (gain > 2.0) gain = 2.0;
            _gain = (float)gain;
        }

        public static List<AudioDeviceInfo> ListRenderDevices()
        {
            var list = new List<AudioDeviceInfo>();
            IMMDeviceEnumerator enumerator = null;
            IMMDevice defaultDev = null;
            IMMDeviceCollection coll = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                string defaultId = "";
                try
                {
                    if (enumerator.GetDefaultAudioEndpoint(0, 0, out defaultDev) == 0 && defaultDev != null)
                    {
                        defaultDev.GetId(out defaultId);
                    }
                }
                catch { }

                if (enumerator.EnumAudioEndpoints(0, DEVICE_STATE_ACTIVE, out coll) == 0 && coll != null)
                {
                    uint count;
                    coll.GetCount(out count);
                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice dev = null;
                        IPropertyStore props = null;
                        try
                        {
                            if (coll.Item(i, out dev) == 0 && dev != null)
                            {
                                string id;
                                dev.GetId(out id);
                                string name = id;
                                if (dev.OpenPropertyStore(STGM_READ, out props) == 0 && props != null)
                                {
                                    var key = new PropertyKey
                                    {
                                        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                                        pid = 14
                                    };
                                    PropVariant pv;
                                    if (props.GetValue(ref key, out pv) == 0)
                                    {
                                        string val = pv.GetStringValue();
                                        if (!string.IsNullOrEmpty(val)) name = val;
                                        PropVariantClear(ref pv);
                                    }
                                }
                                list.Add(new AudioDeviceInfo
                                {
                                    Id = id ?? "",
                                    Name = name ?? "Audio Device",
                                    IsDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)
                                });
                            }
                        }
                        finally
                        {
                            if (props != null) Marshal.ReleaseComObject(props);
                            if (dev != null) Marshal.ReleaseComObject(dev);
                        }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (coll != null) Marshal.ReleaseComObject(coll);
                if (defaultDev != null) Marshal.ReleaseComObject(defaultDev);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
            return list;
        }

        public void Start(string deviceId)
        {
            lock (_controlLock)
            {
                if (_running && string.Equals(_activeDeviceId, deviceId ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                StopInternal();
                _activeDeviceId = deviceId ?? "";
                _running = true;
                _thread = new Thread(CaptureLoop);
                _thread.IsBackground = true;
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Priority = ThreadPriority.AboveNormal;
                _thread.Start(_activeDeviceId);
            }
        }

        public void Stop()
        {
            lock (_controlLock)
            {
                StopInternal();
            }
        }

        private void StopInternal()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(600);
            }
            _thread = null;
        }

        public void Dispose()
        {
            Stop();
        }

        private void CaptureLoop(object state)
        {
            string requestedId = state as string ?? "";
            while (_running)
            {
                IMMDeviceEnumerator enumerator = null;
                IMMDevice device = null;
                IAudioClient audioClient = null;
                IAudioCaptureClient captureClient = null;
                IntPtr mixFmtPtr = IntPtr.Zero;

                try
                {
                    enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                    int hr;
                    if (!string.IsNullOrEmpty(requestedId))
                    {
                        hr = enumerator.GetDevice(requestedId, out device);
                        if (hr != 0 || device == null)
                        {
                            hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                        }
                    }
                    else
                    {
                        hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                    }

                    if (hr != 0 || device == null)
                    {
                        LastError = "No active Windows audio output endpoint found.";
                        Thread.Sleep(1000);
                        continue;
                    }

                    Guid iidClient = IID_IAudioClient;
                    object clientObj;
                    hr = device.Activate(ref iidClient, CLSCTX_ALL, IntPtr.Zero, out clientObj);
                    if (hr != 0 || clientObj == null)
                    {
                        LastError = "Failed to activate IAudioClient: 0x" + hr.ToString("X");
                        Thread.Sleep(1000);
                        continue;
                    }
                    audioClient = (IAudioClient)clientObj;

                    hr = audioClient.GetMixFormat(out mixFmtPtr);
                    if (hr != 0 || mixFmtPtr == IntPtr.Zero)
                    {
                        LastError = "GetMixFormat failed: 0x" + hr.ToString("X");
                        Thread.Sleep(1000);
                        continue;
                    }

                    ushort formatTag = (ushort)Marshal.ReadInt16(mixFmtPtr, 0);
                    ushort channels = (ushort)Marshal.ReadInt16(mixFmtPtr, 2);
                    int sampleRate = Marshal.ReadInt32(mixFmtPtr, 4);
                    ushort blockAlign = (ushort)Marshal.ReadInt16(mixFmtPtr, 12);
                    ushort bitsPerSample = (ushort)Marshal.ReadInt16(mixFmtPtr, 14);
                    ushort cbSize = (ushort)Marshal.ReadInt16(mixFmtPtr, 16);

                    bool isFloat = (formatTag == 3);
                    if (formatTag == 0xFFFE && cbSize >= 22)
                    {
                        byte[] guidBytes = new byte[16];
                        Marshal.Copy(IntPtr.Add(mixFmtPtr, 24), guidBytes, 0, 16);
                        var subFormat = new Guid(guidBytes);
                        isFloat = (subFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
                    }

                    CurrentSampleRate = sampleRate;

                    // 20ms loopback buffer
                    long hnsBufferDuration = 200000;
                    hr = audioClient.Initialize(
                        AUDCLNT_SHAREMODE_SHARED,
                        AUDCLNT_STREAMFLAGS_LOOPBACK,
                        hnsBufferDuration,
                        0,
                        mixFmtPtr,
                        IntPtr.Zero);

                    if (hr != 0)
                    {
                        LastError = "IAudioClient.Initialize loopback failed: 0x" + hr.ToString("X");
                        Thread.Sleep(1000);
                        continue;
                    }

                    Guid iidCapture = IID_IAudioCaptureClient;
                    object capObj;
                    hr = audioClient.GetService(ref iidCapture, out capObj);
                    if (hr != 0 || capObj == null)
                    {
                        LastError = "GetService(IAudioCaptureClient) failed: 0x" + hr.ToString("X");
                        Thread.Sleep(1000);
                        continue;
                    }
                    captureClient = (IAudioCaptureClient)capObj;

                    hr = audioClient.Start();
                    if (hr != 0)
                    {
                        LastError = "IAudioClient.Start failed: 0x" + hr.ToString("X");
                        Thread.Sleep(1000);
                        continue;
                    }

                    LastError = "";
                    const int chunkFrames = 240; // 5 ms at 48kHz -> 960 bytes
                    byte[] outChunk = new byte[chunkFrames * 4];
                    int outPosBytes = 0;
                    byte[] rawBuf = new byte[65536];

                    while (_running)
                    {
                        uint packetFrames = 0;
                        hr = captureClient.GetNextPacketSize(out packetFrames);
                        if (hr != 0)
                        {
                            // Device invalidated or disconnected; re-initialize cleanly
                            break;
                        }

                        if (packetFrames == 0)
                        {
                            Thread.Sleep(2);
                            continue;
                        }

                        while (packetFrames > 0 && _running)
                        {
                            IntPtr dataPtr;
                            uint framesAvailable;
                            uint flags;
                            ulong devPos, qpcPos;
                            hr = captureClient.GetBuffer(out dataPtr, out framesAvailable, out flags, out devPos, out qpcPos);
                            if (hr != 0)
                            {
                                packetFrames = 0;
                                break;
                            }

                            try
                            {
                                int totalBytes = (int)(framesAvailable * blockAlign);
                                bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || dataPtr == IntPtr.Zero;

                                if (!silent && totalBytes > 0)
                                {
                                    if (rawBuf.Length < totalBytes) rawBuf = new byte[totalBytes];
                                    Marshal.Copy(dataPtr, rawBuf, 0, totalBytes);
                                    float gain = _gain;

                                    for (int f = 0; f < (int)framesAvailable; f++)
                                    {
                                        int frameOffset = f * blockAlign;
                                        float left, right;
                                        ReadStereoSample(rawBuf, frameOffset, channels, bitsPerSample, isFloat, out left, out right);

                                        short sL = FloatToInt16(left * gain);
                                        short sR = FloatToInt16(right * gain);

                                        outChunk[outPosBytes++] = (byte)(sL & 0xFF);
                                        outChunk[outPosBytes++] = (byte)((sL >> 8) & 0xFF);
                                        outChunk[outPosBytes++] = (byte)(sR & 0xFF);
                                        outChunk[outPosBytes++] = (byte)((sR >> 8) & 0xFF);

                                        if (outPosBytes >= outChunk.Length)
                                        {
                                            _onChunk(outChunk, outPosBytes, sampleRate);
                                            outPosBytes = 0;
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                captureClient.ReleaseBuffer(framesAvailable);
                            }

                            hr = captureClient.GetNextPacketSize(out packetFrames);
                            if (hr != 0) break;
                        }
                    }

                    try { audioClient.Stop(); } catch { }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Thread.Sleep(1000);
                }
                finally
                {
                    if (mixFmtPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFmtPtr);
                    if (captureClient != null) Marshal.ReleaseComObject(captureClient);
                    if (audioClient != null) Marshal.ReleaseComObject(audioClient);
                    if (device != null) Marshal.ReleaseComObject(device);
                    if (enumerator != null) Marshal.ReleaseComObject(enumerator);
                }
            }
        }

        private static void ReadStereoSample(
            byte[] buf, int offset, int channels, int bitsPerSample, bool isFloat,
            out float left, out float right)
        {
            left = 0f;
            right = 0f;
            if (channels <= 0) return;

            int bytesPerSample = bitsPerSample / 8;
            if (isFloat && bitsPerSample == 32)
            {
                left = BitConverter.ToSingle(buf, offset);
                right = channels >= 2 ? BitConverter.ToSingle(buf, offset + 4) : left;
                if (channels >= 6)
                {
                    float center = BitConverter.ToSingle(buf, offset + 8) * 0.707f;
                    left += center;
                    right += center;
                }
            }
            else if (!isFloat && bitsPerSample == 16)
            {
                left = BitConverter.ToInt16(buf, offset) / 32768f;
                right = channels >= 2 ? (BitConverter.ToInt16(buf, offset + 2) / 32768f) : left;
            }
            else if (!isFloat && bitsPerSample == 32)
            {
                left = BitConverter.ToInt32(buf, offset) / 2147483648f;
                right = channels >= 2 ? (BitConverter.ToInt32(buf, offset + 4) / 2147483648f) : left;
            }
            else if (!isFloat && bitsPerSample == 24)
            {
                left = ReadInt24(buf, offset) / 8388608f;
                right = channels >= 2 ? (ReadInt24(buf, offset + bytesPerSample) / 8388608f) : left;
            }
        }

        private static int ReadInt24(byte[] buf, int offset)
        {
            int v = buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16);
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            return v;
        }

        private static short FloatToInt16(float sample)
        {
            if (sample >= 1.0f) return short.MaxValue;
            if (sample <= -1.0f) return short.MinValue;
            return (short)(sample * 32767.0f);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr p;
            public int p2;

            public string GetStringValue()
            {
                if (vt == 31 && p != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(p);
                }
                return null;
            }
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);

            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);

            [PreserveSig]
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out uint pcDevices);

            [PreserveSig]
            int Item(uint nDevice, out IMMDevice ppDevice);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

            [PreserveSig]
            int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);

            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out uint cProps);

            [PreserveSig]
            int GetAt(uint iProp, out PropertyKey pkey);

            [PreserveSig]
            int GetValue(ref PropertyKey key, out PropVariant pv);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig]
            int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);

            [PreserveSig]
            int GetBufferSize(out uint pNumBufferFrames);

            [PreserveSig]
            int GetStreamLatency(out long phnsLatency);

            [PreserveSig]
            int GetCurrentPadding(out uint pNumPaddingFrames);

            [PreserveSig]
            int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);

            [PreserveSig]
            int GetMixFormat(out IntPtr ppDeviceFormat);

            [PreserveSig]
            int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

            [PreserveSig]
            int Start();

            [PreserveSig]
            int Stop();

            [PreserveSig]
            int Reset();

            [PreserveSig]
            int SetEventHandle(IntPtr eventHandle);

            [PreserveSig]
            int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);

            [PreserveSig]
            int ReleaseBuffer(uint numFramesRead);

            [PreserveSig]
            int GetNextPacketSize(out uint pNumFramesInNextPacket);
        }
    }
}
