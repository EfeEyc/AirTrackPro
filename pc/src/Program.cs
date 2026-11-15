using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AirTrackBridge
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        private static volatile bool _exitRequested;

        public static int Main(string[] args)
        {
            bool noBrowser = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--no-browser", StringComparison.OrdinalIgnoreCase))
                    noBrowser = true;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string settingsPath = Path.Combine(baseDir, "airtrack_settings.xml");

            var settingsStore = new SettingsStore(settingsPath);
            var tracking = new HeadTrackingEngine(settingsStore);
            BridgeServer server = null;
            WasapiLoopbackCapture audio = null;

            audio = new WasapiLoopbackCapture(delegate(byte[] pcm, int len, int rate)
            {
                if (server != null)
                {
                    server.OnDesktopAudioChunk(pcm, len, rate);
                }
            });

            server = new BridgeServer(settingsStore, tracking, audio);

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                _exitRequested = true;
            };

            try
            {
                timeBeginPeriod(1);
            }
            catch { }

            try
            {
                tracking.Start();
                server.Start();

                BridgeSettings s = settingsStore.GetSnapshot();
                string localUrl = "http://127.0.0.1:" + s.HttpPort;
                string primaryIp = server.GetPrimaryIp(s);

                Console.WriteLine("==================================================");
                Console.WriteLine("  AirTrack Pro — PC Companion & OpenTrack Bridge");
                Console.WriteLine("==================================================");
                Console.WriteLine("  Web Dashboard : " + localUrl);
                Console.WriteLine("  LAN IP        : " + primaryIp + " (HTTP:" + s.HttpPort + " / UDP:" + s.UdpPort + ")");
                Console.WriteLine("  OpenTrack UDP : " + s.OpenTrackHost + ":" + s.OpenTrackPort + " @ " + s.OutputRateHz + " Hz");
                Console.WriteLine("  Hotkey        : F12 to Recenter");
                Console.WriteLine("  Press Ctrl+C to stop.");
                Console.WriteLine("==================================================");

                if (!noBrowser && s.AutoOpenBrowser)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = localUrl,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }

                bool wasKeyDown = false;
                while (!_exitRequested)
                {
                    BridgeSettings cur = settingsStore.GetSnapshot();
                    if (cur.EnableGlobalHotkey)
                    {
                        bool isDown = (GetAsyncKeyState(cur.HotkeyVirtualKey) & 0x8000) != 0;
                        if (isDown && !wasKeyDown)
                        {
                            tracking.Recenter();
                            Console.WriteLine("[Hotkey] Recentered head tracking.");
                        }
                        wasKeyDown = isDown;
                    }
                    Thread.Sleep(40);
                }
            }
            finally
            {
                try { server.Dispose(); } catch { }
                try { audio.Dispose(); } catch { }
                try { tracking.Dispose(); } catch { }
                try { timeEndPeriod(1); } catch { }
            }

            return 0;
        }
    }
}
