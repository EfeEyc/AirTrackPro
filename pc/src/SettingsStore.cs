using System;
using System.IO;
using System.Xml.Serialization;

namespace AirTrackBridge
{
    [Serializable]
    public class BridgeSettings
    {
        public int HttpPort = 7878;
        public int UdpPort = 7879;
        public int DiscoveryPort = 7880;

        public string OpenTrackHost = "127.0.0.1";
        public int OpenTrackPort = 4242;

        public int OutputRateHz = 120;
        public double GyroLeadMs = 6.0;
        public double InterpSmoothingMs = 12.0;

        public bool EnableOneEuroFilter = true;
        public double OneEuroMinCutoff = 1.6;
        public double OneEuroBeta = 0.055;

        public double DeadzoneDegrees = 0.12;
        public double ResponseExponent = 1.15;

        public double YawSensitivity = 2.2;
        public double PitchSensitivity = 2.0;
        public double RollSensitivity = 1.2;

        public bool InvertYaw = false;
        public bool InvertPitch = false;
        public bool InvertRoll = false;
        public bool QuaternionTransposeMode = false;

        public bool EnableNeckModel = true;
        public double NeckModelIntensity = 0.65;

        public bool EnableYawDriftGuard = true;
        public double YawDriftGuardStrength = 0.25;

        public bool EnableAudioStream = true;
        public string AudioDeviceId = "";
        public double AudioGain = 1.0;

        public bool EnableGlobalHotkey = true;
        public int HotkeyVirtualKey = 0x7B; // F12

        public string SelectedAdapterIp = "";
        public bool AutoOpenBrowser = true;
    }

    public class SettingsStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private BridgeSettings _current;

        public SettingsStore(string filePath)
        {
            _filePath = filePath;
            _current = LoadInternal();
        }

        public BridgeSettings Current
        {
            get
            {
                lock (_lock)
                {
                    return _current;
                }
            }
        }

        public BridgeSettings GetSnapshot()
        {
            lock (_lock)
            {
                var copy = new BridgeSettings();
                copy.HttpPort = _current.HttpPort;
                copy.UdpPort = _current.UdpPort;
                copy.DiscoveryPort = _current.DiscoveryPort;
                copy.OpenTrackHost = _current.OpenTrackHost;
                copy.OpenTrackPort = _current.OpenTrackPort;
                copy.OutputRateHz = _current.OutputRateHz;
                copy.GyroLeadMs = _current.GyroLeadMs;
                copy.InterpSmoothingMs = _current.InterpSmoothingMs;
                copy.EnableOneEuroFilter = _current.EnableOneEuroFilter;
                copy.OneEuroMinCutoff = _current.OneEuroMinCutoff;
                copy.OneEuroBeta = _current.OneEuroBeta;
                copy.DeadzoneDegrees = _current.DeadzoneDegrees;
                copy.ResponseExponent = _current.ResponseExponent;
                copy.YawSensitivity = _current.YawSensitivity;
                copy.PitchSensitivity = _current.PitchSensitivity;
                copy.RollSensitivity = _current.RollSensitivity;
                copy.InvertYaw = _current.InvertYaw;
                copy.InvertPitch = _current.InvertPitch;
                copy.InvertRoll = _current.InvertRoll;
                copy.QuaternionTransposeMode = _current.QuaternionTransposeMode;
                copy.EnableNeckModel = _current.EnableNeckModel;
                copy.NeckModelIntensity = _current.NeckModelIntensity;
                copy.EnableYawDriftGuard = _current.EnableYawDriftGuard;
                copy.YawDriftGuardStrength = _current.YawDriftGuardStrength;
                copy.EnableAudioStream = _current.EnableAudioStream;
                copy.AudioDeviceId = _current.AudioDeviceId;
                copy.AudioGain = _current.AudioGain;
                copy.EnableGlobalHotkey = _current.EnableGlobalHotkey;
                copy.HotkeyVirtualKey = _current.HotkeyVirtualKey;
                copy.SelectedAdapterIp = _current.SelectedAdapterIp;
                copy.AutoOpenBrowser = _current.AutoOpenBrowser;
                return copy;
            }
        }

        public void Update(Action<BridgeSettings> mutator)
        {
            lock (_lock)
            {
                mutator(_current);
                Sanitize(_current);
                SaveInternal(_current);
            }
        }

        private static void Sanitize(BridgeSettings s)
        {
            if (s.HttpPort < 1024 || s.HttpPort > 65535) s.HttpPort = 7878;
            if (s.UdpPort < 1024 || s.UdpPort > 65535) s.UdpPort = 7879;
            if (s.DiscoveryPort < 1024 || s.DiscoveryPort > 65535) s.DiscoveryPort = 7880;
            if (string.IsNullOrWhiteSpace(s.OpenTrackHost)) s.OpenTrackHost = "127.0.0.1";
            if (s.OpenTrackPort < 1 || s.OpenTrackPort > 65535) s.OpenTrackPort = 4242;
            if (s.OutputRateHz < 30) s.OutputRateHz = 30;
            if (s.OutputRateHz > 240) s.OutputRateHz = 240;
            s.GyroLeadMs = Clamp(s.GyroLeadMs, 0.0, 30.0);
            s.InterpSmoothingMs = Clamp(s.InterpSmoothingMs, 1.0, 50.0);
            s.OneEuroMinCutoff = Clamp(s.OneEuroMinCutoff, 0.1, 10.0);
            s.OneEuroBeta = Clamp(s.OneEuroBeta, 0.0, 0.5);
            s.DeadzoneDegrees = Clamp(s.DeadzoneDegrees, 0.0, 5.0);
            s.ResponseExponent = Clamp(s.ResponseExponent, 0.5, 3.0);
            s.YawSensitivity = Clamp(s.YawSensitivity, 0.1, 10.0);
            s.PitchSensitivity = Clamp(s.PitchSensitivity, 0.1, 10.0);
            s.RollSensitivity = Clamp(s.RollSensitivity, 0.1, 10.0);
            s.NeckModelIntensity = Clamp(s.NeckModelIntensity, 0.0, 2.5);
            s.YawDriftGuardStrength = Clamp(s.YawDriftGuardStrength, 0.0, 1.0);
            s.AudioGain = Clamp(s.AudioGain, 0.0, 2.0);
        }

        private static double Clamp(double val, double min, double max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }

        private BridgeSettings LoadInternal()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var ser = new XmlSerializer(typeof(BridgeSettings));
                    using (var fs = File.OpenRead(_filePath))
                    {
                        var loaded = ser.Deserialize(fs) as BridgeSettings;
                        if (loaded != null)
                        {
                            Sanitize(loaded);
                            return loaded;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to defaults if XML is malformed
            }
            var def = new BridgeSettings();
            SaveInternal(def);
            return def;
        }

        private void SaveInternal(BridgeSettings settings)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var ser = new XmlSerializer(typeof(BridgeSettings));
                using (var fs = File.Create(_filePath))
                {
                    ser.Serialize(fs, settings);
                }
            }
            catch
            {
                // Ignore disk save errors
            }
        }
    }
}
