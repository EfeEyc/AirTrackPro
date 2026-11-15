using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;

namespace AirTrackBridge
{
    public struct Quat
    {
        public double W, X, Y, Z;

        public Quat(double w, double x, double y, double z)
        {
            W = w; X = x; Y = y; Z = z;
        }

        public static Quat Identity
        {
            get { return new Quat(1.0, 0.0, 0.0, 0.0); }
        }

        public Quat Normalized()
        {
            double n = Math.Sqrt(W * W + X * X + Y * Y + Z * Z);
            if (n < 1e-12) return Identity;
            double inv = 1.0 / n;
            return new Quat(W * inv, X * inv, Y * inv, Z * inv);
        }

        public Quat Conjugate()
        {
            return new Quat(W, -X, -Y, -Z);
        }

        public static Quat Multiply(Quat a, Quat b)
        {
            return new Quat(
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W
            );
        }

        public static Quat Slerp(Quat a, Quat b, double t)
        {
            if (t <= 0.0) return a.Normalized();
            if (t >= 1.0) return b.Normalized();

            double dot = a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            Quat bb = b;
            if (dot < 0.0)
            {
                dot = -dot;
                bb = new Quat(-b.W, -b.X, -b.Y, -b.Z);
            }

            if (dot > 0.9995)
            {
                return new Quat(
                    a.W + t * (bb.W - a.W),
                    a.X + t * (bb.X - a.X),
                    a.Y + t * (bb.Y - a.Y),
                    a.Z + t * (bb.Z - a.Z)
                ).Normalized();
            }

            double theta0 = Math.Acos(Math.Min(1.0, Math.Max(-1.0, dot)));
            double theta = theta0 * t;
            double sinTheta = Math.Sin(theta);
            double sinTheta0 = Math.Sin(theta0);
            double s0 = Math.Cos(theta) - dot * sinTheta / sinTheta0;
            double s1 = sinTheta / sinTheta0;
            return new Quat(
                s0 * a.W + s1 * bb.W,
                s0 * a.X + s1 * bb.X,
                s0 * a.Y + s1 * bb.Y,
                s0 * a.Z + s1 * bb.Z
            ).Normalized();
        }
    }

    internal sealed class OneEuroScalar
    {
        private bool _hasPrev;
        private double _xPrev;
        private double _dxPrev;

        public void Reset()
        {
            _hasPrev = false;
            _xPrev = 0.0;
            _dxPrev = 0.0;
        }

        public double Filter(double x, double dt, double minCutoff, double beta)
        {
            if (!_hasPrev || dt <= 0.0)
            {
                _hasPrev = true;
                _xPrev = x;
                _dxPrev = 0.0;
                return x;
            }

            double rate = 1.0 / dt;
            double dx = (x - _xPrev) * rate;
            double alphaD = Alpha(rate, 1.0);
            double edx = alphaD * dx + (1.0 - alphaD) * _dxPrev;
            double cutoff = minCutoff + beta * Math.Abs(edx);
            double alpha = Alpha(rate, cutoff);
            double filtered = alpha * x + (1.0 - alpha) * _xPrev;

            _xPrev = filtered;
            _dxPrev = edx;
            return filtered;
        }

        private static double Alpha(double rate, double cutoff)
        {
            double tau = 1.0 / (2.0 * Math.PI * Math.Max(0.01, cutoff));
            double te = 1.0 / Math.Max(1.0, rate);
            return 1.0 / (1.0 + tau / te);
        }
    }

    public sealed class TrackingTelemetry
    {
        public bool PhoneConnected;
        public bool AirPodsActive;
        public int SensorLocation;
        public double InputHz;
        public double OutputHz;
        public long PacketsIn;
        public long PacketsOut;
        public double RawYaw;
        public double RawPitch;
        public double RawRoll;
        public double OutX;
        public double OutY;
        public double OutZ;
        public double OutYaw;
        public double OutPitch;
        public double OutRoll;
        public bool WantsAudio;
        public string PhoneEndpoint = "";
        public double JitterMs;
    }

    public sealed class HeadTrackingEngine : IDisposable
    {
        private readonly SettingsStore _settingsStore;
        private readonly object _stateLock = new object();
        private readonly UdpClient _openTrackUdp = new UdpClient();
        private readonly byte[] _openTrackPacket = new byte[48];

