import Foundation
import Network

final class NetworkStreamService: ObservableObject {
    @Published var isConnected: Bool = false
    @Published var connectionStateLabel: String = "Disconnected"
    @Published var transportMode: String = "Idle"
    @Published var packetsSent: Int = 0

    var onAudioPacket: ((Data) -> Void)?
    var onRemoteRecenter: (() -> Void)?

    private let netQueue = DispatchQueue(label: "com.airtrackpro.net", qos: .userInteractive)
    private var udpConnection: NWConnection?
    private var wsTask: URLSessionWebSocketTask?
    private var wsSession: URLSession?
    private var keepaliveTimer: Timer?

    private var currentHost: String = ""
    private var currentHttpPort: Int = 7878
    private var currentUdpPort: Int = 7879
    private var wantsAudio: Bool = true

    private var sequence: UInt32 = 0
    private var pendingRecenterPulse: Bool = false
    private var udpReady: Bool = false
    private var wsReady: Bool = false
    private var lastSampleSentTime: Date = .distantPast
    private var isTrackingActive: Bool = false
    private var localPacketsSent: Int = 0
    private var lastPacketsUiUpdate: Date = .distantPast

    func connect(host: String, httpPort: Int, udpPort: Int, wantsAudio: Bool) {
        let trimmed = host.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            DispatchQueue.main.async {
                self.connectionStateLabel = "Enter PC IP or Scan QR"
            }
            return
        }

        disconnect()

        currentHost = trimmed
        currentHttpPort = httpPort
        currentUdpPort = udpPort
        self.wantsAudio = wantsAudio

        DispatchQueue.main.async {
            self.isConnected = false
            self.packetsSent = 0
            self.connectionStateLabel = "Connecting to \(trimmed)..."
        }
        netQueue.async {
            self.localPacketsSent = 0
            self.lastPacketsUiUpdate = .distantPast
        }

