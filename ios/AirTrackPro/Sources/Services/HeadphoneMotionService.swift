import Foundation
import CoreMotion
import simd

final class HeadphoneMotionService: NSObject, ObservableObject, CMHeadphoneMotionManagerDelegate {
    @Published var isAvailable: Bool = false
    @Published var isTracking: Bool = false
    @Published var isHeadphoneConnected: Bool = false
    @Published var sensorLocationLabel: String = "Waiting for AirPods"
    @Published var statusText: String = "Put AirPods Pro in ears"
    @Published var previewYaw: Double = 0.0
    @Published var previewPitch: Double = 0.0
    @Published var previewRoll: Double = 0.0
    @Published var sampleRateHz: Double = 0.0

    var onSample: ((TrackingSample) -> Void)?

    private let motionManager = CMHeadphoneMotionManager()
    private let motionQueue: OperationQueue = {
        let q = OperationQueue()
        q.name = "com.airtrackpro.headmotion"
        q.maxConcurrentOperationCount = 1
        q.qualityOfService = .userInteractive
        return q
    }()

    private let stateLock = NSLock()
    private var referenceQuat: simd_quatd?
    private var latestQuat: simd_quatd?
    private var lastSampleTimestamp: TimeInterval = 0
    private var lastUiUpdateTime: TimeInterval = 0
    private var emaHz: Double = 0
    private var watchdogTimer: Timer?
    private var lastWallClockReceived: Date = .distantPast

    override init() {
        super.init()
        motionManager.delegate = self
    }

    func refreshAvailability() {
        let avail = motionManager.isDeviceMotionAvailable
        DispatchQueue.main.async {
            self.isAvailable = avail
            if !avail {
                self.statusText = "No motion-capable AirPods detected"
            }
        }
    }

    func start() {
        let auth = CMHeadphoneMotionManager.authorizationStatus()
        if auth == .denied || auth == .restricted {
            DispatchQueue.main.async {
                self.isTracking = false
                self.statusText = "Motion access denied in iOS Settings"
            }
            return
        }

        refreshAvailability()
        guard motionManager.isDeviceMotionAvailable else {
            DispatchQueue.main.async {
                self.isTracking = false
                self.statusText = "Connect & wear AirPods Pro"
            }
            return
        }

        if motionManager.isDeviceMotionActive {
            return
        }

        motionManager.startDeviceMotionUpdates(to: motionQueue) { [weak self] motion, error in
            guard let self = self else { return }
            if let error = error {
                DispatchQueue.main.async {
                    self.statusText = "Motion error: \(error.localizedDescription)"
                }
                return
            }
            guard let motion = motion else { return }
            self.handleMotion(motion)
        }

        DispatchQueue.main.async {
            self.isTracking = true
            self.statusText = "Waiting for AirPods IMU..."
            self.startWatchdog()
        }
    }

    func stop() {
        if motionManager.isDeviceMotionActive {
            motionManager.stopDeviceMotionUpdates()
        }
        DispatchQueue.main.async {
            self.watchdogTimer?.invalidate()
            self.watchdogTimer = nil
            self.isTracking = false
            self.isHeadphoneConnected = false
            self.sampleRateHz = 0
            self.statusText = "Tracking paused"
        }
    }

    func recenterLocal() {
        stateLock.lock()
        if let q = latestQuat {
            referenceQuat = q
        } else {
            referenceQuat = nil
        }
        stateLock.unlock()

        DispatchQueue.main.async {
            self.previewYaw = 0
            self.previewPitch = 0
            self.previewRoll = 0
        }
    }

    private func startWatchdog() {
        watchdogTimer?.invalidate()
        watchdogTimer = Timer.scheduledTimer(withTimeInterval: 0.8, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            let elapsed = Date().timeIntervalSince(self.lastWallClockReceived)
            if self.isTracking && elapsed > 1.5 && self.lastWallClockReceived != .distantPast {
                self.sampleRateHz = 0
                self.isHeadphoneConnected = false
                self.statusText = "AirPods paused (out of ear?)"
            }
        }
    }

