using System;

namespace SkiGame.Sensors
{
    /// <summary>
    /// Real-time processing of the Wii Balance Board centre of pressure (COP), run on a fixed
    /// internal clock (default 100 Hz) so it behaves the same for the 5 Hz recordings and the
    /// ~85 Hz live file:
    ///
    ///   raw COP (cm) -> validity gate: NaN / "-1.#IND" rejected, total weight below 20 kg = nobody on the board
    ///                -> glitch rejection: a jump of more than 6 cm in one sample is ignored (max 3 in a row)
    ///                -> 2nd-order Butterworth low-pass 3 Hz (keeps voluntary leans, removes jitter)
    ///                -> subtract the neutral stance (mean of the first 2 s standing still; F3 re-does it)
    ///                -> steering: 1.5 cm dead zone, full turn at 15 cm, response curve ^1.7; speed: 2.5 cm dead zone, full at 8 cm, curve ^1.7; clamp to -1..1
    ///
    /// Then GameInputBase adds the dead zone and the short response ramp.
    /// </summary>
    public sealed class CopProcessor
    {
        public double RawX { get; private set; }
        public double RawY { get; private set; }
        public double Weight { get; private set; }
        public double FilteredX { get; private set; }
        public double FilteredY { get; private set; }
        public double NeutralX { get; private set; }
        public double NeutralY { get; private set; }
        public bool HasNeutral { get; private set; }
        public bool PersonOnBoard { get; private set; }
        public int GlitchesRejected { get; private set; }
        public float NeutralProgress => neutralTarget > 0 ? (float)neutralCount / neutralTarget : (HasNeutral ? 1f : 0f);

        /// <summary>Steering, -1 (full left) .. +1 (full right).</summary>
        public float Steer { get; private set; }
        /// <summary>Speed control, -1 (full brake / lean back) .. +1 (full boost / lean forward).</summary>
        public float Speed { get; private set; }

        private readonly SensorSettings s;
        private readonly double dt;
        private FilterChain lpX, lpY;
        private bool primed;
        private double lastX, lastY;
        private int glitchRun;
        private int neutralTarget, neutralCount;
        private double neutralSumX, neutralSumY;

        public double TickInterval => dt;

        public CopProcessor(SensorSettings settings)
        {
            s = settings;
            double fs = Math.Max(20, s.copProcessRate);
            dt = 1.0 / fs;
            lpX = FilterChain.Butterworth2LowPass(fs, s.copLowPassHz);
            lpY = FilterChain.Butterworth2LowPass(fs, s.copLowPassHz);
            BeginNeutralCalibration();
        }

        public void BeginNeutralCalibration()
        {
            neutralTarget = Math.Max(1, (int)Math.Round(s.copNeutralSeconds / dt));
            neutralCount = 0;
            neutralSumX = neutralSumY = 0;
        }

        /// <summary>One tick of the internal clock with the current raw reading (already sign-corrected).</summary>
        public void Tick(double x, double y, double weight)
        {
            bool valid = !double.IsNaN(x) && !double.IsNaN(y);
            Weight = double.IsNaN(weight) ? 0 : weight;
            PersonOnBoard = valid && (double.IsNaN(weight) || weight >= s.copMinWeightKg);

            if (PersonOnBoard)
            {
                RawX = x; RawY = y;
                if (!primed)
                {
                    lpX.Prime(x); lpY.Prime(y);
                    lastX = x; lastY = y; primed = true;
                }
                // Glitch rejection (single bad samples from the Bluetooth link).
                if ((Math.Abs(x - lastX) > s.copMaxJumpCm || Math.Abs(y - lastY) > s.copMaxJumpCm) && glitchRun < 3)
                {
                    glitchRun++;
                    GlitchesRejected++;
                    x = lastX; y = lastY;
                }
                else glitchRun = 0;
                lastX = x; lastY = y;
            }
            else
            {
                // Nobody on the board (or no data): glide back to the neutral position.
                x = HasNeutral ? NeutralX : lastX;
                y = HasNeutral ? NeutralY : lastY;
            }

            FilteredX = lpX.Process(x);
            FilteredY = lpY.Process(y);

            if (neutralTarget > 0 && PersonOnBoard)
            {
                neutralSumX += FilteredX; neutralSumY += FilteredY;
                if (++neutralCount >= neutralTarget)
                {
                    NeutralX = neutralSumX / neutralCount;
                    NeutralY = neutralSumY / neutralCount;
                    HasNeutral = true;
                    neutralTarget = 0;
                }
            }

            if (!HasNeutral || !PersonOnBoard && !primed)
            {
                Steer = Speed = 0f;
                return;
            }
            // Dead zone in cm, then a response curve: small leans steer gently, a big lean still gives full turn.
            double dx = FilteredX - NeutralX;
            double mag = Math.Max(0, Math.Abs(dx) - s.steerDeadZoneCm) / Math.Max(0.1, s.steerFullScaleCm - s.steerDeadZoneCm);
            Steer = Clamp(Math.Sign(dx) * Math.Pow(Math.Min(1.0, mag), Math.Max(1.0, s.steerCurve)));
            double dy = FilteredY - NeutralY;
            double magY = Math.Max(0, Math.Abs(dy) - s.speedDeadZoneCm) / Math.Max(0.1, s.speedFullScaleCm - s.speedDeadZoneCm);
            Speed = s.copControlsSpeed ? Clamp(Math.Sign(dy) * Math.Pow(Math.Min(1.0, magY), Math.Max(1.0, s.speedCurve))) : 0f;
        }

        /// <summary>Uses a known neutral position (e.g. carried over a Restart) instead of measuring it.</summary>
        public void SetNeutral(double x, double y)
        {
            NeutralX = x; NeutralY = y;
            HasNeutral = true;
            neutralTarget = 0;
        }

        /// <summary>Neutral output (used when the live file goes quiet).</summary>
        public void ForceNeutral() { Steer = Speed = 0f; PersonOnBoard = false; }

        private static float Clamp(double v) => (float)Math.Max(-1.0, Math.Min(1.0, v));
    }
}