        startUdp(host: trimmed, port: UInt16(clamping: udpPort))
        startWebSocket(host: trimmed, port: httpPort)
        startKeepalive()
    }

    func disconnect() {
        keepaliveTimer?.invalidate()
        keepaliveTimer = nil

        udpReady = false
        wsReady = false

        udpConnection?.cancel()
        udpConnection = nil

        wsTask?.cancel(with: .goingAway, reason: nil)
        wsTask = nil
        wsSession?.invalidateAndCancel()
        wsSession = nil

        DispatchQueue.main.async {
            self.isConnected = false
            self.transportMode = "Idle"
            self.connectionStateLabel = "Disconnected"
        }
    }

    func setWantsAudio(_ wants: Bool) {
        wantsAudio = wants
        sendHeartbeatNow()
    }

    func requestRecenter() {
        netQueue.async {
            self.pendingRecenterPulse = true
        }
        sendHeartbeatNow()
    }

    func sendMotionSample(_ sample: TrackingSample) {
        netQueue.async {
            self.sequence &+= 1
            let pulse = self.pendingRecenterPulse
            self.pendingRecenterPulse = false
            self.isTrackingActive = true
            self.lastSampleSentTime = Date()

            let data = BinaryPacketCodec.encodeTracking(
                sample: sample,
                sequence: self.sequence,
                trackingActive: true,
                wantsAudio: self.wantsAudio,
                recenterPulse: pulse
            )
            self.sendBinaryPacket(data)
        }
    }

    func sendHeartbeatNow() {
        netQueue.async {
            self.sequence &+= 1
            let pulse = self.pendingRecenterPulse
            self.pendingRecenterPulse = false
            let active = self.isTrackingActive && Date().timeIntervalSince(self.lastSampleSentTime) < 1.2
            let data = BinaryPacketCodec.encodeTracking(
                sample: nil,
                sequence: self.sequence,
                trackingActive: active,
                wantsAudio: self.wantsAudio,
                recenterPulse: pulse
            )
            self.sendBinaryPacket(data)
        }
    }

    private func sendBinaryPacket(_ data: Data) {
        var didSend = false
        if udpReady, let conn = udpConnection {
            conn.send(content: data, completion: .idempotent)
            didSend = true
        } else if wsReady, let ws = wsTask {
            ws.send(.data(data)) { _ in }
            didSend = true
        }

        guard didSend else { return }
        localPacketsSent += 1
        let now = Date()
        if localPacketsSent <= 5 || now.timeIntervalSince(lastPacketsUiUpdate) >= 0.08 {
            lastPacketsUiUpdate = now
            let countSnapshot = localPacketsSent
            DispatchQueue.main.async {
                self.packetsSent = countSnapshot
            }
        }
    }

    private func startUdp(host: String, port: UInt16) {
        guard let nwPort = NWEndpoint.Port(rawValue: port) else { return }
        let params = NWParameters.udp
        params.serviceClass = .interactiveVoice
        let conn = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: params)
        self.udpConnection = conn

        conn.stateUpdateHandler = { [weak self] state in
            guard let self = self else { return }
            switch state {
            case .ready:
                self.udpReady = true
                self.updateConnectionBadge()
                self.receiveNextUdp(on: conn)
                self.sendHeartbeatNow()
            case .waiting(let err):
                self.udpReady = false
                self.updateConnectionBadge(errorNote: "Waiting: \(err.localizedDescription)")
            case .failed(let err):
                self.udpReady = false
                self.updateConnectionBadge(errorNote: err.localizedDescription)
            case .cancelled:
                self.udpReady = false
            default:
                break
            }
        }
        conn.start(queue: netQueue)
    }

    private func receiveNextUdp(on conn: NWConnection) {
        conn.receiveMessage { [weak self] data, _, _, error in
            guard let self = self else { return }
            if let data = data, data.count > 16 {
                self.onAudioPacket?(data)
            }
            if error == nil && conn.state == .ready {
                self.receiveNextUdp(on: conn)
            }
        }
    }

    private func startWebSocket(host: String, port: Int) {
        guard let url = URL(string: "ws://\(host):\(port)/ws?role=ios") else { return }
        let config = URLSessionConfiguration.default
        let session = URLSession(configuration: config)
        self.wsSession = session
        let task = session.webSocketTask(with: url)
        self.wsTask = task
        task.resume()

        // Mark WS ready once first ping succeeds
        task.sendPing { [weak self] error in
            guard let self = self else { return }
            if error == nil {
                self.wsReady = true
                self.updateConnectionBadge()
                self.receiveNextWs(task: task)
            } else {
                self.wsReady = false
                self.updateConnectionBadge(errorNote: error?.localizedDescription)
            }
        }
    }

    private func receiveNextWs(task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            guard let self = self else { return }
            switch result {
            case .success(let message):
                switch message {
                case .data(let data):
                    self.onAudioPacket?(data)
                case .string(let text):
                    if text.localizedCaseInsensitiveContains("recenter") {
                        self.onRemoteRecenter?()
                    }
                @unknown default:
                    break
                }
                self.receiveNextWs(task: task)
            case .failure:
                self.wsReady = false
                self.updateConnectionBadge()
            }
        }
    }

    private func startKeepalive() {
        DispatchQueue.main.async {
            self.keepaliveTimer?.invalidate()
            self.keepaliveTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
                guard let self = self else { return }
                if Date().timeIntervalSince(self.lastSampleSentTime) > 0.22 {
                    self.sendHeartbeatNow()
                }
            }
        }
    }

    private func updateConnectionBadge(errorNote: String? = nil) {
        DispatchQueue.main.async {
            let connected = self.udpReady || self.wsReady
            self.isConnected = connected
            if self.udpReady && self.wsReady {
                self.transportMode = "UDP + WS"
                self.connectionStateLabel = "Connected to \(self.currentHost)"
            } else if self.udpReady {
                self.transportMode = "UDP Direct"
                self.connectionStateLabel = "Connected (UDP) to \(self.currentHost)"
            } else if self.wsReady {
                self.transportMode = "WebSocket Fallback"
                self.connectionStateLabel = "Connected (WS) to \(self.currentHost)"
            } else if let note = errorNote {
                self.transportMode = "Offline"
                self.connectionStateLabel = "Error: \(note)"
            } else {
                self.transportMode = "Offline"
                self.connectionStateLabel = "Disconnected"
            }
        }
    }

    static func parsePairingString(_ raw: String) -> (ip: String, httpPort: Int, udpPort: Int)? {
        let s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.lowercased().hasPrefix("airtrack://") {
            let body = String(s.dropFirst("airtrack://".count))
            let parts = body.split(separator: ":")
            guard parts.count >= 1 else { return nil }
            let ip = String(parts[0])
            let httpP = parts.count >= 2 ? (Int(parts[1]) ?? 7878) : 7878
            let udpP = parts.count >= 3 ? (Int(parts[2]) ?? 7879) : 7879
            return (ip, httpP, udpP)
        }
        if s.split(separator: ".").count == 4 {
            return (s, 7878, 7879)
        }
        return nil
    }
}
