namespace AirTrackBridge
{
    public static class WebUiAssets
    {
        public static readonly string IndexHtml = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>AirTrack Pro — PC Companion &amp; OpenTrack Bridge</title>
<style>
  :root {
    --bg: #080c14;
    --panel: #101725;
    --panel2: #151e30;
    --border: #233049;
    --accent: #38bdf8;
    --accent2: #22d3ee;
    --green: #34d399;
    --amber: #fbbf24;
    --red: #f87171;
    --text: #e2e8f0;
    --muted: #94a3b8;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 18px 22px 36px;
    background: radial-gradient(circle at top right, #111d33, var(--bg) 55%);
    color: var(--text);
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    font-size: 14px;
  }
  .topbar {
    display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between;
    gap: 12px; margin-bottom: 18px; padding: 14px 18px;
    background: rgba(16, 23, 37, 0.9); border: 1px solid var(--border); border-radius: 14px;
  }
  .brand { display: flex; align-items: center; gap: 12px; }
  .logo {
    width: 38px; height: 38px; border-radius: 10px;
    background: linear-gradient(135deg, #0ea5e9, #6366f1);
    display: grid; place-items: center; font-weight: 800; color: white; letter-spacing: -0.5px;
  }
  .brand h1 { margin: 0; font-size: 18px; font-weight: 700; }
  .brand small { color: var(--muted); display: block; font-size: 12px; }
  .pills { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
  .pill {
    display: inline-flex; align-items: center; gap: 6px; padding: 6px 11px;
    border-radius: 999px; background: var(--panel2); border: 1px solid var(--border);
    font-size: 12px; font-weight: 600; color: var(--muted);
  }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--red); }
  .pill.ok { color: #d1fae5; border-color: rgba(52,211,153,0.4); }
  .pill.ok .dot { background: var(--green); box-shadow: 0 0 8px var(--green); }
  .pill.warn { color: #fef3c7; border-color: rgba(251,191,36,0.4); }
  .pill.warn .dot { background: var(--amber); }
  .actions { display: flex; gap: 8px; }
  button {
    border: 1px solid var(--border); background: var(--panel2); color: var(--text);
    padding: 8px 14px; border-radius: 9px; font-weight: 600; cursor: pointer; font-size: 13px;
    transition: transform .08s ease, background .15s ease, border-color .15s ease;
  }
  button:hover { background: #1c2840; border-color: #3b4f74; }
  button:active { transform: scale(0.98); }
  button.primary {
    background: linear-gradient(135deg, #0284c7, #2563eb); border-color: #38bdf8; color: white;
  }
  button.primary:hover { filter: brightness(1.08); }
  .grid {
    display: grid; grid-template-columns: repeat(12, minmax(0, 1fr)); gap: 16px;
  }
  .card {
    background: rgba(16, 23, 37, 0.92); border: 1px solid var(--border);
    border-radius: 14px; padding: 16px;
  }
  .card h2 {
    margin: 0 0 12px; font-size: 14px; text-transform: uppercase; letter-spacing: 0.06em;
    color: var(--accent); display: flex; justify-content: space-between; align-items: center;
  }
  .col-7 { grid-column: span 7; }
  .col-5 { grid-column: span 5; }
  .col-6 { grid-column: span 6; }
  .col-4 { grid-column: span 4; }
  .col-12 { grid-column: span 12; }
  @media (max-width: 1050px) {
    .col-7, .col-5, .col-6, .col-4 { grid-column: span 12; }
  }
  .preview-wrap {
    display: grid; grid-template-columns: 1fr 220px; gap: 14px; align-items: center;
  }
  @media (max-width: 720px) { .preview-wrap { grid-template-columns: 1fr; } }
  canvas#headCanvas {
    width: 100%; height: 270px; background: radial-gradient(circle at center, #0e1729, #060a12);
    border: 1px solid var(--border); border-radius: 12px; display: block;
  }
  .telemetry-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
  .metric {
    background: var(--panel2); border: 1px solid var(--border); border-radius: 10px; padding: 9px 10px;
  }
  .metric label { display: block; font-size: 11px; color: var(--muted); }
  .metric span { font-size: 17px; font-weight: 700; font-variant-numeric: tabular-nums; }
  .pair-box { display: flex; gap: 16px; align-items: center; flex-wrap: wrap; }
  .qr-frame {
    width: 176px; height: 176px; background: white; padding: 8px; border-radius: 12px;
    flex-shrink: 0; display: grid; place-items: center;
  }
  .qr-frame img { width: 100%; height: 100%; display: block; }
  .pair-info { flex: 1; min-width: 210px; }
  .uri-box {
    background: #080c14; border: 1px solid var(--border); border-radius: 8px;
    padding: 8px 10px; font-family: ui-monospace, Consolas, monospace; font-size: 12px;
    color: var(--accent2); word-break: break-all; margin: 8px 0;
  }
  .row { display: flex; flex-direction: column; gap: 4px; margin-bottom: 11px; }
  .row-inline {
    display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-bottom: 10px;
  }
  .row label, .row-inline label { font-size: 13px; color: var(--text); }
  .val { color: var(--accent2); font-weight: 700; font-variant-numeric: tabular-nums; float: right; }
  input[type=range] { width: 100%; accent-color: var(--accent); cursor: pointer; }
  input[type=text], input[type=number], select {
    width: 100%; background: #090e18; border: 1px solid var(--border); color: var(--text);
    border-radius: 8px; padding: 7px 10px; font-size: 13px;
  }
  .two-col { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
  .three-col { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 10px; }
  .chk { display: flex; align-items: center; gap: 8px; cursor: pointer; user-select: none; }
  .chk input { width: 16px; height: 16px; accent-color: var(--accent); }
  .hint { color: var(--muted); font-size: 12px; line-height: 1.45; }
</style>
</head>
<body>
  <div class=""topbar"">
    <div class=""brand"">
      <div class=""logo"">AT</div>
      <div>
        <h1>AirTrack Pro <span style=""font-weight:400;color:var(--muted)"">PC Companion</span></h1>
        <small>iOS AirPods Pro Head Tracking &rarr; OpenTrack UDP + Desktop Audio Stream</small>
      </div>
    </div>
    <div class=""pills"">
      <div id=""pillPhone"" class=""pill""><span class=""dot""></span><span>iPhone: Waiting</span></div>
      <div id=""pillPods"" class=""pill""><span class=""dot""></span><span>AirPods: Inactive</span></div>
      <div id=""pillAudio"" class=""pill""><span class=""dot""></span><span>Audio: Idle</span></div>
    </div>
    <div class=""actions"">
      <button id=""btnPulse"">Test OpenTrack</button>
      <button id=""btnRecenter"" class=""primary"">Recenter (F12)</button>
    </div>
  </div>

  <div class=""grid"">
    <div class=""card col-7"">
      <h2><span>Live 3D Orientation &amp; OpenTrack Output</span><span id=""statRate"" style=""color:var(--muted);font-size:12px"">0 Hz in &rarr; 120 Hz out</span></h2>
      <div class=""preview-wrap"">
        <canvas id=""headCanvas"" width=""540"" height=""270""></canvas>
        <div class=""telemetry-grid"">
          <div class=""metric""><label>Yaw (Out)</label><span id=""mYaw"">0.0&deg;</span></div>
          <div class=""metric""><label>Pitch (Out)</label><span id=""mPitch"">0.0&deg;</span></div>
          <div class=""metric""><label>Roll (Out)</label><span id=""mRoll"">0.0&deg;</span></div>
          <div class=""metric""><label>Raw Y / P / R</label><span id=""mRaw"" style=""font-size:12px"">0 / 0 / 0</span></div>
          <div class=""metric""><label>6DoF X / Y / Z</label><span id=""mPos"" style=""font-size:12px"">0 / 0 / 0</span></div>
          <div class=""metric""><label>Jitter / Pkts</label><span id=""mPkts"" style=""font-size:12px"">0 ms / 0</span></div>
        </div>
      </div>
    </div>

    <div class=""card col-5"">
      <h2><span>Pair iPhone (SideStore App)</span><span id=""netIpBadge"" style=""color:var(--muted);font-size:12px"">LAN</span></h2>
      <div class=""pair-box"">
        <div class=""qr-frame""><img id=""qrImg"" alt=""Pairing QR""></div>
        <div class=""pair-info"">
          <div class=""row"">
            <label>PC Network Adapter IP</label>
            <select id=""selAdapter""></select>
          </div>
          <div class=""uri-box"" id=""pairUri"">airtrack://...</div>
          <p class=""hint"">
            1. Keep iPhone &amp; PC on the same Wi-Fi/LAN.<br>
            2. Open <strong>AirTrack Pro</strong> on iPhone &amp; tap <strong>Auto-Discover</strong> or <strong>Scan QR</strong>.<br>
            3. In OpenTrack, choose Input: <strong>UDP over network</strong> (Port <strong id=""otPortLbl"">4242</strong>).
          </p>
        </div>
      </div>
    </div>

    <div class=""card col-6"">
      <h2><span>Desktop Audio &rarr; iPhone (AirPods Pro)</span><span id=""audioStats"" style=""color:var(--muted);font-size:12px"">0 kbps</span></h2>
      <div class=""row-inline"">
        <label class=""chk""><input type=""checkbox"" id=""EnableAudioStream""> Stream PC Desktop Audio to iPhone</label>
      </div>
      <div class=""row"">
        <label>Windows Playback Device (WASAPI Loopback)</label>
        <select id=""AudioDeviceId""></select>
      </div>
      <div class=""row"">
        <label>Stream Gain <span class=""val"" id=""vAudioGain"">100%</span></label>
        <input type=""range"" id=""AudioGain"" min=""0"" max=""2"" step=""0.05"">
      </div>
      <p class=""hint"">
        Captures Windows playback audio via WASAPI loopback (48 kHz stereo PCM) and streams directly over LAN UDP to your iPhone so you hear game audio through your AirPods Pro while head tracking stays active.
      </p>
    </div>

    <div class=""card col-6"">
      <h2><span>OpenTrack Target &amp; Precision Filtering</span></h2>
      <div class=""three-col"" style=""margin-bottom:10px"">
        <div class=""row""><label>OpenTrack IP</label><input type=""text"" id=""OpenTrackHost""></div>
        <div class=""row""><label>Port</label><input type=""number"" id=""OpenTrackPort"" min=""1"" max=""65535""></div>
        <div class=""row""><label>Output Rate (Hz)</label>
          <select id=""OutputRateHz""><option value=""60"">60 Hz</option><option value=""120"">120 Hz</option><option value=""240"">240 Hz</option></select>
        </div>
      </div>
      <div class=""two-col"">
        <div class=""row"">
          <label>Interp Smoothing <span class=""val"" id=""vInterpSmoothingMs"">12 ms</span></label>
          <input type=""range"" id=""InterpSmoothingMs"" min=""2"" max=""35"" step=""1"">
        </div>
        <div class=""row"">
          <label>Gyro Lead Prediction <span class=""val"" id=""vGyroLeadMs"">6 ms</span></label>
          <input type=""range"" id=""GyroLeadMs"" min=""0"" max=""20"" step=""1"">
        </div>
      </div>
      <div class=""row-inline"">
        <label class=""chk""><input type=""checkbox"" id=""EnableOneEuroFilter""> One-Euro Adaptive Filter</label>
        <label class=""chk""><input type=""checkbox"" id=""EnableYawDriftGuard""> Soft Yaw Drift Guard</label>
        <label class=""chk""><input type=""checkbox"" id=""EnableGlobalHotkey""> Global F12 Recenter</label>
      </div>
      <div class=""two-col"">
        <div class=""row"">
          <label>One-Euro Min Cutoff <span class=""val"" id=""vOneEuroMinCutoff"">1.6 Hz</span></label>
          <input type=""range"" id=""OneEuroMinCutoff"" min=""0.2"" max=""6.0"" step=""0.1"">
        </div>
        <div class=""row"">
          <label>One-Euro Speed Beta <span class=""val"" id=""vOneEuroBeta"">0.055</span></label>
          <input type=""range"" id=""OneEuroBeta"" min=""0"" max=""0.25"" step=""0.005"">
        </div>
      </div>
    </div>

    <div class=""card col-12"">
      <h2><span>Axis Sensitivity, Response Curves &amp; 6DoF Neck Model</span></h2>
      <div class=""three-col"">
        <div class=""row"">
          <label>Yaw Sensitivity <span class=""val"" id=""vYawSensitivity"">2.2x</span></label>
          <input type=""range"" id=""YawSensitivity"" min=""0.3"" max=""6.0"" step=""0.1"">
        </div>
        <div class=""row"">
          <label>Pitch Sensitivity <span class=""val"" id=""vPitchSensitivity"">2.0x</span></label>
          <input type=""range"" id=""PitchSensitivity"" min=""0.3"" max=""6.0"" step=""0.1"">
        </div>
        <div class=""row"">
          <label>Roll Sensitivity <span class=""val"" id=""vRollSensitivity"">1.2x</span></label>
          <input type=""range"" id=""RollSensitivity"" min=""0.2"" max=""4.0"" step=""0.1"">
        </div>
      </div>
      <div class=""three-col"">
        <div class=""row"">
          <label>Deadzone <span class=""val"" id=""vDeadzoneDegrees"">0.12&deg;</span></label>
          <input type=""range"" id=""DeadzoneDegrees"" min=""0"" max=""2.0"" step=""0.02"">
        </div>
        <div class=""row"">
          <label>Curve Exponent <span class=""val"" id=""vResponseExponent"">1.15</span></label>
          <input type=""range"" id=""ResponseExponent"" min=""0.7"" max=""2.2"" step=""0.05"">
        </div>
        <div class=""row"">
          <label>6DoF Neck Pivot <span class=""val"" id=""vNeckModelIntensity"">0.65x</span></label>
          <input type=""range"" id=""NeckModelIntensity"" min=""0"" max=""2.0"" step=""0.05"">
        </div>
      </div>
      <div class=""pills"" style=""margin-top:6px"">
        <label class=""chk""><input type=""checkbox"" id=""InvertYaw""> Invert Yaw</label>
        <label class=""chk""><input type=""checkbox"" id=""InvertPitch""> Invert Pitch</label>
        <label class=""chk""><input type=""checkbox"" id=""InvertRoll""> Invert Roll</label>
        <label class=""chk""><input type=""checkbox"" id=""EnableNeckModel""> Enable 6DoF Neck Model</label>
        <label class=""chk""><input type=""checkbox"" id=""QuaternionTransposeMode""> Alt Quaternion Frame</label>
      </div>
    </div>
  </div>

<script>
(function() {
  var state = { yaw: 0, pitch: 0, roll: 0, x: 0, y: 0, z: 0 };
  var settings = {};
  var saveTimer = null;

  var numKeys = ['OpenTrackPort','OutputRateHz','GyroLeadMs','InterpSmoothingMs','OneEuroMinCutoff','OneEuroBeta','DeadzoneDegrees','ResponseExponent','YawSensitivity','PitchSensitivity','RollSensitivity','NeckModelIntensity','AudioGain'];
  var boolKeys = ['EnableOneEuroFilter','InvertYaw','InvertPitch','InvertRoll','EnableNeckModel','EnableYawDriftGuard','EnableAudioStream','EnableGlobalHotkey','QuaternionTransposeMode'];
  var strKeys = ['OpenTrackHost','AudioDeviceId'];

  function el(id) { return document.getElementById(id); }

  function updateLabels() {
    el('vAudioGain').textContent = Math.round((settings.AudioGain || 1) * 100) + '%';
    el('vInterpSmoothingMs').textContent = (settings.InterpSmoothingMs || 12) + ' ms';
    el('vGyroLeadMs').textContent = (settings.GyroLeadMs || 6) + ' ms';
    el('vOneEuroMinCutoff').textContent = Number(settings.OneEuroMinCutoff || 1.6).toFixed(1) + ' Hz';
    el('vOneEuroBeta').textContent = Number(settings.OneEuroBeta || 0.055).toFixed(3);
    el('vYawSensitivity').textContent = Number(settings.YawSensitivity || 2.2).toFixed(1) + 'x';
    el('vPitchSensitivity').textContent = Number(settings.PitchSensitivity || 2.0).toFixed(1) + 'x';
    el('vRollSensitivity').textContent = Number(settings.RollSensitivity || 1.2).toFixed(1) + 'x';
    el('vDeadzoneDegrees').textContent = Number(settings.DeadzoneDegrees || 0.12).toFixed(2) + '\u00B0';
    el('vResponseExponent').textContent = Number(settings.ResponseExponent || 1.15).toFixed(2);
    el('vNeckModelIntensity').textContent = Number(settings.NeckModelIntensity || 0.65).toFixed(2) + 'x';
    el('otPortLbl').textContent = settings.OpenTrackPort || 4242;
  }

  function applySettingsToUi(s) {
    settings = s;
    numKeys.concat(strKeys).forEach(function(k) {
      var node = el(k);
      if (node && s[k] !== undefined) node.value = s[k];
    });
    boolKeys.forEach(function(k) {
      var node = el(k);
      if (node && s[k] !== undefined) node.checked = !!s[k];
    });
    updateLabels();
  }

  function pushSettings() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(function() {
      fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(settings)
      });
    }, 90);
  }

  numKeys.forEach(function(k) {
    var node = el(k);
    if (!node) return;
    node.addEventListener('input', function() {
      settings[k] = Number(node.value);
      updateLabels();
      pushSettings();
    });
  });

  boolKeys.forEach(function(k) {
    var node = el(k);
    if (!node) return;
    node.addEventListener('change', function() {
      settings[k] = node.checked;
      pushSettings();
    });
  });

  strKeys.forEach(function(k) {
    var node = el(k);
    if (!node) return;
    node.addEventListener('change', function() {
      settings[k] = node.value;
      pushSettings();
    });
  });

  el('selAdapter').addEventListener('change', function() {
    settings.SelectedAdapterIp = el('selAdapter').value;
    refreshPairingQr(settings.SelectedAdapterIp);
    pushSettings();
  });

  el('btnRecenter').addEventListener('click', function() {
    fetch('/api/recenter', { method: 'POST' });
  });

  el('btnPulse').addEventListener('click', function() {
    fetch('/api/test-pulse', { method: 'POST' });
  });

  function refreshPairingQr(ip) {
    if (!ip) return;
    var uri = 'airtrack://' + ip + ':' + (settings.HttpPort || 7878) + ':' + (settings.UdpPort || 7879);
    el('pairUri').textContent = uri;
    el('netIpBadge').textContent = ip;
    el('qrImg').src = '/api/qr.svg?ip=' + encodeURIComponent(ip) + '&t=' + Date.now();
  }

  function loadInitial() {
    fetch('/api/bootstrap').then(function(r){ return r.json(); }).then(function(d) {
      var selDev = el('AudioDeviceId');
      selDev.innerHTML = '<option value="""">System Default Playback Device</option>';
      (d.audioDevices || []).forEach(function(dev) {
        var opt = document.createElement('option');
        opt.value = dev.id;
        opt.textContent = (dev.isDefault ? '[Default] ' : '') + dev.name;
        selDev.appendChild(opt);
      });

      var selNet = el('selAdapter');
      selNet.innerHTML = '';
      (d.adapters || []).forEach(function(a) {
        var opt = document.createElement('option');
        opt.value = a.ip;
        opt.textContent = a.ip + ' (' + a.name + ')';
        selNet.appendChild(opt);
      });

      applySettingsToUi(d.settings);
      var activeIp = d.settings.SelectedAdapterIp || d.primaryIp;
      if (activeIp) selNet.value = activeIp;
      refreshPairingQr(activeIp || d.primaryIp);
    });
  }

  function connectWs() {
    var proto = location.protocol === 'https:' ? 'wss://' : 'ws://';
    var ws = new WebSocket(proto + location.host + '/ws?role=web');
    ws.onmessage = function(ev) {
      try {
        var msg = JSON.parse(ev.data);
        if (msg.type === 'telemetry') {
          var t = msg.data;
          state.yaw = t.outYaw; state.pitch = t.outPitch; state.roll = t.outRoll;
          state.x = t.outX; state.y = t.outY; state.z = t.outZ;

          el('mYaw').textContent = t.outYaw.toFixed(1) + '\u00B0';
          el('mPitch').textContent = t.outPitch.toFixed(1) + '\u00B0';
          el('mRoll').textContent = t.outRoll.toFixed(1) + '\u00B0';
          el('mRaw').textContent = t.rawYaw.toFixed(1) + ' / ' + t.rawPitch.toFixed(1) + ' / ' + t.rawRoll.toFixed(1);
          el('mPos').textContent = t.outX.toFixed(0) + ' / ' + t.outY.toFixed(0) + ' / ' + t.outZ.toFixed(0);
          el('mPkts').textContent = t.jitterMs.toFixed(1) + ' ms / ' + t.packetsIn;
          el('statRate').textContent = t.inputHz.toFixed(1) + ' Hz in \u2192 ' + t.outputHz.toFixed(0) + ' Hz out';

          var pPhone = el('pillPhone');
          pPhone.className = 'pill ' + (t.phoneConnected ? 'ok' : '');
          pPhone.querySelector('span:last-child').textContent = t.phoneConnected ? ('iPhone: ' + t.phoneEndpoint) : 'iPhone: Waiting';

          var pPods = el('pillPods');
          pPods.className = 'pill ' + (t.airPodsActive ? 'ok' : (t.phoneConnected ? 'warn' : ''));
          var loc = t.sensorLocation === 1 ? ' (L)' : (t.sensorLocation === 2 ? ' (R)' : '');
          pPods.querySelector('span:last-child').textContent = t.airPodsActive ? ('AirPods: Active' + loc) : 'AirPods: Inactive';

          var pAud = el('pillAudio');
          pAud.className = 'pill ' + (msg.audioActive ? 'ok' : '');
          pAud.querySelector('span:last-child').textContent = msg.audioActive ? 'Audio: Streaming' : 'Audio: Idle';
          el('audioStats').textContent = (msg.audioKbps || 0).toFixed(0) + ' kbps';
        }
      } catch (e) {}
    };
    ws.onclose = function() { setTimeout(connectWs, 1200); };
  }

  // 3D Head & Visor Renderer
  var canvas = el('headCanvas');
  var ctx = canvas.getContext('2d');
  var verts = [
    [-1,-1.1,-0.9], [1,-1.1,-0.9], [1,1.1,-0.9], [-1,1.1,-0.9],
    [-1,-1.1,0.9], [1,-1.1,0.9], [1,1.1,0.9], [-1,1.1,0.9],
    [-0.85,0.2,0.95], [0.85,0.2,0.95], [0.85,0.75,0.95], [-0.85,0.75,0.95]
  ];
  var edges = [
    [0,1],[1,2],[2,3],[3,0],[4,5],[5,6],[6,7],[7,4],[0,4],[1,5],[2,6],[3,7],
    [8,9],[9,10],[10,11],[11,8]
  ];

  function draw3d() {
    var w = canvas.width, h = canvas.height;
    ctx.clearRect(0, 0, w, h);

    // Subtle horizon grid
    ctx.strokeStyle = 'rgba(56,189,248,0.12)';
    ctx.lineWidth = 1;
    for (var g = -4; g <= 4; g++) {
      ctx.beginPath();
      ctx.moveTo(w*0.1, h*0.5 + g*24);
      ctx.lineTo(w*0.9, h*0.5 + g*24);
      ctx.stroke();
    }

    var yr = state.yaw * Math.PI / 180;
    var pr = -state.pitch * Math.PI / 180;
    var rr = -state.roll * Math.PI / 180;
    var cy = Math.cos(yr), sy = Math.sin(yr);
    var cp = Math.cos(pr), sp = Math.sin(pr);
    var cr = Math.cos(rr), sr = Math.sin(rr);

    function rot(v) {
      var x = v[0], y = v[1], z = v[2];
      // Roll around Z (front)
      var x1 = x * cr - y * sr, y1 = x * sr + y * cr, z1 = z;
      // Pitch around X (right)
      var y2 = y1 * cp - z1 * sp, z2 = y1 * sp + z1 * cp, x2 = x1;
      // Yaw around Y (up)
      var x3 = x2 * cy + z2 * sy, z3 = -x2 * sy + z2 * cy, y3 = y2;
      var dist = 4.5 - z3;
      var scale = 250 / Math.max(1.5, dist);
      return [w * 0.5 + x3 * scale + state.x * 0.4, h * 0.5 - y3 * scale - state.y * 0.4, z3];
    }

    var pts = verts.map(rot);
    ctx.lineWidth = 2;
    edges.forEach(function(e, idx) {
      var p1 = pts[e[0]], p2 = pts[e[1]];
      ctx.strokeStyle = idx >= 12 ? '#22d3ee' : '#38bdf8';
      ctx.beginPath();
      ctx.moveTo(p1[0], p1[1]);
      ctx.lineTo(p2[0], p2[1]);
      ctx.stroke();
    });

    // Forward nose pointer
    var nose0 = rot([0, 0, 0.9]);
    var nose1 = rot([0, 0, 1.9]);
    ctx.strokeStyle = '#34d399';
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.moveTo(nose0[0], nose0[1]);
    ctx.lineTo(nose1[0], nose1[1]);
    ctx.stroke();

    requestAnimationFrame(draw3d);
  }

  loadInitial();
  connectWs();
  requestAnimationFrame(draw3d);
})();
</script>
</body>
</html>";
    }
}
