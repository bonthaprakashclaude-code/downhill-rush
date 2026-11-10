using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SkiGame.Sensors
{
    /// <summary>
    /// IGameInput driven by the body:
    ///   Wii Balance Board COP, left/right lean  -> Steer
    ///   Wii Balance Board COP, forward/back lean -> SpeedControl (optional)
    ///   EMG forearm flexor (BioRadio Ch1)        -> JumpTriggered
    ///   EMG biceps (BioRadio Ch2)                -> BackflipTriggered
    ///
    /// Sources (Assets/Resources/SensorSettings.asset):
    ///   COP: Replay (recorded file) | LiveFile (WiiMote1_All.txt, read-only)
    ///   EMG: Replay | LiveFile (growing CSV) | LiveBridge (BioRadio Python bridge over UDP)
    /// F4 = guided EMG calibration (saved per player), F3 = re-measure neutral stance,
    /// F6 = switch LIVE <-> REPLAY, F2 = sensor panel. Installed automatically by SensorBootstrap.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("Ski Game/Input/Sensor Input (EMG + COP)")]
    public class SensorInput : GameInputBase, IDirectSteering
    {
        // IDirectSteering: the board sets the direction; the keyboard (when pressed) keeps turn-rate steering.
        public bool SteerSetsHeading => Settings != null && Settings.leanSetsDirection && CopRunning && !keyboardSteering;
        public float HeadingFollowRate => Settings != null ? Settings.headingFollowRate : 150f;
        private bool keyboardSteering;

        public enum CalibrationPhase { None, Relax, FlexJump, FlexBackflip, Done }

        /// <summary>Runtime copy of the settings (F6 changes this copy, never the asset).</summary>
        public SensorSettings Settings { get; private set; }
        public CopProcessor Cop { get; private set; }
        public EmgProcessor Emg { get; private set; }

        public bool CopRunning { get; private set; }
        public bool EmgRunning { get; private set; }
        public string CopStatus { get; private set; } = "";
        public string EmgStatus { get; private set; } = "";
        public bool CopReceiving { get; private set; }
        public bool EmgReceiving { get; private set; }
        public bool IsLiveProfile { get; private set; }
        /// <summary>Measured input rates (lines or samples per second).</summary>
        public float CopLiveRate { get; private set; }
        public float EmgLiveRate { get; private set; }
        public bool CopStale { get; private set; }
        public double CopReplayTime => copRecording != null ? copReplayTime : 0;
        public bool EmgCalibratedForPlayer { get; private set; }

        // Guided calibration
        public CalibrationPhase CalPhase { get; private set; }
        public float CalPhaseRemaining { get; private set; }
        public string CalMessage { get; private set; } = "";
        private List<double> calRelaxJump, calRelaxFlip, calFlexJump, calFlexFlip;
        private float calDoneTimer;

        // Values carried over a scene reload (Restart) so the player does not have to recalibrate.
        private static bool carriedNeutral;
        private static double carriedNeutralX, carriedNeutralY;
        private static bool? carriedLiveProfile;

        private SensorSettings asset;
        private RecordedCop copRecording;
        private RecordedEmg emgRecording;
        private TextFileTailer copTail, emgTail;
        private EmgBridgeReceiver bridge;
        private EmgBridgeSelfTest bridgeSelfTest;
        private CopLiveFileSelfTest copSelfTest;
        private double copReplayTime, copAccumulator, emgReplayPos;
        private readonly List<string> lines = new List<string>(256);
        private readonly List<double> values = new List<double>(16);
        private readonly List<Vector3> copSamples = new List<Vector3>(64);   // x, y, weight
        private Vector3 copHold = new Vector3(float.NaN, float.NaN, 0f);
        private float lastCopLineTime = -999f, lastEmgDataTime = -999f;
        private float rateWindowStart; private int copRateCount, emgRateCount;
        private bool keyboardBroken;

        private static readonly KeyCode[] LeftKeys = { KeyCode.A, KeyCode.LeftArrow };
        private static readonly KeyCode[] RightKeys = { KeyCode.D, KeyCode.RightArrow };
        private static readonly KeyCode[] UpKeys = { KeyCode.W, KeyCode.UpArrow };
        private static readonly KeyCode[] DownKeys = { KeyCode.S, KeyCode.DownArrow };

        private const string PrefKey = "SkiGame.EmgCalibration.v5";

        // One UDP listener for the whole session (a scene reload must not lose the port).
        private static EmgBridgeReceiver sharedBridge;
        private static EmgBridgeReceiver SharedBridge(int port)
        {
            if (sharedBridge != null && (sharedBridge.Port != port || sharedBridge.Error != null)) { sharedBridge.Dispose(); sharedBridge = null; }
            if (sharedBridge == null)
            {
                sharedBridge = new EmgBridgeReceiver(port);
                Application.quitting -= DisposeSharedBridge;
                Application.quitting += DisposeSharedBridge;
            }
            while (sharedBridge.TryDequeue(out _)) { }   // drop anything that queued up between scenes
            return sharedBridge;
        }
        private static void DisposeSharedBridge() { sharedBridge?.Dispose(); sharedBridge = null; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            DisposeSharedBridge();
            carriedNeutral = false;
            carriedLiveProfile = null;
        }

        private void Awake()
        {
            asset = Resources.Load<SensorSettings>("SensorSettings");
            if (asset == null) asset = ScriptableObject.CreateInstance<SensorSettings>();
            bool live = carriedLiveProfile ?? (asset.copSource == SignalSource.LiveFile || asset.emgSource == SignalSource.LiveBridge || asset.emgSource == SignalSource.LiveFile);
            StartProfile(live);
        }

        // ------------------------------------------------------------------ setup

        /// <summary>LIVE = the sources configured in the asset; REPLAY = both recordings.</summary>
        private void StartProfile(bool live)
        {
            StopSources();
            Settings = Instantiate(asset);
            if (!live)
            {
                if (Settings.copSource != SignalSource.Off) Settings.copSource = SignalSource.Replay;
                if (Settings.emgSource != SignalSource.Off) Settings.emgSource = SignalSource.Replay;
            }
            IsLiveProfile = live;
            carriedLiveProfile = live;

            ConfigureResponse(Settings.steerResponseTime, Settings.steerReleaseTime, Settings.speedResponseTime,
                              Settings.steerDeadZone, Settings.speedDeadZone);
            Cop = new CopProcessor(Settings);
            Emg = new EmgProcessor(Settings);
            CalPhase = CalibrationPhase.None;
            StartCop();
            StartEmg();
            rateWindowStart = Time.unscaledTime;
            lastCopLineTime = lastEmgDataTime = -999f;
            Debug.Log($"[SkiGame] Sensor input ({(live ? "LIVE" : "REPLAY")}).  COP: {CopStatus}   EMG: {EmgStatus}");
        }

        private void StopSources()
        {
            copTail?.Dispose(); copTail = null;
            emgTail?.Dispose(); emgTail = null;
            bridge = null;                    // shared: stays open across Restart / profile switches
            bridgeSelfTest?.Dispose(); bridgeSelfTest = null;
            copSelfTest?.Dispose(); copSelfTest = null;
            copRecording = null; emgRecording = null;
            copReplayTime = copAccumulator = emgReplayPos = 0;
        }

        private void StartCop()
        {
            CopRunning = false;
            switch (Settings.copSource)
            {
                case SignalSource.Replay:
                    string path = SensorSettings.Resolve(Settings.copReplayFile);
                    try
                    {
                        copRecording = RecordedCop.Load(path);
                        CopRunning = true;
                        CopStatus = $"Replay {System.IO.Path.GetFileName(path)}  ({copRecording.SampleRate:0.#} Hz, {copRecording.Duration:0} s, loops)";
                    }
                    catch (System.Exception e)
                    {
                        CopStatus = "COP replay file not loaded: " + e.Message;
                        Debug.LogWarning("[SkiGame] " + CopStatus + "  (" + path + ")");
                    }
                    break;
                case SignalSource.LiveFile:
                    string live = SensorSettings.Resolve(Settings.copLiveFile);
                    if (Settings.copLiveSelfTest)
                    {
                        try
                        {
                            var rec = RecordedCop.Load(SensorSettings.Resolve(Settings.copReplayFile));
                            copSelfTest = new CopLiveFileSelfTest(rec, Settings.liveInvertX, Settings.liveInvertY);
                            live = copSelfTest.Path;
                        }
                        catch (System.Exception e) { Debug.LogWarning("[SkiGame] COP self-test failed: " + e.Message); }
                    }
                    copTail = new TextFileTailer(live);
                    CopRunning = true;
                    CopStatus = "Live " + System.IO.Path.GetFileName(live) + (copSelfTest != null ? " (SELF-TEST)" : "");
                    if (carriedNeutral) Cop.SetNeutral(carriedNeutralX, carriedNeutralY);
                    break;
                default:
                    CopStatus = "Off (keyboard steering)";
                    break;
            }
        }

        private void StartEmg()
        {
            EmgRunning = false;
            EmgCalibratedForPlayer = false;
            switch (Settings.emgSource)
            {
                case SignalSource.Replay:
                {
                    string path = SensorSettings.Resolve(Settings.emgReplayFile);
                    try
                    {
                        emgRecording = RecordedEmg.Load(path, Settings.jumpColumn, Settings.backflipColumn);
                        EmgProcessor.CalibrateFromRecording(emgRecording, Settings, Emg);
                        EmgRunning = true;
                        EmgCalibratedForPlayer = true;
                        EmgStatus = $"Replay {System.IO.Path.GetFileName(path)}  ({emgRecording.Count / Settings.emgSampleRate:0} s, loops)";
                    }
                    catch (System.Exception e)
                    {
                        EmgStatus = "EMG replay file not loaded: " + e.Message;
                        Debug.LogWarning("[SkiGame] " + EmgStatus + "  (" + path + ")");
                    }
                    break;
                }
                case SignalSource.LiveFile:
                    if (string.IsNullOrWhiteSpace(Settings.emgLiveFile)) { EmgStatus = "Live EMG file not set (Sensor Settings > Emg Live File)"; break; }
                    emgTail = new TextFileTailer(SensorSettings.Resolve(Settings.emgLiveFile));
                    EmgRunning = true;
                    EmgStatus = "Live " + System.IO.Path.GetFileName(emgTail.Path);
                    LoadSavedCalibration();
                    break;
                case SignalSource.LiveBridge:
                    bridge = SharedBridge(Settings.bridgePort);
                    EmgRunning = bridge.Error == null;
                    EmgStatus = bridge.Error ?? $"Bridge UDP {Settings.bridgePort}: waiting for ski_emg_bridge.py";
                    if (bridge.Error != null) Debug.LogWarning("[SkiGame] " + bridge.Error);
                    if (EmgRunning && Settings.bridgeSelfTest)
                    {
                        try
                        {
                            var rec = RecordedEmg.Load(SensorSettings.Resolve(Settings.emgReplayFile), Settings.jumpColumn, Settings.backflipColumn);
                            bridgeSelfTest = new EmgBridgeSelfTest(rec, Settings.bridgePort, Settings.emgSampleRate, Settings.bridgeJumpChannel, Settings.bridgeBackflipChannel);
                            EmgProcessor.CalibrateFromRecording(rec, Settings, Emg);
                            EmgCalibratedForPlayer = true;
                        }
                        catch (System.Exception e) { Debug.LogWarning("[SkiGame] EMG self-test failed: " + e.Message); }
                    }
                    else LoadSavedCalibration();
                    break;
                default:
                    EmgStatus = "Off (keyboard Space / F)";
                    break;
            }
        }

        private void LoadSavedCalibration()
        {
            if (!Settings.saveCalibration || !PlayerPrefs.HasKey(PrefKey)) return;
            string[] p = PlayerPrefs.GetString(PrefKey).Split(';');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (p.Length == 4 &&
                double.TryParse(p[0], System.Globalization.NumberStyles.Float, ci, out double jr) &&
                double.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out double jm) &&
                double.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out double fr) &&
                double.TryParse(p[3], System.Globalization.NumberStyles.Float, ci, out double fm))
            {
                Emg.Jump.SetCalibration(jr, jm);
                Emg.Backflip.SetCalibration(fr, fm);
                EmgCalibratedForPlayer = true;
            }
        }

        private void SaveCalibration()
        {
            if (!Settings.saveCalibration || emgRecording != null || bridgeSelfTest != null) return;   // replay / self-test calibrations are not saved
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            PlayerPrefs.SetString(PrefKey, string.Join(";",
                Emg.Jump.RestLevel.ToString("R", ci), Emg.Jump.MaxLevel.ToString("R", ci),
                Emg.Backflip.RestLevel.ToString("R", ci), Emg.Backflip.MaxLevel.ToString("R", ci)));
            PlayerPrefs.Save();
        }

        // ------------------------------------------------------------------ per frame

        protected override void Update()
        {
            // After a script reload during Play the runtime objects are gone: rebuild them.
            if (Settings == null || Cop == null || Emg == null) { Awake(); if (Settings == null) return; }
            HandleHotkeys();
            if (CopRunning) { if (copRecording != null) UpdateCopReplay(Time.deltaTime); else UpdateCopLive(Time.unscaledDeltaTime); }
            if (EmgRunning)
            {
                if (emgRecording != null) UpdateEmgReplay(Time.deltaTime);
                else if (bridge != null) UpdateEmgBridge();
                else UpdateEmgLiveFile();
            }
            UpdateCalibrationWizard(Time.unscaledDeltaTime);
            UpdateRates();
            base.Update();   // reads ReadSteer / ReadSpeedControl / ReadJumpRequest / ReadBackflipRequest
        }

        private void UpdateCopReplay(float dt)
        {
            copAccumulator += dt;
            int guard = 0;
            while (copAccumulator >= Cop.TickInterval && guard++ < 50)
            {
                copAccumulator -= Cop.TickInterval;
                copReplayTime += Cop.TickInterval;
                copRecording.Sample(copReplayTime, out double x, out double y, out double w);
                if (Settings.replayInvertX) x = -x;
                if (Settings.replayInvertY) y = -y;
                Cop.Tick(x, y, w);
            }
            if (guard >= 50) copAccumulator = 0;
            CopReceiving = true;
        }

        private void UpdateCopLive(float dt)
        {
            lines.Clear();
            copTail.ReadNewLines(lines);
            copSamples.Clear();
            foreach (string line in lines)
            {
                if (LineParser.Parse(line, values) == 0) continue;
                double x = LineParser.Get(values, Settings.liveColumnX);
                double y = LineParser.Get(values, Settings.liveColumnY);
                double w = Settings.liveColumnWeight >= 0 ? LineParser.Get(values, Settings.liveColumnWeight) : double.NaN;
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                if (Settings.liveInvertX) x = -x;
                if (Settings.liveInvertY) y = -y;
                copSamples.Add(new Vector3((float)x, (float)y, (float)w));
            }
            if (copSamples.Count > 0) { lastCopLineTime = Time.unscaledTime; copRateCount += copSamples.Count; }

            CopStale = Time.unscaledTime - lastCopLineTime > Settings.copStaleTimeout;
            CopReceiving = !CopStale;
            string name = System.IO.Path.GetFileName(copTail.Path) + (copSelfTest != null ? " (SELF-TEST)" : "");
            if (copTail.LastError != null) CopStatus = "Live file: " + copTail.LastError;
            else CopStatus = "Live " + name + (CopStale ? "  - no new data: run wiiuse.exe (Balance Board folder) and close WiiBalanceWalker / Recorder" : "");

            // Spread the samples that arrived this frame over the internal ticks of this frame.
            copAccumulator += dt;
            int ticks = 0;
            while (copAccumulator >= Cop.TickInterval && ticks < 50) { copAccumulator -= Cop.TickInterval; ticks++; }
            if (ticks >= 50) copAccumulator = 0;
            for (int i = 0; i < ticks; i++)
            {
                if (copSamples.Count > 0) copHold = copSamples[Mathf.Min(copSamples.Count - 1, i * copSamples.Count / ticks)];
                if (CopStale) Cop.Tick(double.NaN, double.NaN, 0);
                else Cop.Tick(copHold.x, copHold.y, float.IsNaN(copHold.z) ? double.NaN : copHold.z);
            }
            if (ticks == 0 && copSamples.Count > 0) copHold = copSamples[copSamples.Count - 1];
            if (Cop.HasNeutral) { carriedNeutral = true; carriedNeutralX = Cop.NeutralX; carriedNeutralY = Cop.NeutralY; }
        }

        private void UpdateEmgReplay(float dt)
        {
            int n = emgRecording.Count;
            double next = emgReplayPos + dt * Settings.emgSampleRate;
            int from = (int)emgReplayPos, to = (int)next;
            if (to - from > 5000) from = to - 5000;          // after a long hitch, skip ahead
            for (int i = from; i < to; i++)
            {
                int k = i % n;
                Emg.Process(emgRecording.Jump[k], emgRecording.Backflip[k]);
            }
            emgReplayPos = next;
            EmgReceiving = true;
        }

        private void UpdateEmgBridge()
        {
            int got = 0;
            while (bridge.TryDequeue(out var p))
            {
                // Rebuild the filters if the bridge reports a different sampling rate.
                if (p.sample_rate_hz > 1f && Mathf.Abs((float)Emg.SampleRate - p.sample_rate_hz) > 1f)
                {
                    var old = Emg;
                    Emg = new EmgProcessor(Settings, p.sample_rate_hz);
                    Emg.Jump.SetCalibration(old.Jump.RestLevel, old.Jump.MaxLevel);
                    Emg.Backflip.SetCalibration(old.Backflip.RestLevel, old.Backflip.MaxLevel);
                }
                EmgChannel ch = p.channel == Settings.bridgeJumpChannel ? Emg.Jump
                              : p.channel == Settings.bridgeBackflipChannel ? Emg.Backflip : null;
                if (ch == null) continue;
                if (p.samples != null && p.samples.Length > 0)
                {
                    for (int i = 0; i < p.samples.Length; i++) ch.Process(p.samples[i]);
                    got += p.samples.Length;
                }
                else
                {
                    ch.ProcessEnvelope(p.rms_mv, Mathf.Max(1, Mathf.RoundToInt((float)Emg.SampleRate * 0.05f)));   // kit bridge: RMS every 50 ms
                    got += 50;
                }
            }
            if (got > 0) { lastEmgDataTime = Time.unscaledTime; emgRateCount += got; }
            EmgReceiving = Time.unscaledTime - lastEmgDataTime < 0.5f;
            string lost = bridge.SamplesLost > 0 ? $", {bridge.SamplesLost} samples lost" : "";
            EmgStatus = EmgReceiving
                ? $"Bridge UDP {Settings.bridgePort}: receiving {EmgLiveRate / 2f:0} Hz/ch{lost}{(bridgeSelfTest != null ? " (SELF-TEST)" : "")}"
                : $"Bridge UDP {Settings.bridgePort}: waiting - start Tools/EmgBridge/run_bridge.bat and click Connect";
        }

        private void UpdateEmgLiveFile()
        {
            lines.Clear();
            emgTail.ReadNewLines(lines);
            int got = 0;
            foreach (string line in lines)
            {
                if (LineParser.Parse(line, values) == 0) continue;
                double a = LineParser.Get(values, Settings.jumpColumn), b = LineParser.Get(values, Settings.backflipColumn);
                if (double.IsNaN(a) || double.IsNaN(b)) continue;
                Emg.Process(a, b);
                got++;
            }
            if (got > 0) { lastEmgDataTime = Time.unscaledTime; emgRateCount += got * 2; }
            EmgReceiving = Time.unscaledTime - lastEmgDataTime < 0.5f;
            EmgStatus = emgTail.LastError != null ? "Live EMG file: " + emgTail.LastError
                      : "Live " + System.IO.Path.GetFileName(emgTail.Path) + (EmgReceiving ? "" : "  (no new data)");
        }

        private void UpdateRates()
        {
            float span = Time.unscaledTime - rateWindowStart;
            if (span < 1f) return;
            CopLiveRate = copRateCount / span;
            EmgLiveRate = emgRateCount / span;
            copRateCount = emgRateCount = 0;
            rateWindowStart = Time.unscaledTime;
        }

        // ------------------------------------------------------------------ guided EMG calibration (F4)

        public void StartCalibrationWizard()
        {
            if (!EmgRunning) return;
            Emg.Jump.StopRecording(); Emg.Backflip.StopRecording();
            Emg.Jump.StartRecording(); Emg.Backflip.StartRecording();
            CalPhase = CalibrationPhase.Relax;
            CalPhaseRemaining = Settings.calibRelaxSeconds;
            CalMessage = "";
        }

        private void UpdateCalibrationWizard(float dt)
        {
            if (CalPhase == CalibrationPhase.None) return;
            if (CalPhase == CalibrationPhase.Done)
            {
                calDoneTimer -= dt;
                if (calDoneTimer <= 0) CalPhase = CalibrationPhase.None;
                return;
            }
            CalPhaseRemaining -= dt;
            if (CalPhaseRemaining > 0) return;

            switch (CalPhase)
            {
                case CalibrationPhase.Relax:
                    calRelaxJump = Emg.Jump.StopRecording(); calRelaxFlip = Emg.Backflip.StopRecording();
                    Emg.Jump.StartRecording(); Emg.Backflip.StartRecording();
                    CalPhase = CalibrationPhase.FlexJump; CalPhaseRemaining = Settings.calibFlexSeconds;
                    break;
                case CalibrationPhase.FlexJump:
                    calFlexJump = Emg.Jump.StopRecording(); Emg.Backflip.StopRecording();
                    Emg.Jump.StartRecording(); Emg.Backflip.StartRecording();
                    CalPhase = CalibrationPhase.FlexBackflip; CalPhaseRemaining = Settings.calibFlexSeconds;
                    break;
                case CalibrationPhase.FlexBackflip:
                    Emg.Jump.StopRecording(); calFlexFlip = Emg.Backflip.StopRecording();
                    bool a = Emg.Jump.ApplyCalibration(EmgChannel.Percentile(calRelaxJump, 0.5), EmgChannel.Percentile(calFlexJump, 0.99));
                    bool b = Emg.Backflip.ApplyCalibration(EmgChannel.Percentile(calRelaxFlip, 0.5), EmgChannel.Percentile(calFlexFlip, 0.99));
                    if (a || b) { SaveCalibration(); EmgCalibratedForPlayer = true; }
                    CalMessage = (a && b) ? "EMG calibrated and saved" : (a || b) ? "Partly calibrated - see F2 panel; try F4 again" : "No clear contractions - check electrodes and try F4 again";
                    CalPhase = CalibrationPhase.Done; calDoneTimer = 3f;
                    Debug.Log("[SkiGame] " + CalMessage + "  |  " + Emg.Jump.LastCalibrationMessage + "  |  " + Emg.Backflip.LastCalibrationMessage);
                    break;
            }
        }

        private bool CalibrationRunning => CalPhase == CalibrationPhase.Relax || CalPhase == CalibrationPhase.FlexJump || CalPhase == CalibrationPhase.FlexBackflip;

        private void HandleHotkeys()
        {
            if (KeyDown(Settings.recalibrateCopKey))
            {
                Cop.BeginNeutralCalibration();
                carriedNeutral = false;
                Debug.Log("[SkiGame] COP: re-measuring the neutral stance - stand still for " + Settings.copNeutralSeconds + " s.");
            }
            if (KeyDown(Settings.recalibrateEmgKey)) StartCalibrationWizard();
            if (KeyDown(Settings.switchProfileKey))
            {
                carriedNeutral = false;
                StartProfile(!IsLiveProfile);
            }
        }

        // ------------------------------------------------------------------ IGameInput sources

        protected override float ReadSteer()
        {
            float k = KeyAxis(LeftKeys, RightKeys);
            keyboardSteering = k != 0f || !CopRunning;
            if (keyboardSteering) return k;
            return Cop.Steer;
        }

        protected override float ReadSpeedControl()
        {
            float k = KeyAxis(DownKeys, UpKeys);
            if (k != 0f || !CopRunning) return k;
            return Cop.Speed;
        }

        protected override bool ReadJumpRequest()
        {
            bool emg = EmgRunning && Emg.Jump.ConsumeRequest() && !CalibrationRunning;
            return emg || Key(KeyCode.Space);
        }

        protected override bool ReadBackflipRequest()
        {
            bool emg = EmgRunning && Emg.Backflip.ConsumeRequest() && !CalibrationRunning;
            return emg || Key(KeyCode.F);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            StopSources();
        }

        private void OnDestroy() => StopSources();

        // ------------------------------------------------------------------ keyboard (optional)

        private float KeyAxis(KeyCode[] negative, KeyCode[] positive)
        {
            if (!Settings.keyboardAlsoWorks) return 0f;
            float v = 0f;
            foreach (var k in positive) if (Key(k)) { v += 1f; break; }
            foreach (var k in negative) if (Key(k)) { v -= 1f; break; }
            return v;
        }

        private bool Key(KeyCode key)
        {
            if (keyboardBroken || !Settings.keyboardAlsoWorks) return false;
            try { return Input.GetKey(key); }
            catch (System.InvalidOperationException) { keyboardBroken = true; return false; }
        }

        private bool KeyDown(KeyCode key)
        {
            if (keyboardBroken || key == KeyCode.None) return false;
            try { return Input.GetKeyDown(key); }
            catch (System.InvalidOperationException) { keyboardBroken = true; return false; }
        }
    }

    /// <summary>
    /// Puts SensorInput on the Player whenever a scene loads (also after Restart), if
    /// SensorSettings.useSensors is on, and switches every other input (keyboard, EMG demo) off.
    /// No scene editing needed.
    /// </summary>
    public static class SensorBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var settings = Resources.Load<SensorSettings>("SensorSettings");
            if (settings == null || !settings.useSensors) return;
            var player = Object.FindAnyObjectByType<PlayerController>();
            if (player == null) return;

            foreach (var other in player.GetComponents<GameInputBase>())
                if (!(other is SensorInput)) other.enabled = false;

            var sensor = player.GetComponent<SensorInput>();
            if (sensor == null) sensor = player.gameObject.AddComponent<SensorInput>();
            sensor.enabled = true;
            player.SetInputSource(sensor);

            if (Object.FindAnyObjectByType<SensorDebugHUD>() == null)
                player.gameObject.AddComponent<SensorDebugHUD>();
        }
    }
}
