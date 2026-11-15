import Foundation
import Darwin

final class LanScanner: ObservableObject {
    @Published var isScanning: Bool = false
    @Published var discoveredHosts: [DiscoveredHost] = []
    @Published var scanNote: String = ""

    func scan(httpPort: Int, udpPort: Int) {
        guard !isScanning else { return }
        guard let localIp = getWifiIPv4Address() else {
            DispatchQueue.main.async {
                self.scanNote = "Wi-Fi IPv4 not detected. Enter PC IP or scan QR."
            }
            return
        }

        let octets = localIp.split(separator: ".")
        guard octets.count == 4 else { return }
        let prefix = "\(octets[0]).\(octets[1]).\(octets[2])"
        let myHost = Int(octets[3]) ?? -1

        DispatchQueue.main.async {
            self.isScanning = true
            self.discoveredHosts = []
            self.scanNote = "Scanning \(prefix).1–254..."
        }

        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 0.55
        cfg.timeoutIntervalForResource = 0.75
        let session = URLSession(configuration: cfg)

        let group = DispatchGroup()
        let foundLock = NSLock()
        var found: [DiscoveredHost] = []

        let queue = DispatchQueue(label: "com.airtrackpro.lanscan", attributes: .concurrent)
        let semaphore = DispatchSemaphore(value: 32)

        DispatchQueue.global(qos: .userInitiated).async {
            for host in 1...254 {
                if host == myHost { continue }
                let ip = "\(prefix).\(host)"
                guard let url = URL(string: "http://\(ip):\(httpPort)/api/status") else { continue }

                semaphore.wait()
                group.enter()
                queue.async {
                    let task = session.dataTask(with: url) { data, response, _ in
                        defer {
                            semaphore.signal()
                            group.leave()
                        }
                        guard let http = response as? HTTPURLResponse, http.statusCode == 200,
                              let data = data,
                              let text = String(data: data, encoding: .utf8),
                              text.contains("\"telemetry\"") else {
                            return
                        }
                        foundLock.lock()
                        found.append(DiscoveredHost(id: ip, ip: ip, httpPort: httpPort, udpPort: udpPort))
                        foundLock.unlock()
                    }
                    task.resume()
                }
            }

            group.wait()
            session.invalidateAndCancel()

            DispatchQueue.main.async {
                self.isScanning = false
                self.discoveredHosts = found.sorted { $0.ip < $1.ip }
                self.scanNote = found.isEmpty
                    ? "No PC found on \(prefix).* (check Windows Firewall or scan QR)"
                    : "Found \(found.count) PC(s)"
            }
        }
    }

    private func getWifiIPv4Address() -> String? {
        var address: String?
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let firstAddr = ifaddr else { return nil }
        defer { freeifaddrs(ifaddr) }

        for ptr in sequence(first: firstAddr, next: { $0.pointee.ifa_next }) {
            let interface = ptr.pointee
            let addrFamily = interface.ifa_addr.pointee.sa_family
            if addrFamily == UInt8(AF_INET) {
                let name = String(cString: interface.ifa_name)
                if name == "en0" || name == "en1" {
                    var hostname = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                    let result = getnameinfo(
                        interface.ifa_addr,
                        socklen_t(interface.ifa_addr.pointee.sa_len),
                        &hostname,
                        socklen_t(hostname.count),
                        nil,
                        socklen_t(0),
                        NI_NUMERICHOST
                    )
                    if result == 0 {
                        let ip = String(cString: hostname)
                        if !ip.hasPrefix("169.254.") {
                            address = ip
                            if name == "en0" { break }
                        }
                    }
                }
            }
        }
        return address
    }
}
