import Foundation
import simd

struct TrackingSample {
    let timestamp: TimeInterval
    let qw: Double
    let qx: Double
    let qy: Double
    let qz: Double
    let rx: Double
    let ry: Double
    let rz: Double
    let ax: Double
    let ay: Double
    let az: Double
    let cmYaw: Double
    let cmPitch: Double
    let cmRoll: Double
    let sensorLocation: Int32
}

struct DiscoveredHost: Identifiable, Hashable {
    let id: String
    let ip: String
    let httpPort: Int
    let udpPort: Int
}

enum BinaryPacketCodec {
    static let packetSize = 128

    static func encodeTracking(
        sample: TrackingSample?,
        sequence: UInt32,
        trackingActive: Bool,
        wantsAudio: Bool,
        recenterPulse: Bool
    ) -> Data {
        var data = Data(count: packetSize)
        data.withUnsafeMutableBytes { rawBuf in
            guard let base = rawBuf.baseAddress else { return }
            // Magic "ATPT"
            base.storeBytes(of: UInt8(ascii: "A"), toByteOffset: 0, as: UInt8.self)
            base.storeBytes(of: UInt8(ascii: "T"), toByteOffset: 1, as: UInt8.self)
            base.storeBytes(of: UInt8(ascii: "P"), toByteOffset: 2, as: UInt8.self)
            base.storeBytes(of: UInt8(ascii: "T"), toByteOffset: 3, as: UInt8.self)

            var flags: UInt32 = 0
            if trackingActive { flags |= 1 }
            if wantsAudio { flags |= 2 }
            if recenterPulse { flags |= 4 }
            base.storeBytes(of: flags.littleEndian, toByteOffset: 4, as: UInt32.self)
            base.storeBytes(of: sequence.littleEndian, toByteOffset: 8, as: UInt32.self)

            let sensorLoc = sample?.sensorLocation ?? 0
            base.storeBytes(of: sensorLoc.littleEndian, toByteOffset: 12, as: Int32.self)

            guard let s = sample else { return }
            writeDouble(s.timestamp, to: base, offset: 16)
            writeDouble(s.qw, to: base, offset: 24)
            writeDouble(s.qx, to: base, offset: 32)
            writeDouble(s.qy, to: base, offset: 40)
            writeDouble(s.qz, to: base, offset: 48)
            writeDouble(s.rx, to: base, offset: 56)
            writeDouble(s.ry, to: base, offset: 64)
            writeDouble(s.rz, to: base, offset: 72)
            writeDouble(s.ax, to: base, offset: 80)
            writeDouble(s.ay, to: base, offset: 88)
            writeDouble(s.az, to: base, offset: 96)
            writeDouble(s.cmYaw, to: base, offset: 104)
            writeDouble(s.cmPitch, to: base, offset: 112)
            writeDouble(s.cmRoll, to: base, offset: 120)
        }
        return data
    }

    private static func writeDouble(_ value: Double, to base: UnsafeMutableRawPointer, offset: Int) {
        let bits = value.bitPattern.littleEndian
        base.storeBytes(of: bits, toByteOffset: offset, as: UInt64.self)
    }
}

final class AppSettings: ObservableObject {
    @Published var pcHost: String {
        didSet { UserDefaults.standard.set(pcHost, forKey: "pcHost") }
    }
    @Published var httpPort: Int {
        didSet { UserDefaults.standard.set(httpPort, forKey: "httpPort") }
    }
    @Published var udpPort: Int {
        didSet { UserDefaults.standard.set(udpPort, forKey: "udpPort") }
    }
    @Published var wantsDesktopAudio: Bool {
        didSet { UserDefaults.standard.set(wantsDesktopAudio, forKey: "wantsDesktopAudio") }
    }
    @Published var keepAliveInBackground: Bool {
        didSet { UserDefaults.standard.set(keepAliveInBackground, forKey: "keepAliveInBackground") }
    }
    @Published var preventScreenSleep: Bool {
        didSet { UserDefaults.standard.set(preventScreenSleep, forKey: "preventScreenSleep") }
    }
    @Published var audioBufferMs: Double {
        didSet { UserDefaults.standard.set(audioBufferMs, forKey: "audioBufferMs") }
    }

    init() {
        let ud = UserDefaults.standard
        self.pcHost = ud.string(forKey: "pcHost") ?? ""
        let hp = ud.integer(forKey: "httpPort")
        self.httpPort = hp > 0 ? hp : 7878
        let up = ud.integer(forKey: "udpPort")
        self.udpPort = up > 0 ? up : 7879
        if ud.object(forKey: "wantsDesktopAudio") == nil {
            self.wantsDesktopAudio = true
        } else {
            self.wantsDesktopAudio = ud.bool(forKey: "wantsDesktopAudio")
        }
        if ud.object(forKey: "keepAliveInBackground") == nil {
            self.keepAliveInBackground = true
        } else {
            self.keepAliveInBackground = ud.bool(forKey: "keepAliveInBackground")
        }
        if ud.object(forKey: "preventScreenSleep") == nil {
            self.preventScreenSleep = true
        } else {
            self.preventScreenSleep = ud.bool(forKey: "preventScreenSleep")
        }
        let buf = ud.double(forKey: "audioBufferMs")
        self.audioBufferMs = buf >= 15 ? buf : 35
    }
}