        private readonly OneEuroScalar _yawEuro = new OneEuroScalar();
        private readonly OneEuroScalar _pitchEuro = new OneEuroScalar();
        private readonly OneEuroScalar _rollEuro = new OneEuroScalar();

        private Thread _workerThread;
        private volatile bool _running;

        private bool _hasReference;
        private Quat _qRef = Quat.Identity;
        private Quat _qLatest = Quat.Identity;
        private Quat _qSmoothed = Quat.Identity;
        private double _rx, _ry, _rz;
        private int _sensorLocation;
        private bool _airPodsActive;
        private bool _wantsAudio;
        private string _phoneEndpoint = "";

        private long _lastPacketTicks;
        private long _prevPacketTicks;
        private double _inputHz;
        private double _outputHz;
        private double _jitterMs;
        private long _packetsIn;
        private long _packetsOut;
        private uint _lastSeq;
        private double _yawDriftBias;
        private double _stationarySeconds;
        private long _testPulseUntilTicks;

        private double _rawYaw, _rawPitch, _rawRoll;
        private double _outX, _outY, _outZ, _outYaw, _outPitch, _outRoll;
        private string _cachedOtHost = "";
        private int _cachedOtPort;

        public HeadTrackingEngine(SettingsStore settingsStore)
        {
            _settingsStore = settingsStore;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _workerThread = new Thread(OutputLoop);
            _workerThread.IsBackground = true;
            _workerThread.Priority = ThreadPriority.AboveNormal;
            _workerThread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_workerThread != null && _workerThread.IsAlive)
            {
                _workerThread.Join(500);
            }
            try { _openTrackUdp.Close(); } catch { }
        }

        public void Dispose()
        {
            Stop();
        }

        public void Recenter()
        {
            lock (_stateLock)
            {
                _qRef = _qLatest;
                _qSmoothed = Quat.Identity;
                _yawDriftBias = 0.0;
                _yawEuro.Reset();
                _pitchEuro.Reset();
                _rollEuro.Reset();
            }
        }

        public void TriggerTestPulse()
        {
            lock (_stateLock)
            {
                _testPulseUntilTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
            }
        }

        public void IngestBinaryPacket(byte[] buf, int len, string remoteEndpoint)
        {
            // Packet format ATPT: 128 bytes minimum (or 48+ keepalive)
            if (len < 16) return;
            if (buf[0] != (byte)'A' || buf[1] != (byte)'T' || buf[2] != (byte)'P' || buf[3] != (byte)'T')
                return;

            uint flags = BitConverter.ToUInt32(buf, 4);
            uint seq = BitConverter.ToUInt32(buf, 8);
            int sensorLoc = BitConverter.ToInt32(buf, 12);

            bool active = (flags & 1u) != 0;
            bool wantsAudio = (flags & 2u) != 0;
            bool recenterReq = (flags & 4u) != 0;

            long nowTicks = Stopwatch.GetTimestamp();

            lock (_stateLock)
            {
                _phoneEndpoint = remoteEndpoint ?? _phoneEndpoint;
                _wantsAudio = wantsAudio;
                _airPodsActive = active;
                _sensorLocation = sensorLoc;
                _packetsIn++;

                if (len >= 80 && active)
                {
                    double qw = BitConverter.ToDouble(buf, 24);
                    double qx = BitConverter.ToDouble(buf, 32);
                    double qy = BitConverter.ToDouble(buf, 40);
                    double qz = BitConverter.ToDouble(buf, 48);
                    double rx = BitConverter.ToDouble(buf, 56);
                    double ry = BitConverter.ToDouble(buf, 64);
                    double rz = BitConverter.ToDouble(buf, 72);

                    if (!double.IsNaN(qw) && !double.IsNaN(qx) && !double.IsNaN(qy) && !double.IsNaN(qz))
                    {
                        var q = new Quat(qw, qx, qy, qz).Normalized();
                        _qLatest = q;
                        _rx = double.IsNaN(rx) ? 0.0 : rx;
                        _ry = double.IsNaN(ry) ? 0.0 : ry;
                        _rz = double.IsNaN(rz) ? 0.0 : rz;

                        if (!_hasReference || recenterReq)
                        {
                            _qRef = q;
                            _hasReference = true;
                            _qSmoothed = Quat.Identity;
                            _yawDriftBias = 0.0;
                            _yawEuro.Reset();
                            _pitchEuro.Reset();
                            _rollEuro.Reset();
                        }
                    }

                    if (_prevPacketTicks > 0 && seq != _lastSeq)
                    {
                        double dtMs = (nowTicks - _prevPacketTicks) * 1000.0 / Stopwatch.Frequency;
                        if (dtMs > 2.0 && dtMs < 500.0)
                        {
                            double hz = 1000.0 / dtMs;
                            _inputHz = _inputHz <= 0.1 ? hz : (_inputHz * 0.85 + hz * 0.15);
                            double expectedMs = _inputHz > 1.0 ? (1000.0 / _inputHz) : 40.0;
                            double jitter = Math.Abs(dtMs - expectedMs);
                            _jitterMs = _jitterMs * 0.9 + jitter * 0.1;
                        }
                    }
                    _prevPacketTicks = nowTicks;
                }
                else if (recenterReq)
                {
                    _qRef = _qLatest;
                    _qSmoothed = Quat.Identity;
                }

                _lastSeq = seq;
                _lastPacketTicks = nowTicks;
            }
        }

