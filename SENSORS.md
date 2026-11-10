# Sensor control: EMG and Wii Balance Board

How the game is controlled by the body instead of the keyboard:

| Body signal | Sensor | Game control |
|---|---|---|
| Forearm flexor (FCU) contraction | EMG, BioRadio **Ch1** | **Jump** |
| Biceps contraction | EMG, BioRadio **Ch2** | **Backflip** (only while in the air) |
| Lean left / right | Wii Balance Board centre of pressure (COP), medio-lateral | **Steering** |
| Lean forward / back | Wii Balance Board COP, antero-posterior | **Speed** (forward = faster, back = brake) |

**v5.0 is the final, playable version.** EMG comes live from the BioRadio through the UDP bridge, and COP comes live from the board software's `WiiMote1_All.txt`. Both have replay modes for testing.

This document covers which filters are used and why, the numbers from analysing the recordings, how replay and live mode work, and how to tune everything. All filters run **causally**: they only use past samples, exactly as in real time. The analysis therefore measures the delays the player will actually feel.

The figures are in `Docs/figures/`. The analysis script is `Tools/SignalAnalysis/sensor_analysis.py`. It uses the same filter maths as the game, so you can re-run it on new recordings.

---

## 1. The data we analysed

| File | What it is | Rate | Length |
|---|---|---|---|
| `john_final.csv` (+ `.json`, also in `Assets/StreamingAssets`) | BioRadio EMG, 2 channels, in mV | 1000 Hz | 131.8 s, 63 Ch1 contractions + 57 Ch2 contractions, alternating every ~1 s |
| `John14.txt`, `John15.txt`, `John16.txt` | Wii Balance Board Recorder export: Time (s), CpX, CpY (cm), 4 corner forces (N), Weight (kg), raw counts | 5 Hz | 111 s, 74 s, 63 s. Leans left/right, then forward/back |
| `WiiMote1_All.txt` (live, 886 MB) | Written continuously by the board software. We read only its last 5,000 lines into `Data/live_sample.txt`. | **≈ 85 lines/s** | 10 tab-separated columns |

Format of the live file: column 4 = COP X, column 5 = COP Y, column 6 = total weight (kg), columns 7–9 = clock time (hh mm ss). This matches your professor's script, which reads columns 4 and 5. The sample was taken with nobody on the board: weight was 2.6–2.8 kg, and the COP stayed fixed at 26.05 / 16.75.

---

## 2. EMG: jump and backflip

### 2.1 What the recording showed

| Measurement | Ch1 (jump) | Ch2 (backflip) | Consequence |
|---|---|---|---|
| Power below 20 Hz (movement artefact, drift) | **32 %** | **50 %** | A high-pass at 20 Hz is essential. Without it, every arm movement would look like a contraction. |
| Median frequency of the EMG band | 83 Hz | 57 Hz | Normal surface EMG, well inside 20–450 Hz |
| 50 / 100 / 150 Hz mains peaks (vs. neighbouring frequencies) | 1.7× / 1.2× / 1.5× | 1.5× / 1.1× / 1.6× | Very little mains hum in this lab. The 50 Hz notch is kept as cheap insurance for other rooms. |
| DC offset | −0.05 mV | −0.79 mV | The high-pass removes it. The filter is primed with the first sample so start-up doesn't fire a false jump. |
| Envelope at rest / during contraction | 0.0049 / 0.229 mV | 0.0036 / 0.566 mV | Large signal-to-noise ratio, so a threshold works well |
| Small activity before each Ch2 contraction | — | up to ~25–35 % of a full contraction | **Ch2 needs a higher ON threshold (0.40)** than Ch1 (0.15) |

### 2.2 The EMG filter chain, run on every sample at 1000 Hz

```
raw EMG (mV)
 1. High-pass  : 4th-order Butterworth, 20 Hz     (two biquads, Q = 0.5412 and 1.3066)
 2. Low-pass   : 4th-order Butterworth, 450 Hz    (two biquads, Q = 0.5412 and 1.3066)
 3. Notch      : 50 Hz, Q = 30                     (≈ 1.7 Hz wide)
 4. Rectify    : full-wave, |x|
 5. Envelope   : 2nd-order Butterworth low-pass, 5 Hz   (Q = 0.7071)
 6. Normalise  : n = (envelope − rest) / (contraction − rest)
 7. Detect     : hysteresis per muscle, ON must last ≥ 20 ms
                   jump (forearm):  ON 0.15, OFF 0.08
                   backflip (biceps): ON 0.40, OFF 0.10
 8. Game       : ON edge → one jump / backflip (cooldown 0.25 s / 0.30 s, 0.13 s input buffer, 0.12 s coyote time)
```

