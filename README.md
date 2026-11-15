# AirTrack Pro

Use **AirPods Pro (1st Gen / 2nd Gen / AirPods 3 / AirPods Max)** connected to your iPhone for ultra-smooth **OpenTrack** head tracking on Windows, controlled through a live **PC Web Interface**, with optional **real-time Windows Desktop Audio streaming** back to your AirPods Pro.

## Architecture

1. **Native iOS App (`ios/AirTrackPro`)**:
   - Uses Apple's official `CMHeadphoneMotionManager` framework for native AirPods Pro sensor fusion (quaternion + gyro rotation rate + acceleration + sensor location).
   - Streams compact 128-byte binary `ATPT` packets over local Wi-Fi UDP (with WebSocket fallback).
   - Includes a **LAN Auto-Discover** scanner, **QR Code pairing**, a tactile **Recenter Head** button, and an `AVAudioSourceNode` low-latency ring-buffered player so you can hear PC desktop audio through your AirPods while head tracking stays active (including in the background).
   - Built automatically in GitHub Actions as an **unsigned `.ipa`** ready to sign & install with **SideStore** (no paid Apple Developer account or Mac required).

2. **PC Companion Server & Web Interface (`pc/`)**:
   - Pure C# Windows server with zero third-party dependencies (`Http + RFC 6455 WebSocket + UDP + COM WASAPI Loopback`).
   - Serves a live **PC Web Dashboard** (`http://127.0.0.1:7878`) with:
     - Real-time **3D Head Orientation Preview** & telemetry
     - **QR Code** pairing & adapter IP selector
     - **120 Hz / 240 Hz** OpenTrack UDP output (`48-byte` little-endian `X, Y, Z, Yaw, Pitch, Roll` to `127.0.0.1:4242`)
     - **One-Euro adaptive filter**, short-horizon gyro lead prediction (turns 25 Hz AirPods IMU updates into silky 120 Hz output), soft deadzone, response curves, 6DoF neck/shoulder pivot model, yaw drift guard, and global **F12 Recenter** hotkey
     - **WASAPI Desktop Audio Loopback** streaming (`48 kHz` stereo PCM over UDP to the iPhone)

---

## Setup Guide

### 1. Build & Install the iOS App via SideStore
1. Open the **Actions** tab in your GitHub repository and open the latest **Build AirTrack Pro** workflow run.
2. Download the **`AirTrackPro-iOS-IPA`** artifact and unzip `AirTrackPro-unsigned.ipa` on your iPhone (Files app).
3. Open **SideStore**, tap **`+`**, select `AirTrackPro-unsigned.ipa`, and install it.
4. On first launch, allow **Motion & Fitness**, **Local Network**, and (if using QR pairing) **Camera** permissions.

### 2. Run the PC Companion & Open Web Dashboard
- **Local run (zero SDK required)**: Double-click [`pc/start.bat`](pc/start.bat) (or run `pc\AirTrackBridge.exe`).
- Your default browser will open `http://127.0.0.1:7878` automatically.
- *Note:* When Windows Firewall asks, allow **Private Networks** so the iPhone can reach ports `7878` (Web/WS) and `7879` (UDP tracking/audio).

### 3. Configure OpenTrack
1. Open **OpenTrack** on your PC.
2. Set **Input** to **`UDP over network`**.
3. Click the Input settings hammer icon and make sure **Port** is **`4242`** (matches AirTrack default).
4. Set your desired **Output** (e.g., `freetrack 2.0 Enhanced` for TrackIR-compatible games) and click **Start**.
5. Put your AirPods Pro in your ears, pair the iPhone app via **Auto-Discover PC** or **Scan QR**, and tap **RECENTER HEAD** (or press **F12** on PC).
