import SwiftUI
import AVFoundation

struct QRScannerSheet: View {
    let onScanned: (String) -> Void
    @Environment(\.presentationMode) private var presentationMode
    @State private var permissionDenied = false

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            if permissionDenied {
                VStack(spacing: 16) {
                    Image(systemName: "camera.fill")
                        .font(.system(size: 42))
                        .foregroundColor(.orange)
                    Text("Camera Permission Needed")
                        .font(.headline)
                        .foregroundColor(.white)
                    Text("Allow camera access in iOS Settings to scan the pairing QR code from the PC Web Interface, or enter the PC IP manually.")
                        .font(.subheadline)
                        .foregroundColor(.gray)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 28)
                    Button("Close") {
                        presentationMode.wrappedValue.dismiss()
                    }
                    .padding(.horizontal, 24)
                    .padding(.vertical, 10)
                    .background(Color.blue)
                    .foregroundColor(.white)
                    .cornerRadius(10)
                }
            } else {
                CameraPreviewRepresentable(
                    onCodeScanned: { code in
                        onScanned(code)
                        presentationMode.wrappedValue.dismiss()
                    },
                    onPermissionDenied: {
                        permissionDenied = true
                    }
                )
                .ignoresSafeArea()

                VStack {
                    HStack {
                        Spacer()
                        Button(action: { presentationMode.wrappedValue.dismiss() }) {
                            Image(systemName: "xmark.circle.fill")
                                .font(.system(size: 32))
                                .foregroundColor(.white.opacity(0.9))
                                .padding()
                        }
                    }
                    Spacer()
                    RoundedRectangle(cornerRadius: 18)
                        .stroke(Color.cyan, lineWidth: 3)
                        .frame(width: 240, height: 240)
                        .background(Color.clear)
                    Spacer()
                    Text("Point at the QR code in the PC Web Interface")
                        .font(.subheadline)
                        .foregroundColor(.white)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 10)
                        .background(Color.black.opacity(0.65))
                        .cornerRadius(10)
                        .padding(.bottom, 34)
                }
            }
        }
    }
}

private struct CameraPreviewRepresentable: UIViewControllerRepresentable {
    let onCodeScanned: (String) -> Void
    let onPermissionDenied: () -> Void

    func makeUIViewController(context: Context) -> ScannerViewController {
        let vc = ScannerViewController()
        vc.onCodeScanned = onCodeScanned
        vc.onPermissionDenied = onPermissionDenied
        return vc
    }

    func updateUIViewController(_ uiViewController: ScannerViewController, context: Context) {}
}

private final class ScannerViewController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    var onCodeScanned: ((String) -> Void)?
    var onPermissionDenied: (() -> Void)?

    private var session: AVCaptureSession?
    private var previewLayer: AVCaptureVideoPreviewLayer?
    private var rotationCoordinatorObject: AnyObject?
    private var rotationObservation: NSKeyValueObservation?
    private var didFinish = false

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        checkPermissionAndStart()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        previewLayer?.frame = view.bounds
        applyLegacyOrientationIfNeeded()
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        stopSession()
    }

    private func checkPermissionAndStart() {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            setupSession()
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
                DispatchQueue.main.async {
                    if granted {
                        self?.setupSession()
                    } else {
                        self?.onPermissionDenied?()
                    }
                }
            }
        default:
            DispatchQueue.main.async {
                self.onPermissionDenied?()
            }
        }
    }

    private func setupSession() {
        let capSession = AVCaptureSession()
        guard let device = AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: device),
              capSession.canAddInput(input) else {
            return
        }
        capSession.addInput(input)

        let output = AVCaptureMetadataOutput()
        if capSession.canAddOutput(output) {
            capSession.addOutput(output)
            output.setMetadataObjectsDelegate(self, queue: DispatchQueue.main)
            output.metadataObjectTypes = [.qr]
        }

        let preview = AVCaptureVideoPreviewLayer(session: capSession)
        preview.videoGravity = .resizeAspectFill
        preview.frame = view.bounds
        view.layer.addSublayer(preview)

        self.session = capSession
        self.previewLayer = preview
        configurePreviewRotation(device: device, previewLayer: preview)

        DispatchQueue.global(qos: .userInitiated).async {
            capSession.startRunning()
        }
    }

    private func configurePreviewRotation(device: AVCaptureDevice, previewLayer: AVCaptureVideoPreviewLayer) {
        if #available(iOS 17.0, *) {
            let coordinator = AVCaptureDevice.RotationCoordinator(device: device, previewLayer: previewLayer)
            self.rotationCoordinatorObject = coordinator
            applyRotationAngle(coordinator.videoRotationAngleForHorizonLevelPreview)
            self.rotationObservation = coordinator.observe(\.videoRotationAngleForHorizonLevelPreview, options: [.new]) { [weak self] coord, _ in
                let angle = coord.videoRotationAngleForHorizonLevelPreview
                DispatchQueue.main.async {
                    self?.applyRotationAngle(angle)
                }
            }
        } else {
            applyLegacyOrientationIfNeeded()
        }
    }

    @available(iOS 17.0, *)
    private func applyRotationAngle(_ angle: CGFloat) {
        guard let conn = previewLayer?.connection,
              conn.isVideoRotationAngleSupported(angle) else { return }
        conn.videoRotationAngle = angle
    }

    private func applyLegacyOrientationIfNeeded() {
        if #available(iOS 17.0, *) { return }
        guard let conn = previewLayer?.connection, conn.isVideoOrientationSupported else { return }
        let uiOrientation = view.window?.windowScene?.interfaceOrientation ?? .portrait
        switch uiOrientation {
        case .landscapeLeft:
            conn.videoOrientation = .landscapeLeft
        case .landscapeRight:
            conn.videoOrientation = .landscapeRight
        case .portraitUpsideDown:
            conn.videoOrientation = .portraitUpsideDown
        default:
            conn.videoOrientation = .portrait
        }
    }

    private func stopSession() {
        rotationObservation?.invalidate()
        rotationObservation = nil
        rotationCoordinatorObject = nil
        if let s = session, s.isRunning {
            DispatchQueue.global(qos: .userInitiated).async {
                s.stopRunning()
            }
        }
    }

    func metadataOutput(
        _ output: AVCaptureMetadataOutput,
        didOutput metadataObjects: [AVMetadataObject],
        from connection: AVCaptureConnection
    ) {
        guard !didFinish,
              let obj = metadataObjects.first as? AVMetadataMachineReadableCodeObject,
              let val = obj.stringValue else { return }
        didFinish = true
        UIImpactFeedbackGenerator(style: .medium).impactOccurred()
        stopSession()
        onCodeScanned?(val)
    }
}