        public TrackingTelemetry GetTelemetry()
        {
            long nowTicks = Stopwatch.GetTimestamp();
            lock (_stateLock)
            {
                bool connected = _lastPacketTicks > 0 &&
                    ((nowTicks - _lastPacketTicks) / (double)Stopwatch.Frequency) < 2.5;
                return new TrackingTelemetry
                {
                    PhoneConnected = connected,
                    AirPodsActive = connected && _airPodsActive,
                    SensorLocation = _sensorLocation,
                    InputHz = connected ? Math.Round(_inputHz, 1) : 0.0,
                    OutputHz = Math.Round(_outputHz, 1),
                    PacketsIn = _packetsIn,
                    PacketsOut = _packetsOut,
                    RawYaw = Math.Round(_rawYaw, 2),
                    RawPitch = Math.Round(_rawPitch, 2),
                    RawRoll = Math.Round(_rawRoll, 2),
                    OutX = Math.Round(_outX, 1),
                    OutY = Math.Round(_outY, 1),
                    OutZ = Math.Round(_outZ, 1),
                    OutYaw = Math.Round(_outYaw, 2),
                    OutPitch = Math.Round(_outPitch, 2),
                    OutRoll = Math.Round(_outRoll, 2),
                    WantsAudio = connected && _wantsAudio,
                    PhoneEndpoint = connected ? _phoneEndpoint : "",
                    JitterMs = connected ? Math.Round(_jitterMs, 1) : 0.0
                };
            }
        }

