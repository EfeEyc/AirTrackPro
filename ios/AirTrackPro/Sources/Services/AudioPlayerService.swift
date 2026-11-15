import Foundation
import AVFoundation
import os

private final class StereoFloatRingBuffer {
    private let capacity: Int
    private var leftBuf: [Float]
    private var rightBuf: [Float]
    private var readIndex: Int = 0
    private var writeIndex: Int = 0
    private var count: Int = 0
    private var primed: Bool = false
    private let lockPtr: UnsafeMutablePointer<os_unfair_lock>

    var targetLatencyFrames: Int = 1680 // ~35ms at 48kHz

    init(capacity: Int = 48000) {
        self.capacity = capacity
        self.leftBuf = [Float](repeating: 0, count: capacity)
        self.rightBuf = [Float](repeating: 0, count: capacity)
        self.lockPtr = UnsafeMutablePointer<os_unfair_lock>.allocate(capacity: 1)
        self.lockPtr.initialize(to: os_unfair_lock())
    }

    deinit {
        lockPtr.deinitialize(count: 1)
        lockPtr.deallocate()
    }

    func reset() {
        os_unfair_lock_lock(lockPtr)
        readIndex = 0
        writeIndex = 0
        count = 0
        primed = false
        os_unfair_lock_unlock(lockPtr)
    }

    func writeInterleavedInt16(data: Data, offset: Int, frameCount: Int, channels: Int) {
        guard frameCount > 0, channels >= 1 else { return }
        os_unfair_lock_lock(lockPtr)
        let maxAllowed = min(capacity, max(targetLatencyFrames * 2 + 1800, 4800))

        data.withUnsafeBytes { raw in
            let scale: Float = 1.0 / 32768.0
            let strideBytes = channels * 2
            for f in 0..<frameCount {
                let b0 = offset + f * strideBytes
                guard b0 + 1 < raw.count else { break }
                let u0 = UInt16(raw[b0]) | (UInt16(raw[b0 + 1]) << 8)
                let s0 = Int16(bitPattern: u0)
                let s1: Int16
                if channels >= 2 && b0 + 3 < raw.count {
                    let u1 = UInt16(raw[b0 + 2]) | (UInt16(raw[b0 + 3]) << 8)
                    s1 = Int16(bitPattern: u1)
                } else {
                    s1 = s0
                }
                leftBuf[writeIndex] = Float(s0) * scale
                rightBuf[writeIndex] = Float(s1) * scale
                writeIndex = (writeIndex + 1) % capacity
                if count < capacity {
                    count += 1
                } else {
                    readIndex = (readIndex + 1) % capacity
                }
            }
        }

        if count > maxAllowed {
            let trim = count - targetLatencyFrames
            if trim > 0 {
                readIndex = (readIndex + trim) % capacity
                count -= trim
            }
        }
        os_unfair_lock_unlock(lockPtr)
    }

    func read(leftOut: UnsafeMutablePointer<Float>, rightOut: UnsafeMutablePointer<Float>?, frameCount: Int) -> Int {
        os_unfair_lock_lock(lockPtr)
        if !primed {
            if count >= targetLatencyFrames {
                primed = true
            } else {
                os_unfair_lock_unlock(lockPtr)
                for i in 0..<frameCount {
                    leftOut[i] = 0
                    rightOut?[i] = 0
                }
                return 0
            }
        }

        let toCopy = min(frameCount, count)
        for i in 0..<toCopy {
            leftOut[i] = leftBuf[readIndex]
            rightOut?[i] = rightBuf[readIndex]
            readIndex = (readIndex + 1) % capacity
        }
        count -= toCopy

        if toCopy < frameCount {
            let lastL = toCopy > 0 ? leftOut[toCopy - 1] : 0
            let lastR = (toCopy > 0 && rightOut != nil) ? rightOut![toCopy - 1] : 0
            let rem = frameCount - toCopy
            let fadeSteps = min(rem, 32)
            for i in 0..<rem {
                let fade = i < fadeSteps ? (1.0 - Float(i + 1) / Float(fadeSteps)) : 0
                leftOut[toCopy + i] = lastL * fade
                rightOut?[toCopy + i] = lastR * fade
            }
            primed = false
        }

        let remaining = count
        os_unfair_lock_unlock(lockPtr)
        return remaining
    }

