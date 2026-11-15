import SwiftUI
import UIKit

struct ContentView: View {
    @EnvironmentObject var settings: AppSettings
    @EnvironmentObject var motion: HeadphoneMotionService
    @EnvironmentObject var network: NetworkStreamService
    @EnvironmentObject var audio: AudioPlayerService
    @EnvironmentObject var scanner: LanScanner

    @State private var showQrScanner = false
    @State private var hostInput: String = ""

    private let bgColor = Color(red: 0.03, green: 0.05, blue: 0.09)
    private let cardColor = Color(red: 0.07, green: 0.10, blue: 0.16)
    private let borderColor = Color(red: 0.15, green: 0.21, blue: 0.32)

    var body: some View {
        NavigationView {
            ScrollView {
                VStack(spacing: 16) {
                    statusHeader
                    attitudeCard
                    recenterButton
                    connectionCard
                    audioAndBackgroundCard
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 12)
            }
            .background(bgColor.ignoresSafeArea())
            .navigationTitle("AirTrack Pro")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button(action: { showQrScanner = true }) {
                        Image(systemName: "qrcode.viewfinder")
                            .foregroundColor(.cyan)
                    }
                }
            }
        }
        .navigationViewStyle(.stack)
        .preferredColorScheme(.dark)
        .sheet(isPresented: $showQrScanner) {
            QRScannerSheet { raw in
                if let parsed = NetworkStreamService.parsePairingString(raw) {
                    hostInput = parsed.ip
                    settings.pcHost = parsed.ip
                    settings.httpPort = parsed.httpPort
                    settings.udpPort = parsed.udpPort
                    network.connect(
                        host: parsed.ip,
                        httpPort: parsed.httpPort,
                        udpPort: parsed.udpPort,
                        wantsAudio: settings.wantsDesktopAudio
                    )
                }
            }
        }
        .onAppear {
            hostInput = settings.pcHost
            UIApplication.shared.isIdleTimerDisabled = settings.preventScreenSleep
            audio.configure(
                streamEnabled: settings.wantsDesktopAudio,
                keepAliveEnabled: settings.keepAliveInBackground,
                bufferMs: settings.audioBufferMs
            )
            motion.start()
            if !settings.pcHost.isEmpty && !network.isConnected {
                network.connect(
                    host: settings.pcHost,
                    httpPort: settings.httpPort,
                    udpPort: settings.udpPort,
                    wantsAudio: settings.wantsDesktopAudio
                )
            }
        }
        .onChange(of: settings.preventScreenSleep) { newVal in
            UIApplication.shared.isIdleTimerDisabled = newVal
        }
        .onChange(of: settings.wantsDesktopAudio) { newVal in
            network.setWantsAudio(newVal)
            audio.configure(
                streamEnabled: newVal,
                keepAliveEnabled: settings.keepAliveInBackground,
                bufferMs: settings.audioBufferMs
            )
        }
        .onChange(of: settings.keepAliveInBackground) { newVal in
            audio.configure(
                streamEnabled: settings.wantsDesktopAudio,
                keepAliveEnabled: newVal,
                bufferMs: settings.audioBufferMs
            )
        }
        .onChange(of: settings.audioBufferMs) { newVal in
            audio.configure(
                streamEnabled: settings.wantsDesktopAudio,
                keepAliveEnabled: settings.keepAliveInBackground,
                bufferMs: newVal
            )
        }
    }

    private var statusHeader: some View {
        HStack(spacing: 10) {
            StatusBadge(
                title: motion.isHeadphoneConnected ? motion.sensorLocationLabel : "AirPods Pro",
                subtitle: motion.isHeadphoneConnected ? String(format: "%.1f Hz", motion.sampleRateHz) : motion.statusText,
                isOk: motion.isHeadphoneConnected,
                icon: "airpodspro"
            )
            StatusBadge(
                title: network.isConnected ? network.transportMode : "PC Bridge",
                subtitle: network.isConnected ? settings.pcHost : "Not connected",
                isOk: network.isConnected,
                icon: "desktopcomputer"
            )
        }
    }

    private var attitudeCard: some View {
        VStack(spacing: 14) {
            HStack {
                Text("HEAD ORIENTATION")
                    .font(.system(size: 12, weight: .bold))
                    .foregroundColor(.cyan)
                Spacer()
                Text("Packets Sent: \(network.packetsSent)")
                    .font(.system(size: 12, weight: .medium, design: .monospaced))
                    .foregroundColor(.gray)
            }

            HeadHorizonPreview(
                yaw: motion.previewYaw,
                pitch: motion.previewPitch,
                roll: motion.previewRoll
            )
            .frame(height: 118)

            HStack(spacing: 10) {
                AxisMetricBox(label: "YAW", value: motion.previewYaw, accent: .cyan)
                AxisMetricBox(label: "PITCH", value: motion.previewPitch, accent: .green)
                AxisMetricBox(label: "ROLL", value: motion.previewRoll, accent: .orange)
            }
        }
        .padding(16)
        .background(cardColor)
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(borderColor, lineWidth: 1))
        .cornerRadius(16)
    }

    private var recenterButton: some View {
        Button(action: {
            UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
            motion.recenterLocal()
            network.requestRecenter()
        }) {
            HStack(spacing: 10) {
                Image(systemName: "scope")
                    .font(.system(size: 20, weight: .bold))
                Text("RECENTER HEAD")
                    .font(.system(size: 17, weight: .bold))
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 16)
            .background(
                LinearGradient(
                    colors: [Color(red: 0.02, green: 0.52, blue: 0.85), Color(red: 0.14, green: 0.38, blue: 0.92)],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                )
            )
            .foregroundColor(.white)
            .cornerRadius(14)
        }
    }

    private var connectionCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("PC WEB BRIDGE CONNECTION")
                    .font(.system(size: 12, weight: .bold))
                    .foregroundColor(.cyan)
                Spacer()
                Text(network.connectionStateLabel)
                    .font(.caption)
                    .foregroundColor(network.isConnected ? .green : .gray)
                    .lineLimit(1)
            }

            HStack(spacing: 8) {
                TextField("PC IP (e.g. 192.168.1.42)", text: $hostInput)
                    .keyboardType(.numbersAndPunctuation)
                    .autocapitalization(.none)
                    .disableAutocorrection(true)
                    .padding(10)
                    .background(bgColor)
                    .overlay(RoundedRectangle(cornerRadius: 10).stroke(borderColor, lineWidth: 1))
                    .cornerRadius(10)

                Button(action: {
                    if network.isConnected {
                        network.disconnect()
                    } else {
                        settings.pcHost = hostInput.trimmingCharacters(in: .whitespacesAndNewlines)
                        network.connect(
                            host: settings.pcHost,
                            httpPort: settings.httpPort,
                            udpPort: settings.udpPort,
                            wantsAudio: settings.wantsDesktopAudio
                        )
                    }
                }) {
                    Text(network.isConnected ? "Stop" : "Connect")
                        .font(.system(size: 14, weight: .bold))
                        .padding(.horizontal, 16)
                        .padding(.vertical, 11)
                        .background(network.isConnected ? Color.red.opacity(0.85) : Color.cyan.opacity(0.85))
                        .foregroundColor(.black)
                        .cornerRadius(10)
                }
            }

            HStack(spacing: 10) {
                Button(action: {
                    scanner.scan(httpPort: settings.httpPort, udpPort: settings.udpPort)
                }) {
                    HStack {
                        if scanner.isScanning {
                            ProgressView().progressViewStyle(CircularProgressViewStyle(tint: .white))
                        } else {
                            Image(systemName: "wifi")
                        }
                        Text(scanner.isScanning ? "Scanning..." : "Auto-Discover PC")
                    }
                    .font(.system(size: 13, weight: .semibold))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 10)
                    .background(Color.white.opacity(0.08))
                    .foregroundColor(.white)
                    .cornerRadius(10)
                }

                Button(action: { showQrScanner = true }) {
                    HStack {
                        Image(systemName: "qrcode.viewfinder")
                        Text("Scan QR")
                    }
                    .font(.system(size: 13, weight: .semibold))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 10)
                    .background(Color.white.opacity(0.08))
                    .foregroundColor(.white)
                    .cornerRadius(10)
                }
            }

            if !scanner.scanNote.isEmpty {
                Text(scanner.scanNote)
                    .font(.caption)
                    .foregroundColor(.gray)
            }

            ForEach(scanner.discoveredHosts) { host in
                Button(action: {
                    hostInput = host.ip
                    settings.pcHost = host.ip
                    settings.httpPort = host.httpPort
                    settings.udpPort = host.udpPort
                    network.connect(
                        host: host.ip,
                        httpPort: host.httpPort,
                        udpPort: host.udpPort,
                        wantsAudio: settings.wantsDesktopAudio
                    )
                }) {
                    HStack {
                        Image(systemName: "checkmark.circle.fill")
                            .foregroundColor(.green)
                        Text("Tap to pair: \(host.ip)")
                            .font(.subheadline)
                            .foregroundColor(.white)
                        Spacer()
                        Image(systemName: "arrow.right")
                            .foregroundColor(.cyan)
                    }
                    .padding(10)
                    .background(bgColor)
                    .cornerRadius(10)
                }
            }
        }
        .padding(16)
        .background(cardColor)
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(borderColor, lineWidth: 1))
        .cornerRadius(16)
    }

    private var audioAndBackgroundCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("PC DESKTOP AUDIO & BACKGROUND")
                    .font(.system(size: 12, weight: .bold))
                    .foregroundColor(.cyan)
                Spacer()
                Text("\(audio.audioStatus) • \(Int(audio.bufferFillMs)) ms")
                    .font(.caption)
                    .foregroundColor(.gray)
            }

            Toggle("Stream PC Desktop Audio to AirPods", isOn: $settings.wantsDesktopAudio)
                .tint(.cyan)

            Toggle("Keep Head Tracking Alive in Background", isOn: $settings.keepAliveInBackground)
                .tint(.cyan)

            Toggle("Keep iPhone Screen Awake", isOn: $settings.preventScreenSleep)
                .tint(.cyan)

            if settings.wantsDesktopAudio {
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text("Audio Jitter Buffer")
                            .font(.subheadline)
                        Spacer()
                        Text("\(Int(settings.audioBufferMs)) ms")
                            .font(.system(size: 13, weight: .bold, design: .monospaced))
                            .foregroundColor(.cyan)
                    }
                    Slider(value: $settings.audioBufferMs, in: 15...100, step: 5)
                        .accentColor(.cyan)
                }
            }

            HStack(spacing: 12) {
                Button(action: {
                    if motion.isTracking {
                        motion.stop()
                    } else {
                        motion.start()
                    }
                }) {
                    Text(motion.isTracking ? "Pause Sensors" : "Resume Sensors")
                        .font(.system(size: 13, weight: .semibold))
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                        .background(Color.white.opacity(0.08))
                        .foregroundColor(.white)
                        .cornerRadius(10)
                }

                if !settings.pcHost.isEmpty,
                   let webUrl = URL(string: "http://\(settings.pcHost):\(settings.httpPort)") {
                    Link(destination: webUrl) {
                        HStack {
                            Image(systemName: "safari")
                            Text("Open Web UI")
                        }
                        .font(.system(size: 13, weight: .semibold))
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                        .background(Color.white.opacity(0.08))
                        .foregroundColor(.cyan)
                        .cornerRadius(10)
                    }
                }
            }
        }
        .padding(16)
        .background(cardColor)
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(borderColor, lineWidth: 1))
        .cornerRadius(16)
    }
}