    private func handleMotion(_ motion: CMDeviceMotion) {
        let qRaw = motion.attitude.quaternion
        let q = simd_normalize(simd_quatd(ix: qRaw.x, iy: qRaw.y, iz: qRaw.z, r: qRaw.w))

        var sensorLoc: Int32 = 0
        var locStr = "AirPods IMU"
        switch motion.sensorLocation {
        case .headphoneLeft:
            sensorLoc = 1
            locStr = "Left AirPod"
        case .headphoneRight:
            sensorLoc = 2
            locStr = "Right AirPod"
        case .default:
            sensorLoc = 0
            locStr = "Headphones"
        @unknown default:
            sensorLoc = 0
        }

        let sample = TrackingSample(
            timestamp: motion.timestamp,
            qw: q.real,
            qx: q.imag.x,
            qy: q.imag.y,
            qz: q.imag.z,
            rx: motion.rotationRate.x,
            ry: motion.rotationRate.y,
            rz: motion.rotationRate.z,
            ax: motion.userAcceleration.x,
            ay: motion.userAcceleration.y,
            az: motion.userAcceleration.z,
            cmYaw: motion.attitude.yaw,
            cmPitch: motion.attitude.pitch,
            cmRoll: motion.attitude.roll,
            sensorLocation: sensorLoc
        )

        onSample?(sample)

        stateLock.lock()
        latestQuat = q
        if referenceQuat == nil {
            referenceQuat = q
        }
        let ref = referenceQuat ?? q
        let dt = lastSampleTimestamp > 0 ? (motion.timestamp - lastSampleTimestamp) : 0
        lastSampleTimestamp = motion.timestamp
        if dt > 0.002 && dt < 0.5 {
            let hz = 1.0 / dt
            emaHz = emaHz <= 0.1 ? hz : (emaHz * 0.85 + hz * 0.15)
        }
        let shouldUi = (motion.timestamp - lastUiUpdateTime) >= 0.05
        if shouldUi {
            lastUiUpdateTime = motion.timestamp
        }
        let hzSnapshot = emaHz
        stateLock.unlock()

        if shouldUi {
            let qRel = simd_normalize(ref.inverse * q)
            let (y, p, r) = extractHeadEulerDegrees(qRel)
            DispatchQueue.main.async {
                self.lastWallClockReceived = Date()
                self.isHeadphoneConnected = true
                self.sensorLocationLabel = locStr
                self.statusText = "Tracking active"
                self.sampleRateHz = hzSnapshot
                self.previewYaw = y
                self.previewPitch = p
                self.previewRoll = r
            }
        }
    }

    private func extractHeadEulerDegrees(_ q: simd_quatd) -> (Double, Double, Double) {
        let w = q.real
        let x = q.imag.x
        let y = q.imag.y
        let z = q.imag.z

        let r01 = 2.0 * (x * y - w * z)
        let r11 = 1.0 - 2.0 * (x * x + z * z)
        let r20 = 2.0 * (x * z - w * y)
        let r21 = 2.0 * (y * z + w * x)
        let r22 = 1.0 - 2.0 * (x * x + y * y)

        let radToDeg = 180.0 / Double.pi
        let pitch = atan2(r21, sqrt(r20 * r20 + r22 * r22)) * radToDeg
        let yaw = atan2(r01, r11) * radToDeg
        let roll = atan2(-r20, r22) * radToDeg
        return (yaw, pitch, roll)
    }

    // MARK: - CMHeadphoneMotionManagerDelegate
    func headphoneMotionManagerDidConnect(_ manager: CMHeadphoneMotionManager) {
        DispatchQueue.main.async {
            self.isAvailable = true
            self.isHeadphoneConnected = true
            self.statusText = "AirPods Pro connected"
            if !self.isTracking {
                self.start()
            }
        }
    }

    func headphoneMotionManagerDidDisconnect(_ manager: CMHeadphoneMotionManager) {
        DispatchQueue.main.async {
            self.isHeadphoneConnected = false
            self.sampleRateHz = 0
            self.statusText = "AirPods disconnected"
        }
    }
}
