using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AirTrackBridge
{
    public sealed class LanAdapterInfo
    {
        public string Ip = "";
        public string Name = "";
        public string Broadcast = "";
        public int Priority;
    }

    public sealed class BridgeServer : IDisposable
    {
        private readonly SettingsStore _settingsStore;
        private readonly HeadTrackingEngine _tracking;
        private readonly WasapiLoopbackCapture _audioCapture;

        private TcpListener _tcpListener;
        private UdpClient _udpSocket;
        private UdpClient _discoverySocket;

        private Thread _httpAcceptThread;
        private Thread _udpRecvThread;
        private Thread _discoveryThread;
        private Thread _telemetryThread;

        private volatile bool _running;

        private readonly object _wsLock = new object();
        private readonly List<WsClient> _wsClients = new List<WsClient>();

        private readonly object _audioTargetLock = new object();
        private IPEndPoint _phoneUdpEndpoint;
        private long _lastPhoneUdpTicks;
        private int _audioSeq;
        private long _audioBytesThisWindow;
        private double _audioKbps;
        private bool _audioSendingActive;

        private sealed class WsClient
        {
            public TcpClient Tcp;
            public NetworkStream Stream;
            public string Role = "web";
            public readonly object WriteLock = new object();
            public volatile bool Alive = true;
        }

        public BridgeServer(SettingsStore settingsStore, HeadTrackingEngine tracking, WasapiLoopbackCapture audioCapture)
        {
            _settingsStore = settingsStore;
            _tracking = tracking;
            _audioCapture = audioCapture;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            BridgeSettings s = _settingsStore.GetSnapshot();

            _tcpListener = new TcpListener(IPAddress.Any, s.HttpPort);
            _tcpListener.Start();

            _udpSocket = new UdpClient(s.UdpPort);
            TryDisableUdpConnReset(_udpSocket);

            try
            {
                _discoverySocket = new UdpClient(s.DiscoveryPort);
                _discoverySocket.EnableBroadcast = true;
                TryDisableUdpConnReset(_discoverySocket);
            }
            catch
            {
                _discoverySocket = new UdpClient();
                _discoverySocket.EnableBroadcast = true;
            }

            _httpAcceptThread = StartBg(HttpAcceptLoop);
            _udpRecvThread = StartBg(UdpRecvLoop);
            _discoveryThread = StartBg(DiscoveryLoop);
            _telemetryThread = StartBg(TelemetryAndAudioManagerLoop);
        }

        public void Stop()
        {
            _running = false;
            try { if (_tcpListener != null) _tcpListener.Stop(); } catch { }
            try { if (_udpSocket != null) _udpSocket.Close(); } catch { }
            try { if (_discoverySocket != null) _discoverySocket.Close(); } catch { }

            lock (_wsLock)
            {
                foreach (var c in _wsClients)
                {
                    c.Alive = false;
                    try { c.Tcp.Close(); } catch { }
                }
                _wsClients.Clear();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        public void OnDesktopAudioChunk(byte[] pcmBytes, int byteCount, int sampleRate)
        {
            BridgeSettings s = _settingsStore.GetSnapshot();
            if (!s.EnableAudioStream) return;

            TrackingTelemetry t = _tracking.GetTelemetry();
            if (!t.PhoneConnected || !t.WantsAudio) return;

            uint seq = (uint)Interlocked.Increment(ref _audioSeq);
            byte[] packet = new byte[16 + byteCount];
            packet[0] = (byte)'A';
            packet[1] = (byte)'T';
            packet[2] = (byte)'P';
            packet[3] = (byte)'A';
            WriteUInt32(packet, 4, seq);
            WriteUInt32(packet, 8, (uint)sampleRate);
            WriteUInt16(packet, 12, 2);
            WriteUInt16(packet, 14, 16);
            Buffer.BlockCopy(pcmBytes, 0, packet, 16, byteCount);

            IPEndPoint udpEp = null;
            bool udpFresh = false;
            lock (_audioTargetLock)
            {
                if (_phoneUdpEndpoint != null)
                {
                    long ageMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _lastPhoneUdpTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                    if (ageMs < 2500)
                    {
                        udpEp = _phoneUdpEndpoint;
                        udpFresh = true;
                    }
                }
            }

            if (udpFresh && udpEp != null && _udpSocket != null)
            {
                try
                {
                    _udpSocket.Send(packet, packet.Length, udpEp);
                    Interlocked.Add(ref _audioBytesThisWindow, packet.Length);
                    _audioSendingActive = true;
                    return;
                }
                catch { }
            }

            // WebSocket fallback if UDP is blocked
            List<WsClient> iosClients = GetClientsByRole("ios");
            foreach (var c in iosClients)
            {
                SendWsFrame(c, 0x02, packet, packet.Length);
                Interlocked.Add(ref _audioBytesThisWindow, packet.Length);
                _audioSendingActive = true;
            }
        }

        public static List<LanAdapterInfo> GetLanAdapters()
        {
            var result = new List<LanAdapterInfo>();
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface nic in nics)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    string name = nic.Name ?? "";
                    string desc = nic.Description ?? "";
                    bool isVirtual =
                        name.IndexOf("vEthernet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("WSL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        desc.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0;

                    IPInterfaceProperties props = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] ipBytes = ua.Address.GetAddressBytes();
                        if (ipBytes[0] == 169 && ipBytes[1] == 254) continue; // link-local

                        int prio = isVirtual ? 10 : 100;
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                            nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                        {
                            prio += 50;
                        }
                        if (ipBytes[0] == 192 && ipBytes[1] == 168) prio += 20;
                        else if (ipBytes[0] == 10) prio += 15;

                        string bcast = "255.255.255.255";
                        try
                        {
                            if (ua.IPv4Mask != null)
                            {
                                byte[] maskBytes = ua.IPv4Mask.GetAddressBytes();
                                byte[] bBytes = new byte[4];
                                for (int i = 0; i < 4; i++)
                                    bBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                                bcast = new IPAddress(bBytes).ToString();
                            }
                        }
                        catch { }

                        result.Add(new LanAdapterInfo
                        {
                            Ip = ua.Address.ToString(),
                            Name = name,
                            Broadcast = bcast,
                            Priority = prio
                        });
                    }
                }
            }
            catch { }

            result.Sort(delegate(LanAdapterInfo a, LanAdapterInfo b) { return b.Priority.CompareTo(a.Priority); });
            return result;
        }

        public string GetPrimaryIp(BridgeSettings s)
        {
            var list = GetLanAdapters();
            if (!string.IsNullOrEmpty(s.SelectedAdapterIp))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Ip == s.SelectedAdapterIp) return list[i].Ip;
                }
            }
            return list.Count > 0 ? list[0].Ip : "127.0.0.1";
        }

        private void HttpAcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _tcpListener.AcceptTcpClient();
                    client.NoDelay = true;
                    ThreadPool.QueueUserWorkItem(HandleTcpClient, client);
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(50);
                }
            }
        }

        private void HandleTcpClient(object obj)
        {
            TcpClient tcp = (TcpClient)obj;
            NetworkStream stream = null;
            try
            {
                stream = tcp.GetStream();
                stream.ReadTimeout = 5000;

                string reqHeader = ReadHttpHeader(stream);
                if (string.IsNullOrEmpty(reqHeader))
                {
                    tcp.Close();
                    return;
                }

                string[] lines = reqHeader.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2)
                {
                    tcp.Close();
                    return;
                }

                string method = requestLine[0].ToUpperInvariant();
                string rawTarget = requestLine[1];
                string path = rawTarget;
                string query = "";
                int qIdx = rawTarget.IndexOf('?');
                if (qIdx >= 0)
                {
                    path = rawTarget.Substring(0, qIdx);
                    query = rawTarget.Substring(qIdx + 1);
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int col = lines[i].IndexOf(':');
                    if (col > 0)
                    {
                        headers[lines[i].Substring(0, col).Trim()] = lines[i].Substring(col + 1).Trim();
                    }
                }

                string upgrade;
                if (headers.TryGetValue("Upgrade", out upgrade) &&
                    upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    HandleWebSocketUpgrade(tcp, stream, headers, query);
                    return;
                }

                int contentLength = 0;
                string clStr;
                if (headers.TryGetValue("Content-Length", out clStr))
                {
                    int.TryParse(clStr, out contentLength);
                }
                string body = "";
                if (contentLength > 0 && contentLength < 1048576)
                {
                    byte[] bodyBytes = ReadExact(stream, contentLength);
                    if (bodyBytes != null) body = Encoding.UTF8.GetString(bodyBytes);
                }

                RouteHttpRequest(stream, method, path, query, body);
                tcp.Close();
            }
            catch
            {
                try { tcp.Close(); } catch { }
            }
        }

        private void RouteHttpRequest(NetworkStream stream, string method, string path, string query, string body)
        {
            if (method == "GET" && path == "/")
            {
                SendHttpResponse(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(WebUiAssets.IndexHtml));
                return;
            }

            if (method == "GET" && path == "/api/bootstrap")
            {
                string json = BuildBootstrapJson();
                SendHttpResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                return;
            }

            if (method == "GET" && path == "/api/status")
            {
                string json = BuildTelemetryPayloadJson();
                SendHttpResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                return;
            }

            if (method == "GET" && path == "/api/qr.svg")
            {
                BridgeSettings s = _settingsStore.GetSnapshot();
                string ip = GetQueryParam(query, "ip");
                if (string.IsNullOrEmpty(ip)) ip = GetPrimaryIp(s);
                string uri = string.Format(CultureInfo.InvariantCulture, "airtrack://{0}:{1}:{2}", ip, s.HttpPort, s.UdpPort);
                string svg = QrSvgGenerator.GenerateSvg(uri, 6);
                SendHttpResponse(stream, "200 OK", "image/svg+xml; charset=utf-8", Encoding.UTF8.GetBytes(svg));
                return;
            }

            if (method == "POST" && path == "/api/settings")
            {
                ApplySettingsJson(body);
                SendHttpResponse(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                return;
            }

            if (method == "POST" && path == "/api/recenter")
            {
                _tracking.Recenter();
                SendHttpResponse(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                return;
            }

            if (method == "POST" && path == "/api/test-pulse")
            {
                _tracking.TriggerTestPulse();
                SendHttpResponse(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"));
                return;
            }

            SendHttpResponse(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("Not found"));
        }

        private void HandleWebSocketUpgrade(TcpClient tcp, NetworkStream stream, Dictionary<string, string> headers, string query)
        {
            string key;
            if (!headers.TryGetValue("Sec-WebSocket-Key", out key) || string.IsNullOrEmpty(key))
            {
                tcp.Close();
                return;
            }

            string acceptKey;
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
                acceptKey = Convert.ToBase64String(hash);
            }

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + acceptKey + "\r\n\r\n";
            byte[] respBytes = Encoding.ASCII.GetBytes(response);
            stream.Write(respBytes, 0, respBytes.Length);
            stream.ReadTimeout = Timeout.Infinite;

            string role = GetQueryParam(query, "role");
            if (string.IsNullOrEmpty(role)) role = "web";

            var client = new WsClient { Tcp = tcp, Stream = stream, Role = role };
            lock (_wsLock) { _wsClients.Add(client); }

            string remoteIp = "";
            try
            {
                var ep = tcp.Client.RemoteEndPoint as IPEndPoint;
                if (ep != null) remoteIp = ep.Address.ToString();
            }
            catch { }

            try
            {
                while (_running && client.Alive)
                {
                    int b0 = stream.ReadByte();
                    int b1 = stream.ReadByte();
                    if (b0 < 0 || b1 < 0) break;

                    int opcode = b0 & 0x0F;
                    bool masked = (b1 & 0x80) != 0;
                    long len = b1 & 0x7F;
                    if (len == 126)
                    {
                        byte[] ext = ReadExact(stream, 2);
                        if (ext == null) break;
                        len = (ext[0] << 8) | ext[1];
                    }
                    else if (len == 127)
                    {
                        byte[] ext = ReadExact(stream, 8);
                        if (ext == null) break;
                        len = 0;
                        for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
                    }

                    if (len < 0 || len > 1048576) break;

                    byte[] maskKey = null;
                    if (masked)
                    {
                        maskKey = ReadExact(stream, 4);
                        if (maskKey == null) break;
                    }

                    byte[] payload = len > 0 ? ReadExact(stream, (int)len) : new byte[0];
                    if (payload == null) break;

                    if (masked && maskKey != null)
                    {
                        for (int i = 0; i < payload.Length; i++)
                            payload[i] ^= maskKey[i & 3];
                    }

                    if (opcode == 0x08) // Close
                    {
                        SendWsFrame(client, 0x08, new byte[0], 0);
                        break;
                    }
                    else if (opcode == 0x09) // Ping
                    {
                        SendWsFrame(client, 0x0A, payload, payload.Length);
                    }
                    else if (opcode == 0x02) // Binary (iOS fallback ATPT packet)
                    {
                        _tracking.IngestBinaryPacket(payload, payload.Length, remoteIp + " (WS)");
                    }
                    else if (opcode == 0x01) // Text command
                    {
                        string text = Encoding.UTF8.GetString(payload);
                        if (text.IndexOf("recenter", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _tracking.Recenter();
                        }
                    }
                }
            }
            catch { }
            finally
            {
                client.Alive = false;
                lock (_wsLock) { _wsClients.Remove(client); }
                try { tcp.Close(); } catch { }
            }
        }

        private void UdpRecvLoop()
        {
            while (_running)
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _udpSocket.Receive(ref remote);
                    if (data != null && data.Length >= 16 &&
                        data[0] == (byte)'A' && data[1] == (byte)'T' && data[2] == (byte)'P' && data[3] == (byte)'T')
                    {
                        lock (_audioTargetLock)
                        {
                            _phoneUdpEndpoint = remote;
                            _lastPhoneUdpTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        }
                        _tracking.IngestBinaryPacket(data, data.Length, remote.Address.ToString());
                    }
                }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(5);
                }
            }
        }

        private void DiscoveryLoop()
        {
            // Also listen for incoming AIRTRACK_DISCOVER probes
            Thread probeListener = StartBg(delegate
            {
                while (_running)
                {
                    try
                    {
                        var remote = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = _discoverySocket.Receive(ref remote);
                        if (data != null && data.Length > 0)
                        {
                            string msg = Encoding.UTF8.GetString(data);
                            if (msg.IndexOf("AIRTRACK_DISCOVER", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                byte[] beacon = BuildBeaconBytes();
                                _discoverySocket.Send(beacon, beacon.Length, remote);
                            }
                        }
                    }
                    catch
                    {
                        if (!_running) break;
                        Thread.Sleep(100);
                    }
                }
            });

            while (_running)
            {
                try
                {
                    BridgeSettings s = _settingsStore.GetSnapshot();
                    byte[] beacon = BuildBeaconBytes();
                    var adapters = GetLanAdapters();
                    var sentBroadcasts = new HashSet<string>();
                    foreach (var a in adapters)
                    {
                        if (!string.IsNullOrEmpty(a.Broadcast) && sentBroadcasts.Add(a.Broadcast))
                        {
                            try
                            {
                                _discoverySocket.Send(beacon, beacon.Length, new IPEndPoint(IPAddress.Parse(a.Broadcast), s.DiscoveryPort));
                            }
                            catch { }
                        }
                    }
                    try
                    {
                        _discoverySocket.Send(beacon, beacon.Length, new IPEndPoint(IPAddress.Broadcast, s.DiscoveryPort));
                    }
                    catch { }
                }
                catch { }
                Thread.Sleep(1200);
            }
        }

        private void TelemetryAndAudioManagerLoop()
        {
            long prevTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            while (_running)
            {
                Thread.Sleep(33); // ~30 Hz Web UI update

                BridgeSettings s = _settingsStore.GetSnapshot();
                _audioCapture.SetGain(s.AudioGain);

                TrackingTelemetry t = _tracking.GetTelemetry();
                bool shouldCaptureAudio = s.EnableAudioStream && t.PhoneConnected && t.WantsAudio;
                if (shouldCaptureAudio)
                {
                    _audioCapture.Start(s.AudioDeviceId);
                }
                else
                {
                    _audioCapture.Stop();
                    _audioSendingActive = false;
                }

                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                double elapsedSec = (nowTicks - prevTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (elapsedSec >= 0.4)
                {
                    long bytes = Interlocked.Exchange(ref _audioBytesThisWindow, 0);
                    _audioKbps = (bytes * 8.0 / 1000.0) / elapsedSec;
                    if (!shouldCaptureAudio) _audioKbps = 0.0;
                    prevTicks = nowTicks;
                }

                List<WsClient> webClients = GetClientsByRole("web");
                if (webClients.Count > 0)
                {
                    string json = BuildTelemetryPayloadJson();
                    byte[] payload = Encoding.UTF8.GetBytes(json);
                    foreach (var c in webClients)
                    {
                        SendWsFrame(c, 0x01, payload, payload.Length);
                    }
                }
            }
        }

        private byte[] BuildBeaconBytes()
        {
            BridgeSettings s = _settingsStore.GetSnapshot();
            string ip = GetPrimaryIp(s);
            string host = EscapeJson(Environment.MachineName);
            string json = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"app\":\"AirTrackPro\",\"host\":\"{0}\",\"ip\":\"{1}\",\"httpPort\":{2},\"udpPort\":{3}}}",
                host, EscapeJson(ip), s.HttpPort, s.UdpPort);
            return Encoding.UTF8.GetBytes(json);
        }

        private string BuildBootstrapJson()
        {
            BridgeSettings s = _settingsStore.GetSnapshot();
            var adapters = GetLanAdapters();
            var devices = WasapiLoopbackCapture.ListRenderDevices();

            var sb = new StringBuilder();
            sb.Append("{");
            sb.AppendFormat("\"primaryIp\":\"{0}\",", EscapeJson(GetPrimaryIp(s)));
            sb.Append("\"adapters\":[");
            for (int i = 0; i < adapters.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.AppendFormat("{{\"ip\":\"{0}\",\"name\":\"{1}\"}}",
                    EscapeJson(adapters[i].Ip), EscapeJson(adapters[i].Name));
            }
            sb.Append("],\"audioDevices\":[");
            for (int i = 0; i < devices.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.AppendFormat("{{\"id\":\"{0}\",\"name\":\"{1}\",\"isDefault\":{2}}}",
                    EscapeJson(devices[i].Id), EscapeJson(devices[i].Name), devices[i].IsDefault ? "true" : "false");
            }
            sb.Append("],\"settings\":");
            sb.Append(SerializeSettings(s));
            sb.Append("}");
            return sb.ToString();
        }

        private string BuildTelemetryPayloadJson()
        {
            TrackingTelemetry t = _tracking.GetTelemetry();
            bool audActive = _audioSendingActive && t.PhoneConnected && t.WantsAudio;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"type\":\"telemetry\",\"audioActive\":{0},\"audioKbps\":{1:F1},\"data\":{{" +
                "\"phoneConnected\":{2},\"airPodsActive\":{3},\"sensorLocation\":{4}," +
                "\"inputHz\":{5:F1},\"outputHz\":{6:F1},\"packetsIn\":{7},\"packetsOut\":{8}," +
                "\"rawYaw\":{9:F2},\"rawPitch\":{10:F2},\"rawRoll\":{11:F2}," +
                "\"outX\":{12:F1},\"outY\":{13:F1},\"outZ\":{14:F1}," +
                "\"outYaw\":{15:F2},\"outPitch\":{16:F2},\"outRoll\":{17:F2}," +
                "\"wantsAudio\":{18},\"phoneEndpoint\":\"{19}\",\"jitterMs\":{20}",
                audActive ? "true" : "false",
                _audioKbps,
                t.PhoneConnected ? "true" : "false",
                t.AirPodsActive ? "true" : "false",
                t.SensorLocation,
                t.InputHz, t.OutputHz, t.PacketsIn, t.PacketsOut,
                t.RawYaw, t.RawPitch, t.RawRoll,
                t.OutX, t.OutY, t.OutZ,
                t.OutYaw, t.OutPitch, t.OutRoll,
                t.WantsAudio ? "true" : "false",
                EscapeJson(t.PhoneEndpoint),
                t.JitterMs.ToString("F1", CultureInfo.InvariantCulture)) + "}}";
        }

        private static string SerializeSettings(BridgeSettings s)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"HttpPort\":{0},\"UdpPort\":{1},\"OpenTrackHost\":\"{2}\",\"OpenTrackPort\":{3}," +
                "\"OutputRateHz\":{4},\"GyroLeadMs\":{5},\"InterpSmoothingMs\":{6}," +
                "\"EnableOneEuroFilter\":{7},\"OneEuroMinCutoff\":{8},\"OneEuroBeta\":{9}," +
                "\"DeadzoneDegrees\":{10},\"ResponseExponent\":{11}," +
                "\"YawSensitivity\":{12},\"PitchSensitivity\":{13},\"RollSensitivity\":{14}," +
                "\"InvertYaw\":{15},\"InvertPitch\":{16},\"InvertRoll\":{17},\"QuaternionTransposeMode\":{18}," +
                "\"EnableNeckModel\":{19},\"NeckModelIntensity\":{20}," +
                "\"EnableYawDriftGuard\":{21},\"YawDriftGuardStrength\":{22}," +
                "\"EnableAudioStream\":{23},\"AudioDeviceId\":\"{24}\",\"AudioGain\":{25}," +
                "\"EnableGlobalHotkey\":{26},\"SelectedAdapterIp\":\"{27}\"}}",
                s.HttpPort, s.UdpPort, EscapeJson(s.OpenTrackHost), s.OpenTrackPort,
                s.OutputRateHz, s.GyroLeadMs, s.InterpSmoothingMs,
                B(s.EnableOneEuroFilter), s.OneEuroMinCutoff, s.OneEuroBeta,
                s.DeadzoneDegrees, s.ResponseExponent,
                s.YawSensitivity, s.PitchSensitivity, s.RollSensitivity,
                B(s.InvertYaw), B(s.InvertPitch), B(s.InvertRoll), B(s.QuaternionTransposeMode),
                B(s.EnableNeckModel), s.NeckModelIntensity,
                B(s.EnableYawDriftGuard), s.YawDriftGuardStrength,
                B(s.EnableAudioStream), EscapeJson(s.AudioDeviceId), s.AudioGain,
                B(s.EnableGlobalHotkey), EscapeJson(s.SelectedAdapterIp));
        }

        private void ApplySettingsJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            _settingsStore.Update(delegate(BridgeSettings s)
            {
                string str;
                double num;
                bool b;
                if (TryJsonString(json, "OpenTrackHost", out str)) s.OpenTrackHost = str;
                if (TryJsonNumber(json, "OpenTrackPort", out num)) s.OpenTrackPort = (int)Math.Round(num);
                if (TryJsonNumber(json, "OutputRateHz", out num)) s.OutputRateHz = (int)Math.Round(num);
                if (TryJsonNumber(json, "GyroLeadMs", out num)) s.GyroLeadMs = num;
                if (TryJsonNumber(json, "InterpSmoothingMs", out num)) s.InterpSmoothingMs = num;
                if (TryJsonBool(json, "EnableOneEuroFilter", out b)) s.EnableOneEuroFilter = b;
                if (TryJsonNumber(json, "OneEuroMinCutoff", out num)) s.OneEuroMinCutoff = num;
                if (TryJsonNumber(json, "OneEuroBeta", out num)) s.OneEuroBeta = num;
                if (TryJsonNumber(json, "DeadzoneDegrees", out num)) s.DeadzoneDegrees = num;
                if (TryJsonNumber(json, "ResponseExponent", out num)) s.ResponseExponent = num;
                if (TryJsonNumber(json, "YawSensitivity", out num)) s.YawSensitivity = num;
                if (TryJsonNumber(json, "PitchSensitivity", out num)) s.PitchSensitivity = num;
                if (TryJsonNumber(json, "RollSensitivity", out num)) s.RollSensitivity = num;
                if (TryJsonBool(json, "InvertYaw", out b)) s.InvertYaw = b;
                if (TryJsonBool(json, "InvertPitch", out b)) s.InvertPitch = b;
                if (TryJsonBool(json, "InvertRoll", out b)) s.InvertRoll = b;
                if (TryJsonBool(json, "QuaternionTransposeMode", out b)) s.QuaternionTransposeMode = b;
                if (TryJsonBool(json, "EnableNeckModel", out b)) s.EnableNeckModel = b;
                if (TryJsonNumber(json, "NeckModelIntensity", out num)) s.NeckModelIntensity = num;
                if (TryJsonBool(json, "EnableYawDriftGuard", out b)) s.EnableYawDriftGuard = b;
                if (TryJsonNumber(json, "YawDriftGuardStrength", out num)) s.YawDriftGuardStrength = num;
                if (TryJsonBool(json, "EnableAudioStream", out b)) s.EnableAudioStream = b;
                if (TryJsonString(json, "AudioDeviceId", out str)) s.AudioDeviceId = str;
                if (TryJsonNumber(json, "AudioGain", out num)) s.AudioGain = num;
                if (TryJsonBool(json, "EnableGlobalHotkey", out b)) s.EnableGlobalHotkey = b;
                if (TryJsonString(json, "SelectedAdapterIp", out str)) s.SelectedAdapterIp = str;
            });
        }

        private List<WsClient> GetClientsByRole(string role)
        {
            var list = new List<WsClient>();
            lock (_wsLock)
            {
                for (int i = 0; i < _wsClients.Count; i++)
                {
                    if (_wsClients[i].Alive && _wsClients[i].Role == role)
                    {
                        list.Add(_wsClients[i]);
                    }
                }
            }
            return list;
        }

        private static void SendWsFrame(WsClient client, byte opcode, byte[] data, int len)
        {
            if (!client.Alive) return;
            try
            {
                byte[] header;
                if (len <= 125)
                {
                    header = new byte[2];
                    header[0] = (byte)(0x80 | opcode);
                    header[1] = (byte)len;
                }
                else if (len <= 65535)
                {
                    header = new byte[4];
                    header[0] = (byte)(0x80 | opcode);
                    header[1] = 126;
                    header[2] = (byte)((len >> 8) & 0xFF);
                    header[3] = (byte)(len & 0xFF);
                }
                else
                {
                    header = new byte[10];
                    header[0] = (byte)(0x80 | opcode);
                    header[1] = 127;
                    long l = len;
                    for (int i = 0; i < 8; i++)
                        header[9 - i] = (byte)((l >> (i * 8)) & 0xFF);
                }

                lock (client.WriteLock)
                {
                    client.Stream.Write(header, 0, header.Length);
                    if (len > 0) client.Stream.Write(data, 0, len);
                }
            }
            catch
            {
                client.Alive = false;
            }
        }

        private static string ReadHttpHeader(NetworkStream stream)
        {
            var ms = new MemoryStream();
            byte[] b = new byte[1];
            int match = 0;
            while (ms.Length < 16384)
            {
                int n = stream.Read(b, 0, 1);
                if (n <= 0) break;
                ms.WriteByte(b[0]);
                if (b[0] == '\r' && (match == 0 || match == 2)) match++;
                else if (b[0] == '\n' && (match == 1 || match == 3)) match++;
                else match = (b[0] == '\r') ? 1 : 0;
                if (match == 4) break;
            }
            return Encoding.ASCII.GetString(ms.ToArray());
        }

        private static byte[] ReadExact(NetworkStream stream, int len)
        {
            byte[] buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = stream.Read(buf, read, len - read);
                if (n <= 0) return null;
                read += n;
            }
            return buf;
        }

        private static void SendHttpResponse(NetworkStream stream, string status, string contentType, byte[] body)
        {
            string header = string.Format(
                CultureInfo.InvariantCulture,
                "HTTP/1.1 {0}\r\nContent-Type: {1}\r\nContent-Length: {2}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n",
                status, contentType, body.Length);
            byte[] hBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(hBytes, 0, hBytes.Length);
            stream.Write(body, 0, body.Length);
        }

        private static string GetQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return "";
            string[] parts = query.Split('&');
            foreach (string p in parts)
            {
                int eq = p.IndexOf('=');
                if (eq > 0 && string.Equals(p.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(p.Substring(eq + 1));
                }
            }
            return "";
        }

        private static bool TryJsonString(string json, string key, out string val)
        {
            val = "";
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return false;
            val = m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            return true;
        }

        private static bool TryJsonNumber(string json, string key, out double val)
        {
            val = 0;
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
            if (!m.Success) return false;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        }

        private static bool TryJsonBool(string json, string key, out bool val)
        {
            val = false;
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            val = m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private static string B(bool v) { return v ? "true" : "false"; }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "");
        }

        private static void WriteUInt32(byte[] buf, int offset, uint val)
        {
            buf[offset] = (byte)(val & 0xFF);
            buf[offset + 1] = (byte)((val >> 8) & 0xFF);
            buf[offset + 2] = (byte)((val >> 16) & 0xFF);
            buf[offset + 3] = (byte)((val >> 24) & 0xFF);
        }

        private static void WriteUInt16(byte[] buf, int offset, ushort val)
        {
            buf[offset] = (byte)(val & 0xFF);
            buf[offset + 1] = (byte)((val >> 8) & 0xFF);
        }

        private static Thread StartBg(ThreadStart target)
        {
            var t = new Thread(target);
            t.IsBackground = true;
            t.Start();
            return t;
        }

        private static void TryDisableUdpConnReset(UdpClient client)
        {
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                client.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { }
        }
    }
}
