import SwiftUI

@main
struct AirTrackProApp: App {
    @StateObject private var settings: AppSettings
    @StateObject private var motion: HeadphoneMotionService
    @StateObject private var network: NetworkStreamService
    @StateObject private var audio: AudioPlayerService
    @StateObject private var scanner: LanScanner

    init() {
        let s = AppSettings()
        let m = HeadphoneMotionService()
        let n = NetworkStreamService()
        let a = AudioPlayerService()
        let l = LanScanner()

        m.onSample = { [weak n] sample in
            n?.sendMotionSample(sample)
        }
        n.onAudioPacket = { [weak a] data in
            a?.ingestAudioPacket(data)
        }
        n.onRemoteRecenter = { [weak m] in
            m?.recenterLocal()
        }

        _settings = StateObject(wrappedValue: s)
        _motion = StateObject(wrappedValue: m)
        _network = StateObject(wrappedValue: n)
        _audio = StateObject(wrappedValue: a)
        _scanner = StateObject(wrappedValue: l)
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(settings)
                .environmentObject(motion)
                .environmentObject(network)
                .environmentObject(audio)
                .environmentObject(scanner)
        }
    }
}