        private void OutputLoop()
        {
            long freq = Stopwatch.Frequency;
            long prevTick = Stopwatch.GetTimestamp();

            while (_running)
            {
                BridgeSettings s = _settingsStore.GetSnapshot();
                double targetPeriodSec = 1.0 / Math.Max(30, s.OutputRateHz);
                long targetTicks = (long)(targetPeriodSec * freq);

                long nowTick = Stopwatch.GetTimestamp();
                double dt = (nowTick - prevTick) / (double)freq;
                if (dt <= 0.0 || dt > 0.25) dt = targetPeriodSec;
                prevTick = nowTick;

                double instOutHz = Math.Min(s.OutputRateHz * 1.25, 1.0 / dt);
                bool sendPacket = false;
                double outX = 0, outY = 0, outZ = 0, outYaw = 0, outPitch = 0, outRoll = 0;

                lock (_stateLock)
                {
                    _outputHz = _outputHz <= 0.1 ? instOutHz : (_outputHz * 0.9 + instOutHz * 0.1);

                    bool pulseActive = nowTick < _testPulseUntilTicks;
                    double ageSec = _lastPacketTicks > 0 ? (nowTick - _lastPacketTicks) / (double)freq : 999.0;
                    bool trackingAlive = _hasReference && _airPodsActive && ageSec < 1.2;

                    if (pulseActive)
                    {
                        double phase = ((_testPulseUntilTicks - nowTick) / (double)freq) * Math.PI * 2.0;
                        outYaw = Math.Sin(phase) * 25.0;
                        outPitch = Math.Cos(phase) * 12.0;
                        outRoll = Math.Sin(phase * 0.5) * 8.0;
                        _rawYaw = outYaw;
                        _rawPitch = outPitch;
                        _rawRoll = outRoll;
                        _outX = 0; _outY = 0; _outZ = 0;
                        _outYaw = outYaw; _outPitch = outPitch; _outRoll = outRoll;
                        sendPacket = true;
                    }
                    else if (trackingAlive)
                    {
                        Quat targetQ = _qLatest;
                        if (s.GyroLeadMs > 0.1 && ageSec < 0.08)
                        {
                            double lead = Math.Min(ageSec, s.GyroLeadMs / 1000.0) * Math.Exp(-ageSec / 0.035);
                            targetQ = IntegrateGyro(targetQ, _rx, _ry, _rz, lead);
                        }

                        Quat qRel = s.QuaternionTransposeMode
                            ? Quat.Multiply(targetQ, _qRef.Conjugate())
                            : Quat.Multiply(_qRef.Conjugate(), targetQ);

                        double tau = Math.Max(0.001, s.InterpSmoothingMs / 1000.0);
                        double alpha = 1.0 - Math.Exp(-dt / tau);
                        _qSmoothed = Quat.Slerp(_qSmoothed, qRel, alpha);

                        double rawYaw, rawPitch, rawRoll;
                        ExtractHeadEulerDegrees(_qSmoothed, out rawYaw, out rawPitch, out rawRoll);

                        if (s.EnableYawDriftGuard)
                        {
                            double omegaNorm = Math.Sqrt(_rx * _rx + _ry * _ry + _rz * _rz);
                            if (omegaNorm < 0.025)
                            {
                                _stationarySeconds += dt;
                                if (_stationarySeconds > 0.6 && Math.Abs(rawYaw - _yawDriftBias) < 3.5)
                                {
                                    double trimRate = 0.08 * s.YawDriftGuardStrength;
                                    _yawDriftBias += (rawYaw - _yawDriftBias) * Math.Min(1.0, trimRate * dt);
                                }
                            }
                            else
                            {
                                _stationarySeconds = 0.0;
                            }
                            rawYaw = WrapAngle180(rawYaw - _yawDriftBias);
                        }

                        _rawYaw = rawYaw;
                        _rawPitch = rawPitch;
                        _rawRoll = rawRoll;

                        double fYaw = rawYaw;
                        double fPitch = rawPitch;
                        double fRoll = rawRoll;
                        if (s.EnableOneEuroFilter)
                        {
                            fYaw = _yawEuro.Filter(rawYaw, dt, s.OneEuroMinCutoff, s.OneEuroBeta);
                            fPitch = _pitchEuro.Filter(rawPitch, dt, s.OneEuroMinCutoff, s.OneEuroBeta);
                            fRoll = _rollEuro.Filter(rawRoll, dt, s.OneEuroMinCutoff, s.OneEuroBeta);
                        }

                        if (s.InvertYaw) fYaw = -fYaw;
                        if (s.InvertPitch) fPitch = -fPitch;
                        if (s.InvertRoll) fRoll = -fRoll;

                        outYaw = Clamp(ShapeAxis(fYaw, s.DeadzoneDegrees, s.ResponseExponent, s.YawSensitivity), -180.0, 180.0);
                        outPitch = Clamp(ShapeAxis(fPitch, s.DeadzoneDegrees, s.ResponseExponent, s.PitchSensitivity), -89.0, 89.0);
                        outRoll = Clamp(ShapeAxis(fRoll, s.DeadzoneDegrees, s.ResponseExponent, s.RollSensitivity), -180.0, 180.0);

                        if (s.EnableNeckModel && s.NeckModelIntensity > 0.0)
                        {
                            double yRad = fYaw * Math.PI / 180.0;
                            double pRad = fPitch * Math.PI / 180.0;
                            double rRad = fRoll * Math.PI / 180.0;
                            double k = s.NeckModelIntensity;
                            outX = (Math.Sin(yRad) * 65.0 + Math.Sin(rRad) * 130.0) * k;
                            outY = ((Math.Cos(rRad) - 1.0) * 130.0 + (Math.Cos(pRad) - 1.0) * 55.0) * k;
                            outZ = ((Math.Cos(yRad) - 1.0) * 65.0 + Math.Sin(pRad) * 85.0) * k;
                        }

                        _outX = outX;
                        _outY = outY;
                        _outZ = outZ;
                        _outYaw = outYaw;
                        _outPitch = outPitch;
                        _outRoll = outRoll;
                        sendPacket = true;
                    }
                    else if (ageSec >= 1.2)
                    {
                        _inputHz = 0.0;
                    }
                }

                if (sendPacket)
                {
                    SendToOpenTrack(s.OpenTrackHost, s.OpenTrackPort, outX, outY, outZ, outYaw, outPitch, outRoll);
                }

                long elapsedTicks = Stopwatch.GetTimestamp() - nowTick;
                long remainTicks = targetTicks - elapsedTicks;
                if (remainTicks > 0)
                {
                    int sleepMs = (int)(remainTicks * 1000 / freq);
                    if (sleepMs > 1)
                    {
                        Thread.Sleep(sleepMs - 1);
                    }
                    while (Stopwatch.GetTimestamp() - nowTick < targetTicks)
                    {
                        Thread.SpinWait(20);
                    }
                }
            }
        }