All filters are IIR biquads designed with the bilinear transform (RBJ cookbook formulas), so cascading them gives exact Butterworth responses. See `emg_filters.png`.

### 2.3 John's v4 filters vs. the final choice

John's v4 used:
- a 20–450 Hz band-pass, then rectification;
- a **5 Hz** envelope;
- absolute thresholds at 30 % / 12 % of the 20th–95th percentile range;
- a 0.6 s refractory period.

My first version used an 8 Hz envelope with thresholds of 0.30 / 0.40. All variants were scored on `john_final.csv` against the contractions found offline:

| Version | Jump: detected / false / delay* | Backflip: detected / false / delay* |
|---|---|---|
| John's v4 | 63/63 · 0 · 48 ms | 56/57 · **9 false** · fires on the small activity before each contraction |
| My first version (8 Hz envelope) | 63/63 · 0 · 88 ms | 57/57 · 2 false · 159 ms |
| **Final: John's 5 Hz envelope + per-muscle thresholds** | **63/63 · 0 · 29 ms** | **57/57 · 0 false · 159 ms** |

\* Delay = how long after the contraction starts the jump fires (start = 30 % of that contraction's peak on the offline envelope).

So the final version keeps John's faster 5 Hz envelope. It adds per-muscle normalised thresholds, a low threshold for the clean forearm signal and a higher one for the biceps, plus the notch and filter priming. The jump now fires **165 ms before the contraction peaks**.

A grid search tried envelope cut-offs of 5 and 8 Hz, calibration at the 95th and 99th percentile, ON thresholds from 0.10 to 0.50, OFF thresholds from 0.06 to 0.15, and minimum ON times of 20 and 30 ms. Each was calibrated on the first 30 s and tested on the remaining 100 s. These choices were the fastest with no errors, and they keep a safety margin: the jump threshold first gives a false trigger at 0.10, and the backflip at 0.35.

### 2.4 Calibration (each player is different)

- **Live: press F4 on the main menu.** Follow the big on-screen prompts:
  - **RELAX BOTH ARMS** (4 s);
  - **FLEX FOREARM** 3 times (6 s);
  - **FLEX BICEPS** 3 times (6 s).

  Rest is the median of the relax phase; contraction is the 99th percentile of each muscle's own flex phase. The result is **saved** and reused next time. Press F4 again for a new player.
  - Until someone calibrates, the status bar says "EMG not calibrated - press F4", and values from `john_final.csv` are used.
  - If no clear contraction is found (peak less than 3× rest), the old values are kept.
- **Replay:** the first 30 s of the recording are used automatically.

### 2.5 Getting live EMG into the game: the bridge

```
BioRadio --Bluetooth--> BioRadioSDK.dll --> Tools/EmgBridge/ski_emg_bridge.py --UDP 127.0.0.1:50557--> game
```

The kit's `bioradio_unity_bridge.py` sends only the **last sample and the RMS of each 50 ms batch**. That RMS isn't filtered: on Ch2 it is dominated by the −0.79 mV offset and the movement artefact. So I wrote `ski_emg_bridge.py`, based on the kit's script with the same SDK calls and the same Scan / Connect window. It changes two things:

1. It sends **every sample**, in a `samples` array, every 20 ms, so the game runs the full filter chain above.
2. It redraws the plot separately, every 200 ms, so drawing never delays the data.

It still sends the kit's fields (`channel`, `raw_mv`, `rms_mv`, `sample_count`, `sample_rate_hz`), so the kit's `BioRadioUdpMover.cs` keeps working.

The game also accepts the **kit's original bridge**. In that case it uses `rms_mv` directly as the envelope; this works, but rejects noise less well.

The SDK DLL is not copied into the repository, because of the vendor licence. The bridge finds it in the kit's `Python_Bridge` folder in Downloads, or you can pass `--sdk`.

---

## 3. COP (Wii Balance Board): steering and speed

### 3.1 What the recordings showed

| Measurement | John14 | John15 | John16 | Consequence |
|---|---|---|---|---|
| Neutral stance (X / Y cm) | −1.0 / −1.6 | −0.8 / −1.1 | −1.7 / −1.4 | Not zero and different every time, so **measure neutral at the start** |
| Sway while standing still, SD (cm) | 0.8 / 0.6 | 0.1 / 0.3 | 0.15 / 0.3 | **Dead zone 0.8 cm** on steering |
| Sample-to-sample noise (cm) | 1.0 / 0.4 | 0.24 / 0.25 | 0.35 / 0.22 | Needs a low-pass |
| Typical full lean, 95th percentile (cm) | 12.7 / 7.2 | 13.3 / 8.0 | 14.3 / 7.0 | **Full steer at 10 cm**, full speed at 6 cm |
| Fastest lean change (cm/s) | 57 | 20 | 26 | Voluntary leans are slow (< ~2 Hz), so a 3 Hz low-pass keeps them |
| Cross-talk | Leaning sideways moves Y back by 1.5–2 cm. Leaning forward/back moves X by up to ±3–4 cm. | | | **Dead zone 1.8 cm on speed**, so sideways leans don't brake |

### 3.2 The COP filter chain, run on an internal 100 Hz clock

```
raw COP X, Y (cm), weight (kg)
 1. Resample    : onto a fixed 100 Hz clock (replay: linear interpolation of the 5 Hz file;
                  live: the ~85 Hz lines that arrived this frame are spread over this frame's ticks)
 2. Validity    : NaN or "-1.#IND00" rejected. Weight < 20 kg = nobody on the board → output glides to neutral.
                  Live file silent for > 0.5 s → neutral.
 3. Glitches    : a jump of > 6 cm in one sample is ignored (at most 3 in a row)
 4. Low-pass    : 2nd-order Butterworth, 3 Hz (Q = 0.7071), one per axis
 5. Neutral     : subtract the mean of the first 2 s with someone on the board (F3 re-measures it)
 6. Scale       : steer = X / 10 cm, speed = Y / 6 cm, clamped to −1..+1
 7. Game        : dead zone 0.08 (steer) / 0.30 (speed), ramp 0.05 s (steer) / 0.15 s (speed),
                  then steering turns the skier at up to 80 °/s (see CONTROLS.md)
```

`cop_processing.png` shows raw COP against the game input for all three recordings. `cop_filter.png` shows the filter's response.

### 3.3 Why these values

- **3 Hz low-pass, 2nd-order Butterworth:**
  - It passes the voluntary leans unchanged, removes sway jitter and the occasional noisy sample, and delays the signal by only **77 ms** (group delay at 0.5 Hz).
  - A lower cut-off (1–2 Hz) would feel laggy. A higher one passes more sway into the steering.
- **100 Hz internal clock:** the same filter then behaves the same on the 5 Hz recordings and the 85 Hz live file.
- **Neutral measured each session:** the stance centre varied by 1 cm between recordings.
- **10 cm full scale:** people reached 12–14 cm, so full steering doesn't require a maximum lean. Steering was at full lock 20–26 % of the time in these recordings.
- **Game-side ramp reduced from 0.12 s to 0.05 s:** the 3 Hz low-pass already smooths, so stacking both would add needless lag.

### 3.4 Live file: how it is read (read-only)

- `WiiMote1_All.txt` is opened with **read access only**, sharing read/write/delete with the program writing it. The game never writes, renames or locks it.
- Reading starts at the **end** of the file, so the 886 MB of history is skipped. Each frame only the newly added bytes are read, about 85 lines/s × ~75 bytes ≈ 6 KB/s.
- If the file is restarted or truncated, the reader starts again from the beginning. If the game falls more than 256 KB behind, it jumps close to the end.
- **Left/right sign:** checked on the real board: +X in the live file means **right**, so `liveInvertX` is off.

---

## 4. Total delay from the body to the game

| Stage | EMG jump / backflip | COP steering |
|---|---|---|
| Sensor / software | BioRadio Bluetooth + SDK buffer (unknown, typically 20–50 ms) | ~12 ms (85 Hz file) |
| Bridge | ≤ 20 ms (send period) + < 1 ms UDP | — |
| Filtering + detection | measured: **29 ms** (jump) / **159 ms** (backflip) after the contraction starts | low-pass 77 ms + 0.05 s ramp (≈ 25 ms average) |
| Game frame | ≤ 17 ms | ≤ 17 ms |

The game's 0.12 s coyote time and 0.13 s input buffer cover most of the EMG delay: a jump triggered slightly after going over a ramp lip still counts.

---

## 5. How to play with EMG and the balance board

**Before the session:**
1. Turn on the BioRadio and check the channels in BioCapture: **Ch1 = forearm (jump), Ch2 = biceps (backflip)**. Then **close BioCapture**.
2. Pair the board, then **double-click `wiiuse.exe`** in the `Balance Board` folder. This is the program that writes `WiiMote1_All.txt` (about 85 lines/s) into the folder it runs from.
   - Close WiiBalanceWalker and the Wii Balance Board Recorder first: only one program can use the board at a time.
   - The Recorder only writes its own `NameXX.txt` files, at 5 Hz, when you press F1. It does **not** feed the game.
3. Double-click **`Tools/EmgBridge/run_bridge.bat`**, then click **Scan**, choose your BioRadio, and click **Connect**. Leave this window open.
   - First time only: `py -m pip install pythonnet matplotlib`.
4. Open the project in Unity and press **Play**.

**In the game:**
5. Check the status bar at the bottom-left: **LIVE · EMG ● bridge · COP ● board**. Green means data is arriving.
6. Step on the board and **stand still** until the "STAND STILL" message disappears (2 s). This finds your centre.
7. Press **F4** on the menu and follow the calibration prompts (16 s).
8. Press **Enter** to start:
   - lean left or right to steer, forward or back to speed up or brake;
   - squeeze your forearm to jump;
   - curl your biceps in the air to backflip.

**Keys:**

| Key | Does |
|---|---|
| **F2** | Sensor panel: COP raw / filtered / centre, weight, steer and speed bars, both EMG envelopes against their thresholds, a 6 s scrolling plot |
| **F3** | Re-centre the board (stand still for 2 s) |
| **F4** | Guided EMG calibration (saved per computer) |
| **F6** | Switch **LIVE ↔ REPLAY**. Replay plays the recordings `john_final.csv` and `John14.txt`, so you can demo the game without hardware. |
| Keyboard | Still works alongside the sensors (A/D, W/S, Space, F) |

**Testing without hardware:** `Tools/EmgBridge/run_bridge_replay_test.bat` streams the recorded EMG through the real UDP path. Alternatively, in `Assets/Resources/SensorSettings.asset`, tick `Bridge Self Test` and/or `Cop Live Self Test`. Unity then feeds the recordings through the live paths itself, and **never touches the real WiiMote file**. Untick them before playing for real.

**Settings** are in `Assets/Resources/SensorSettings.asset`:

| What you want | Set |
|---|---|
| Live (default) | `Cop Source = LiveFile`, `Emg Source = LiveBridge` |
| A different board file | `Cop Live File` (opened read-only) |
| Channels swapped | `Bridge Jump Channel` / `Bridge Backflip Channel` |
| Keyboard only | untick `Use Sensors` |

---

## 6. Still to confirm with someone on the board

1. **Live COP direction.** Left/right was checked on the board (`Live Invert X` off). Lean forward: Speed should go **+**. If it goes −, tick `Live Invert Y`.
2. **Live COP scale.** If a comfortable lean doesn't reach ±1, lower `Steer Full Scale Cm`. The live software may use a different cm scale from the Recorder.
3. **EMG units.** The kit multiplies the SDK values by 1000 (V → mV), as in `john_final.json`. Calibration (F4) makes the game independent of the exact scale anyway.

---

## 7. Every parameter (defaults)

| Group | Parameter | Value |
|---|---|---|
| EMG | high-pass / low-pass / notch | 20 Hz BW4 / 450 Hz BW4 / 50 Hz Q30 |
| EMG | envelope | rectify + 5 Hz BW2 |
| EMG | jump ON / OFF | 0.15 / 0.08 |
| EMG | backflip ON / OFF | 0.40 / 0.10 |
| EMG | minimum ON time | 20 ms |
| EMG | calibration | live: F4 guided (relax median, flex 99th percentile), saved · replay: first 30 s, 10th / 99th percentile |
| EMG | live input | UDP 127.0.0.1:50557 from ski_emg_bridge.py (every sample, 20 ms batches) |
| COP | processing rate | 100 Hz |
| COP | low-pass | 3 Hz BW2 |
| COP | glitch limit | 6 cm per sample (max 3 in a row) |
| COP | minimum weight | 20 kg |
| COP | stale timeout | 0.5 s |
| COP | neutral | mean of the first 2 s |
| COP | steering | dead zone 1.5 cm, full turn at 15 cm, response curve ^1.7 (5 cm lean ≈ 10 % turn, 10 cm ≈ 45 %) |
| COP | speed (forward/back) | dead zone 2.5 cm, full at 8 cm, response curve ^1.7 (4 cm ≈ 11 %, 6 cm ≈ 46 %), 0.4 s ramp |
| Game | steer ramp / release | 0.05 s / 0.04 s |
| Game | speed ramp | 0.4 s |

Re-run the analysis on new recordings with:

```
python Tools/SignalAnalysis/sensor_analysis.py --emg SensorData/EMG/<file>.csv --cop SensorData/COP/<file>.txt --out Docs/figures
```