    var bufferedFrames: Int {
        os_unfair_lock_lock(lockPtr)
        let c = count
        os_unfair_lock_unlock(lockPtr)
        return c
    }
}

final class AudioPlayerService: ObservableObject {
    @Published var isEngineRunning: Bool = false
    @Published var sampleRate: Int = 48000
    @Published var bufferFillMs: Double = 0.0
    @Published var packetsReceived: Int = 0
    @Published var audioStatus: String = "Idle"

    private let engine = AVAudioEngine()
    private var sourceNode: AVAudioSourceNode?
    private let ringBuffer = StereoFloatRingBuffer()
    private let controlQueue = DispatchQueue(label: "com.airtrackpro.audioctrl")

    private var streamEnabled: Bool = true
    private var keepAliveEnabled: Bool = true
    private var activeFormatRate: Int = 48000
    private var lastPacketDate: Date = .distantPast
    private var uiTimer: Timer?

    init() {
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(handleInterruption(_:)),
            name: AVAudioSession.interruptionNotification,
            object: nil
        )
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(handleRouteChange(_:)),
            name: AVAudioSession.routeChangeNotification,
            object: nil
        )
        DispatchQueue.main.async {
            self.uiTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
                self?.refreshMetrics()
            }
        }
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
        uiTimer?.invalidate()
        stop()
    }

    func configure(streamEnabled: Bool, keepAliveEnabled: Bool, bufferMs: Double) {
        controlQueue.async {
            self.streamEnabled = streamEnabled
            self.keepAliveEnabled = keepAliveEnabled
            let frames = Int(Double(self.activeFormatRate) * max(15.0, min(120.0, bufferMs)) / 1000.0)
            self.ringBuffer.targetLatencyFrames = frames

            if streamEnabled || keepAliveEnabled {
                self.startEngineIfNeeded(rate: self.activeFormatRate)
            } else {
                self.stopEngineInternal()
            }
        }
    }

    func stop() {
        controlQueue.async {
            self.stopEngineInternal()
        }
    }

    func ingestAudioPacket(_ data: Data) {
        guard data.count > 16 else { return }
        let magicOk = data.withUnsafeBytes { raw -> Bool in
            guard let b = raw.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return false }
            return b[0] == UInt8(ascii: "A") && b[1] == UInt8(ascii: "T") &&
                   b[2] == UInt8(ascii: "P") && b[3] == UInt8(ascii: "A")
        }
        guard magicOk else { return }

        let pktRate = data.withUnsafeBytes { raw -> UInt32 in
            UInt32(raw[8]) | (UInt32(raw[9]) << 8) | (UInt32(raw[10]) << 16) | (UInt32(raw[11]) << 24)
        }
        let channels = data.withUnsafeBytes { raw -> UInt16 in
            UInt16(raw[12]) | (UInt16(raw[13]) << 8)
        }
        let bits = data.withUnsafeBytes { raw -> UInt16 in
            UInt16(raw[14]) | (UInt16(raw[15]) << 8)
        }

        guard bits == 16, channels >= 1 else { return }
        let rateInt = Int(pktRate)
        guard rateInt >= 8000, rateInt <= 192000 else { return }

        let payloadBytes = data.count - 16
        let frameSize = Int(channels) * 2
        let frameCount = payloadBytes / frameSize
        guard frameCount > 0 else { return }

        if rateInt != activeFormatRate {
            controlQueue.async {
                self.activeFormatRate = rateInt
                self.ringBuffer.reset()
                if self.streamEnabled || self.keepAliveEnabled {
                    self.rebuildAndStart(rate: rateInt)
                }
            }
        }

        if streamEnabled {
            ringBuffer.writeInterleavedInt16(data: data, offset: 16, frameCount: frameCount, channels: Int(channels))
        }

        DispatchQueue.main.async {
            self.lastPacketDate = Date()
            self.packetsReceived += 1
            if self.sampleRate != rateInt {
                self.sampleRate = rateInt
            }
        }
    }

    private func startEngineIfNeeded(rate: Int) {
        if engine.isRunning && sourceNode != nil && activeFormatRate == rate {
            return
        }
        rebuildAndStart(rate: rate)
    }

    private func rebuildAndStart(rate: Int) {
        stopEngineInternal()

        do {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(.playback, mode: .default, options: [])
            try session.setPreferredIOBufferDuration(0.005)
            try session.setActive(true)
        } catch {
            DispatchQueue.main.async {
                self.audioStatus = "AudioSession error: \(error.localizedDescription)"
            }
            return
        }

        guard let format = AVAudioFormat(standardFormatWithSampleRate: Double(rate), channels: 2) else { return }
        let rb = self.ringBuffer

        let node = AVAudioSourceNode(format: format) { _, _, frameCount, audioBufferList -> OSStatus in
            let abl = UnsafeMutableAudioBufferListPointer(audioBufferList)
            let n = Int(frameCount)
            if abl.count >= 2,
               let mL = abl[0].mData?.assumingMemoryBound(to: Float.self),
               let mR = abl[1].mData?.assumingMemoryBound(to: Float.self) {
                _ = rb.read(leftOut: mL, rightOut: mR, frameCount: n)
            } else if abl.count == 1,
                      let mL = abl[0].mData?.assumingMemoryBound(to: Float.self) {
                _ = rb.read(leftOut: mL, rightOut: nil, frameCount: n)
            }
            return noErr
        }

        self.sourceNode = node
        engine.attach(node)
        engine.connect(node, to: engine.mainMixerNode, format: format)
        engine.connect(engine.mainMixerNode, to: engine.outputNode, format: nil)

        do {
            engine.prepare()
            try engine.start()
            DispatchQueue.main.async {
                self.isEngineRunning = true
                self.audioStatus = self.streamEnabled ? "Ready for PC audio" : "Silent background keepalive"
            }
        } catch {
            DispatchQueue.main.async {
                self.isEngineRunning = false
                self.audioStatus = "Engine start failed: \(error.localizedDescription)"
            }
        }
    }

    private func stopEngineInternal() {
        if engine.isRunning {
            engine.stop()
        }
        if let node = sourceNode {
            engine.detach(node)
            sourceNode = nil
        }
        ringBuffer.reset()
        DispatchQueue.main.async {
            self.isEngineRunning = false
            self.audioStatus = "Idle"
        }
    }

    private func refreshMetrics() {
        let frames = ringBuffer.bufferedFrames
        let ms = Double(frames) * 1000.0 / Double(max(8000, sampleRate))
        bufferFillMs = ms
        if !isEngineRunning {
            audioStatus = "Off"
        } else if !streamEnabled {
            audioStatus = keepAliveEnabled ? "Background keepalive" : "Muted"
        } else if Date().timeIntervalSince(lastPacketDate) < 1.0 {
            audioStatus = "Streaming \(sampleRate / 1000)kHz"
        } else {
            audioStatus = "Waiting for PC audio"
        }
    }

    @objc private func handleInterruption(_ notification: Notification) {
        guard let info = notification.userInfo,
              let typeVal = info[AVAudioSessionInterruptionTypeKey] as? UInt,
              let type = AVAudioSession.InterruptionType(rawValue: typeVal) else { return }
        if type == .ended {
            controlQueue.async {
                if self.streamEnabled || self.keepAliveEnabled {
                    self.rebuildAndStart(rate: self.activeFormatRate)
                }
            }
        }
    }

    @objc private func handleRouteChange(_ notification: Notification) {
        controlQueue.async {
            if (self.streamEnabled || self.keepAliveEnabled) && !self.engine.isRunning {
                self.rebuildAndStart(rate: self.activeFormatRate)
            }
        }
    }
}