private struct StatusBadge: View {
    let title: String
    let subtitle: String
    let isOk: Bool
    let icon: String

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: icon)
                .font(.system(size: 18))
                .foregroundColor(isOk ? .green : .orange)
                .frame(width: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.system(size: 13, weight: .bold))
                    .foregroundColor(.white)
                    .lineLimit(1)
                Text(subtitle)
                    .font(.system(size: 11))
                    .foregroundColor(.gray)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
        }
        .padding(10)
        .frame(maxWidth: .infinity)
        .background(Color(red: 0.07, green: 0.10, blue: 0.16))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .stroke(isOk ? Color.green.opacity(0.45) : Color(red: 0.15, green: 0.21, blue: 0.32), lineWidth: 1)
        )
        .cornerRadius(12)
    }
}

private struct AxisMetricBox: View {
    let label: String
    let value: Double
    let accent: Color

    var body: some View {
        VStack(spacing: 4) {
            Text(label)
                .font(.system(size: 10, weight: .bold))
                .foregroundColor(.gray)
            Text(String(format: "%+.1f°", value))
                .font(.system(size: 18, weight: .bold, design: .monospaced))
                .foregroundColor(accent)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 10)
        .background(Color(red: 0.04, green: 0.06, blue: 0.10))
        .cornerRadius(10)
    }
}

private struct HeadHorizonPreview: View {
    let yaw: Double
    let pitch: Double
    let roll: Double

    var body: some View {
        GeometryReader { geo in
            let w = geo.size.width
            let h = geo.size.height
            let xOff = CGFloat(max(-1.0, min(1.0, yaw / 60.0))) * (w * 0.35)
            let yOff = CGFloat(max(-1.0, min(1.0, -pitch / 45.0))) * (h * 0.35)

            ZStack {
                RoundedRectangle(cornerRadius: 12)
                    .fill(Color(red: 0.03, green: 0.05, blue: 0.08))

                // Horizon line
                Rectangle()
                    .fill(Color.cyan.opacity(0.35))
                    .frame(width: w * 1.4, height: 1.5)
                    .offset(y: yOff)
                    .rotationEffect(.degrees(roll))

                // Center reticle
                Circle()
                    .stroke(Color.white.opacity(0.18), lineWidth: 1)
                    .frame(width: 44, height: 44)

                // Moving head indicator dot
                Circle()
                    .fill(Color.cyan)
                    .frame(width: 14, height: 14)
                    .shadow(color: .cyan, radius: 6)
                    .offset(x: xOff, y: yOff)
            }
            .clipped()
            .cornerRadius(12)
        }
    }
}
