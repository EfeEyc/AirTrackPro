# AirTrack Pro

Ultra-low-latency **OpenTrack head tracking** + optional **Windows desktop audio streaming** powered by your AirPods' built-in motion sensors and an iPhone/iPhone Duo companion app.

## Features

- **Broad AirPods & Beats Support:** Works with any headphone supported by Apple's `CMHeadphoneMotionManager` (AirPods Pro 1st / 2nd / 3rd Gen, AirPods 3rd & 4th Gen, AirPods Max Lightning & USB-C, Beats Fit Pro / Studio Pro — including single-earbud wear).
- **Zero-Lag Tracking Pipeline:**
  - Streams compact 128-byte binary `ATPT` frames over LAN **UDP** with `.interactiveVoice` Wi-Fi QoS priority (`WebSocket` fallback included).
  - Pure C# Windows bridge running with 1 ms timer resolution (`120 Hz` / `240 Hz` output) feeding 48-byte `X, Y, Z, Yaw, Pitch, Roll` UDP packets directly into **OpenTrack** (`127.0.0.1:4242`).
  - Switchable Web UI presets (**Raw / Zero-Lag**, **Fast 1-Euro (Default)**, **Extra Smooth**), gyro lookahead prediction, 6DoF neck-pivot model, soft yaw drift guard, and global **F12 Recenter**.
- **Desktop Audio Loopback (Optional):** Captures Windows audio via **WASAPI Loopback** (`48 kHz` stereo PCM) and streams it over UDP back to your AirPods Pro so you hear PC game/flight-sim audio while head tracking stays active (including in background).
- **Native iPhone, iPad & iPhone Duo UI:** Built with Xcode 27 / iOS 27.2 SDK (`iOS 15.0+` deployment target) with adaptive 1-column / 2-column foldable layout and horizon-tracking QR scanner.
- **Zero Apple Developer Paid Account Needed:** Unsigned (ad-hoc wrapped) `.ipa` built in GitHub Actions for one-tap **SideStore** installation.

---

## Quick Start

### 1. Download from Releases
Grab both files from the [**Latest Release**](https://github.com/EfeEyc/AirTrackPro/releases/latest):
- **`AirTrackPro-unsigned.ipa`** — install on your iPhone with **SideStore**
- **`AirTrackBridge-win-x64.zip`** — extract and run on Windows (`AirTrackBridge.exe` / `start.bat`)

*(You can also download per-commit builds from the repository **Actions** tab.)*

### 2. Install the iOS App (SideStore)
1. Download `AirTrackPro-unsigned.ipa` onto your iPhone (Files app).
2. Open **SideStore**, tap **`+`**, select `AirTrackPro-unsigned.ipa`, and install it.
3. On first launch, allow **Motion & Fitness**, **Local Network**, and **Camera** permissions.

### 3. Run the PC Bridge
1. Extract `AirTrackBridge-win-x64.zip` (or double-click `pc/start.bat` from the repo) and run `AirTrackBridge.exe`.
2. Your browser opens **`http://127.0.0.1:7878`** with the live 3D head preview, filter presets, and QR pairing code.
3. If Windows Firewall prompts, allow access on **Private** networks so the iPhone can reach:
   - **TCP `7878`** (Web Dashboard & WebSocket fallback)
   - **UDP `7879`** (low-latency head tracking & desktop audio stream)
   - **UDP `7880`** (LAN auto-discovery)

### 4. Pair iPhone & Setup OpenTrack
1. Open **OpenTrack** on your PC:
   - **Input:** `UDP over network` (Port **`4242`**)
   - **Output:** `freetrack 2.0 Enhanced` (for TrackIR-compatible games)
   - **Filter:** Set to **`None`** (or minimal Accela smoothing) to avoid double-filtering on top of AirTrack's bridge, then click **Start**.
2. Put your AirPods in your ears.
3. In **AirTrack Pro** on iPhone, tap **Scan QR** (or **Auto-Discover PC**).
   - Verify the connection badge reads **`UDP + WS`** for lowest latency.
4. Look straight at your monitor and tap **RECENTER HEAD** (on iPhone, in the Web UI, or press **F12** on PC).

---

## Repository Structure & Local Development

- `pc/` — Self-contained pure-C# Windows bridge (`Http + WebSocket + UDP + COM WASAPI Loopback`, zero NuGet dependencies).
  - Run `powershell -ExecutionPolicy Bypass -File pc\build_local.ps1` to compile `pc\AirTrackBridge.exe` locally with the built-in .NET Framework compiler, or publish with `.NET 8`: `dotnet publish pc/AirTrackBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/pc`.
- `ios/AirTrackPro/` — Swift/SwiftUI iOS app configured with [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`project.yml`).
- `.github/workflows/build.yml` — Automated macOS (`xcode-27`) `.ipa` build, Windows `win-x64` `.exe` build, and tagged GitHub Release publishing (`v*`).

## Troubleshooting Tips
- **Head tracking feels too smoothed:** Click **Raw / Zero-Lag** in the Web UI (`http://127.0.0.1:7878`) and set OpenTrack's own Filter to **None**.
- **Badge says `WebSocket Fallback` instead of `UDP + WS`:** Allow **UDP port `7879`** inbound for `AirTrackBridge.exe` in Windows Defender Firewall on Private networks.
- **No motion data on iPhone:** Ensure your AirPods are worn in-ear, spatial/head-tracking is not blocked in iOS Accessibility settings, and Motion & Fitness permission is enabled for AirTrack Pro in iOS Settings.