        private void SendToOpenTrack(string host, int port, double x, double y, double z, double yaw, double pitch, double roll)
        {
            try
            {
                if (_cachedOtHost != host || _cachedOtPort != port)
                {
                    _openTrackUdp.Connect(host, port);
                    _cachedOtHost = host;
                    _cachedOtPort = port;
                }

                WriteDouble(_openTrackPacket, 0, x);
                WriteDouble(_openTrackPacket, 8, y);
                WriteDouble(_openTrackPacket, 16, z);
                WriteDouble(_openTrackPacket, 24, yaw);
                WriteDouble(_openTrackPacket, 32, pitch);
                WriteDouble(_openTrackPacket, 40, roll);

                _openTrackUdp.Send(_openTrackPacket, 48);
                Interlocked.Increment(ref _packetsOut);
            }
            catch
            {
                _cachedOtHost = "";
            }
        }

        private static void WriteDouble(byte[] buf, int offset, double val)
        {
            byte[] b = BitConverter.GetBytes(val);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            Buffer.BlockCopy(b, 0, buf, offset, 8);
        }

        private static Quat IntegrateGyro(Quat q, double wx, double wy, double wz, double dt)
        {
            double mag = Math.Sqrt(wx * wx + wy * wy + wz * wz);
            if (mag < 1e-6 || dt <= 0.0) return q;
            double halfAngle = 0.5 * mag * dt;
            double s = Math.Sin(halfAngle) / mag;
            var dq = new Quat(Math.Cos(halfAngle), wx * s, wy * s, wz * s);
            return Quat.Multiply(q, dq).Normalized();
        }

        private static void ExtractHeadEulerDegrees(Quat q, out double yawDeg, out double pitchDeg, out double rollDeg)
        {
            // CoreMotion headphone frame: +X right ear, +Y forward nose, +Z up
            // OpenTrack convention: +Yaw right, +Pitch up, +Roll right ear down
            double w = q.W, x = q.X, y = q.Y, z = q.Z;
            double r01 = 2.0 * (x * y - w * z);
            double r11 = 1.0 - 2.0 * (x * x + z * z);
            double r20 = 2.0 * (x * z - w * y);
            double r21 = 2.0 * (y * z + w * x);
            double r22 = 1.0 - 2.0 * (x * x + y * y);

            double pitchRad = Math.Atan2(r21, Math.Sqrt(r20 * r20 + r22 * r22));
            double yawRad = Math.Atan2(r01, r11);
            double rollRad = Math.Atan2(-r20, r22);

            const double radToDeg = 180.0 / Math.PI;
            yawDeg = yawRad * radToDeg;
            pitchDeg = pitchRad * radToDeg;
            rollDeg = rollRad * radToDeg;
        }

        private static double ShapeAxis(double angleDeg, double deadzoneDeg, double exponent, double sensitivity)
        {
            double abs = Math.Abs(angleDeg);
            if (abs <= deadzoneDeg) return 0.0;
            double eff = abs - deadzoneDeg;
            double shaped = Math.Pow(eff, exponent) * sensitivity;
            return angleDeg >= 0.0 ? shaped : -shaped;
        }

        private static double WrapAngle180(double deg)
        {
            while (deg > 180.0) deg -= 360.0;
            while (deg < -180.0) deg += 360.0;
            return deg;
        }

        private static double Clamp(double val, double min, double max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }
    }
}
