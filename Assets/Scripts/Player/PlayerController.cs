using UnityEngine;

namespace SkiGame
{
    /// <summary>
    /// Skier gameplay: movement, ground/air handling, jump, backflip, landing check,
    /// collisions (obstacles + coins) and points for airborne coins.
    ///
    /// WHY A CUSTOM KINEMATIC CONTROLLER (no Rigidbody):
    ///  - Jump airtime and flip timing must be exact and repeatable, so a delayed EMG
    ///    trigger always behaves the same. Rigidbody physics adds friction, bounces and
    ///    solver jitter on a procedural mesh slope.
    ///  - Input buffer, coyote time and a generous landing tolerance are trivial to
    ///    implement when we own the vertical motion.
    ///  - Speed is a single number we clamp between min and max - no fighting the physics engine.
    /// Ground is found with a downward raycast (slope chunks and ramps on layer Default);
    /// obstacles and coins are found with an overlap capsule (layer Ignore Raycast).
    ///
    /// Vertical motion is computed as "height above the slope plane", so a jump lasts
    /// 2 * jumpVelocity / gravity regardless of speed or slope steepness.
    ///
    /// The controller reads ONLY IGameInput and talks to the model ONLY via ISkierVisual.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private SkierSettings settings;
        [SerializeField] private GameRulesSettings rules;
        [SerializeField] private SlopeGenerator slope;
        [Tooltip("Any component that implements IGameInput (KeyboardInput now, an EMG/COP input later). " +
                 "If empty, the first enabled IGameInput on this GameObject is used.")]
        [SerializeField] private MonoBehaviour inputSource;
        [Tooltip("Optional component implementing ISkierVisual. If empty, the first ISkierVisual in the children is used.")]
        [SerializeField] private MonoBehaviour visualComponent;
        [Tooltip("Log every take-off / landing (velocity, height, airtime) to the Console - handy when tuning ramps and jumps.")]
        [SerializeField] private bool logAirtime = false;
        private float airStartTime;
        private bool onRampLastFrame;
        private bool airFromRamp;

        // ------------------------------------------------------------------ public state
        public IGameInput InputSource => input;
        public ISkierVisual Visual => visual;
        public SkierSettings Settings => settings;
        /// <summary>Set by the GameManager. False in menu / pause / game over.</summary>
        public bool ControlsEnabled { get; set; }
        /// <summary>Set by the DifficultyManager.</summary>
        public float MaxSpeedMultiplier { get; set; } = 1f;
        public float Speed { get; private set; }
        public float Speed01 => Mathf.InverseLerp(settings.minSpeed, settings.maxSpeed * MaxSpeedMultiplier, Speed);
        public bool IsGrounded => grounded;
        public bool IsFlipping => flipping;
        public float FlipProgress => flipping ? flipT : 0f;
        public bool IsStunned => stunTimer > 0f;
        public bool IsInvulnerable => invulnerableTimer > 0f;
        /// <summary>Heading in degrees from straight downhill (+Z). Positive = right.</summary>
        public float Heading => yaw;
        public float DistanceTravelled => transform.position.z - startZ;
        public float HeightAboveGround => grounded ? 0f : Mathf.Max(0f, localHeight - lastGroundHeight);
        public int FlipsThisAirtime => flipsThisAir;
        public int PendingAirPoints => pendingAirPoints;
        /// <summary>Multiplier that will apply to the current airtime (x2 after a completed flip).</summary>
        public int CurrentMultiplier => (!grounded && flipsThisAir > 0) ? rules.backflipMultiplier : 1;
        public bool InCoyoteTime => !grounded && !jumpedThisAir && Time.time - lastGroundedTime <= settings.coyoteTime;
        public float JumpBufferRemaining => Mathf.Max(0f, settings.inputBufferTime - (Time.time - lastJumpPressTime));
        public float FlipBufferRemaining => Mathf.Max(0f, settings.inputBufferTime - (Time.time - lastFlipPressTime));

        // ------------------------------------------------------------------ internals
        private IGameInput input;
        private ISkierVisual visual;

        private bool grounded = true;
        private bool flipping;
        private bool jumpedThisAir;
        private float yaw;
        private float localHeight;       // height above the slope plane (m)
        private float verticalVelocity;  // along world up, relative to the slope plane (m/s)
        private float lastGroundHeight;
        private float startZ;
        private float lastGroundedTime = -999f, lastJumpPressTime = -999f, lastFlipPressTime = -999f, lastRampTime = -999f;
        private float flipT;
        private float stunTimer, invulnerableTimer;
        private bool wasInvulnerable;
        private int flipsThisAir;
        private int pendingAirPoints;
        private float steerValue, speedValue;
        private Ramp lastRamp;
        private Vector3 groundNormal = Vector3.up;
        private Vector3 moveDirection = Vector3.forward;
        private readonly Collider[] overlapBuffer = new Collider[32];

        // ------------------------------------------------------------------ setup
        private void Awake()
        {
            if (settings == null) settings = ScriptableObject.CreateInstance<SkierSettings>();
            if (rules == null) rules = ScriptableObject.CreateInstance<GameRulesSettings>();
            if (slope == null) slope = FindAnyObjectByType<SlopeGenerator>();
            ResolveInput();
            ResolveVisual();
        }

        private void Start() => PlaceAtStart();

        private void ResolveInput()
        {
            input = inputSource as IGameInput;
            if (inputSource != null && input == null)
                Debug.LogError($"[SkiGame] {inputSource.GetType().Name} does not implement IGameInput.", this);
            if (input == null)
            {
                foreach (MonoBehaviour mb in GetComponents<MonoBehaviour>())
                {
                    if (mb is IGameInput candidate && mb.enabled) { input = candidate; inputSource = mb; break; }
                }
            }
            if (input == null)
            {
                Debug.LogWarning("[SkiGame] No IGameInput found on the Player - adding KeyboardInput.", this);
                var kb = gameObject.AddComponent<KeyboardInput>();
                input = kb;
                inputSource = kb;
            }
        }

        private void ResolveVisual()
        {
            visual = visualComponent as ISkierVisual;
            if (visual == null) visual = GetComponentInChildren<ISkierVisual>();
            if (visual == null)
            {
                Debug.LogWarning("[SkiGame] No ISkierVisual found in the Player's children - the skier will be invisible.", this);
                visual = new NullSkierVisual();
            }
        }

        /// <summary>Swap the input source at runtime (e.g. from a calibration menu).</summary>
        public void SetInputSource(IGameInput newInput)
        {
            if (newInput == null) return;
            input = newInput;
            inputSource = newInput as MonoBehaviour;
        }

        private void PlaceAtStart()
        {
            float x = slope != null ? slope.GetCenterX(0f) : 0f;
            float y = slope != null ? slope.GetSurfaceHeight(x, 0f) : 0f;
            transform.position = new Vector3(x, y, 0f);
            Vector3 up = slope != null ? slope.SlopeNormal : Vector3.up;
            transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(Vector3.forward, up), up);
            startZ = transform.position.z;
            Speed = settings.startSpeed;
            yaw = 0f;
            localHeight = 0f;
            grounded = true;
            lastGroundedTime = Time.time;
            visual.SetOnGround(true);
        }

        // ------------------------------------------------------------------ main loop
        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            if (!ControlsEnabled || slope == null)
            {
                // Menu / game over: stand still, relaxed pose.
                visual.SetLean(0f);
                visual.SetTuck(0f);
                visual.SetSpeed01(0f);
                return;
            }

            stunTimer = Mathf.Max(0f, stunTimer - dt);
            invulnerableTimer = Mathf.Max(0f, invulnerableTimer - dt);
            if (IsInvulnerable != wasInvulnerable) { wasInvulnerable = IsInvulnerable; visual.SetInvulnerable(wasInvulnerable); }

            ReadInput();

            Vector3 pos = transform.position;
            UpdateSpeed(dt);
            UpdateHeadingAndMove(ref pos, dt);
            UpdateVertical(ref pos, dt);
            HandleBufferedActions();
            pos.y = slope.GetBaseHeight(pos.z) + localHeight;

            transform.position = pos;
            UpdateOrientation(dt);
            DetectOverlaps(pos);
            UpdateVisual();
        }

        private void ReadInput()
        {
            bool blocked = IsStunned;
            steerValue = blocked ? 0f : Mathf.Clamp(input.Steer, -1f, 1f);
            speedValue = blocked ? 0f : Mathf.Clamp(input.SpeedControl, -1f, 1f);
            if (blocked) return;
            // Triggers are stored with a timestamp -> input buffer.
            if (input.JumpTriggered) lastJumpPressTime = Time.time;
            if (input.BackflipTriggered) lastFlipPressTime = Time.time;
        }

        // ------------------------------------------------------------------ speed & steering
        private void UpdateSpeed(float dt)
        {
            float max = settings.maxSpeed * MaxSpeedMultiplier;
            float accel = settings.downhillAcceleration;
            if (grounded)
            {
                accel += speedValue >= 0f ? speedValue * settings.boostAcceleration : speedValue * settings.brakeDeceleration;
                accel -= settings.carveDrag * Mathf.Abs(steerValue);
            }
            else
            {
                accel *= settings.airAccelerationFactor;
            }
            if (IsStunned) accel = -settings.brakeDeceleration * 0.5f;
            Speed = Mathf.Clamp(Speed + accel * dt, settings.minSpeed, max);
        }

        private void UpdateHeadingAndMove(ref Vector3 pos, float dt)
        {
            float authority = grounded ? 1f : settings.airSteerFactor;
            float pathYaw = slope.GetPathYaw(pos.z);
            if (IsStunned)
            {
                // After a crash the skier is straightened out along the track.
                yaw = Mathf.MoveTowards(yaw, pathYaw, settings.turnRate * dt);
            }
            else
            {
                if (input is IDirectSteering direct && direct.SteerSetsHeading)
                {
                    // DIRECT STEERING (balance board): the lean IS the direction, continuously.
                    // Centred = straight downhill; lean right = angled right, proportional to the lean.
                    float target = steerValue * settings.maxHeadingAngle;
                    yaw = Mathf.MoveTowards(yaw, target, direct.HeadingFollowRate * authority * dt);
                }
                else
                {
                    // GRADUAL STEERING (keyboard): input sets the TURN RATE, not the heading.
                    // Hold right -> keep turning right; let go -> keep the current heading.
                    yaw += steerValue * settings.steerTurnRate * authority * dt;
                }
            }
            // Never ski sideways or uphill: limit the heading relative to straight downhill.
            yaw = Mathf.Clamp(yaw, -settings.maxHeadingAngle, settings.maxHeadingAngle);

            // Soft boundary: on the banks you slow down and get turned back toward the path.
            float lateral = slope.LateralOffset(pos);
            float over = Mathf.Abs(lateral) - slope.HalfWidth;
            if (over > 0f)
            {
                float t = Mathf.Clamp01(over / Mathf.Max(0.01f, slope.BankWidth));
                Speed = Mathf.Max(settings.minSpeed, Speed - settings.bankFriction * t * dt);
                yaw = Mathf.MoveTowards(yaw, pathYaw - Mathf.Sign(lateral) * settings.maxHeadingAngle, settings.bankPushTurnRate * t * dt);
            }

            float rad = yaw * Mathf.Deg2Rad;
            moveDirection = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            pos += moveDirection * (Speed * dt);

            // Hard limit far up the bank.
            float limit = slope.HalfWidth + slope.BankWidth * settings.hardBoundaryFraction;
            float center = slope.GetCenterX(pos.z);
            float newLateral = pos.x - center;
            if (Mathf.Abs(newLateral) > limit) pos.x = center + Mathf.Sign(newLateral) * limit;
        }

        // ------------------------------------------------------------------ vertical: ground / air
        private void UpdateVertical(ref Vector3 pos, float dt)
        {
            float baseY = slope.GetBaseHeight(pos.z);
            float groundH = SampleGroundHeight(pos, baseY, out Vector3 normal, out Ramp ramp);
            lastGroundHeight = groundH;

            if (grounded)
            {
                float drop = localHeight - groundH;
                // Snap distance grows a little with speed (to follow bank curves at high speed)
                // but is CAPPED so it can never swallow a ramp lip, even at a low frame rate.
                float snap = settings.groundSnapDistance + Mathf.Min(Speed * dt * 0.8f, 0.45f);
                // RAMP FIX: we were on a ramp a moment ago and the ground just dropped away
                // (we passed the lip) -> always launch, independent of frame rate.
                bool leftRampLip = ramp == null && onRampLastFrame && drop > 0.25f;
                if (drop <= snap && !leftRampLip)
                {
                    // Stay glued to the ground; remember its vertical speed (needed for ramp launches).
                    verticalVelocity = Mathf.Clamp((groundH - localHeight) / dt, -30f, 30f);
                    localHeight = groundH;
                    groundNormal = normal;
                    if (ramp != null) { lastRamp = ramp; lastRampTime = Time.time; }
                    onRampLastFrame = ramp != null;
                    lastGroundedTime = Time.time;
                }
                else
                {
                    // Ground fell away (ramp lip, bank edge) -> airborne.
                    // Frame-based (not time-based) so ramps launch at any frame rate.
                    LeaveGround(onRampLastFrame ? lastRamp : null);
                    onRampLastFrame = false;
                }
            }
            else
            {
                verticalVelocity -= settings.gravity * dt;
                localHeight += verticalVelocity * dt;

                if (flipping)
                {
                    flipT += dt / settings.flipDuration;
                    if (flipT >= 1f)
                    {
                        // One full rotation done while still in the air.
                        flipping = false;
                        flipT = 0f;
                        flipsThisAir++;
                    }
                }

                // Generous landing check: falling and within landingTolerance of the ground.
                if (verticalVelocity <= 0f && localHeight <= groundH + settings.landingTolerance)
                {
                    localHeight = groundH;
                    groundNormal = normal;
                    Land();
                }
            }
        }

        private float SampleGroundHeight(Vector3 pos, float baseY, out Vector3 normal, out Ramp ramp)
        {
            var origin = new Vector3(pos.x, pos.y + settings.groundProbeUp, pos.z);
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, settings.groundProbeUp + settings.groundProbeDown,
                    GameLayers.GroundMask, QueryTriggerInteraction.Ignore))
            {
                normal = hit.normal;
                ramp = hit.collider.GetComponentInParent<Ramp>();
                return hit.point.y - baseY;
            }
            // Fallback: analytic slope height (e.g. before a chunk exists).
            normal = slope.SlopeNormal;
            ramp = null;
            return slope.GetSurfaceHeight(pos.x, pos.z) - baseY;
        }

        private void LeaveGround(Ramp launchRamp)
        {
            grounded = false;
            jumpedThisAir = false;
            flipsThisAir = 0;
            pendingAirPoints = 0;
            airFromRamp = launchRamp != null;
            float groundVelocity = verticalVelocity;
            if (launchRamp != null) verticalVelocity = Mathf.Max(verticalVelocity, launchRamp.LaunchVelocity);
            verticalVelocity = Mathf.Clamp(verticalVelocity, -10f, 30f);
            airStartTime = Time.time;
            if (logAirtime)
                Debug.Log($"[SkiGame] Take-off  ramp={(launchRamp != null)}  groundVel={groundVelocity:F1}  launchVel={verticalVelocity:F1}  h={localHeight:F2}  speed={Speed:F1}  sinceRamp={Time.time - lastRampTime:F2}");
            visual.SetOnGround(false);
        }

        // ------------------------------------------------------------------ jump & backflip
        private void HandleBufferedActions()
        {
            if (IsStunned) return;
            float now = Time.time;

            // JUMP: buffered trigger + coyote time.
            if (now - lastJumpPressTime <= settings.inputBufferTime)
            {
                bool canJump = grounded || (!jumpedThisAir && now - lastGroundedTime <= settings.coyoteTime);
                if (canJump) DoJump();
            }

            // BACKFLIP: works any time airborne (normal jump or ramp), buffered.
            if (now - lastFlipPressTime <= settings.inputBufferTime && !grounded && !flipping &&
                (settings.allowMultipleFlips || flipsThisAir == 0))
            {
                StartFlip();
            }
        }

        private void DoJump()
        {
            // On a ramp (or just after leaving one) the jump only adds a small bonus on top
            // of the ramp launch, instead of stacking a full jump on it.
            bool rampContext = (grounded && onRampLastFrame) || (!grounded && airFromRamp);
            float rampLaunch = lastRamp != null ? lastRamp.LaunchVelocity : 0f;
            if (grounded) LeaveGround(null);
            if (rampContext)
                verticalVelocity = Mathf.Max(Mathf.Max(verticalVelocity, 0f), Mathf.Max(rampLaunch, settings.jumpVelocity)) + settings.rampJumpBonus;
            else
                verticalVelocity = Mathf.Max(verticalVelocity, 0f) + settings.jumpVelocity;
            jumpedThisAir = true;
            lastJumpPressTime = -999f;
            lastGroundedTime = -999f;
            visual.PlayJump();
            GameEvents.RaiseJumped();
        }

        private void StartFlip()
        {
            flipping = true;
            flipT = 0f;
            lastFlipPressTime = -999f;
            visual.PlayBackflip(settings.flipDuration);
            GameEvents.RaiseBackflipStarted();
        }

        private void Land()
        {
            if (logAirtime)
                Debug.Log($"[SkiGame] Landing  airtime={Time.time - airStartTime:F2}s  vel={verticalVelocity:F1}  flipping={flipping}");
            grounded = true;
            lastGroundedTime = Time.time;
            verticalVelocity = 0f;
            bool badLanding = false;

            if (flipping)
            {
                float done = flipT * 360f;
                float remaining = 360f - done;
                if (remaining <= settings.landingAngleTolerance) flipsThisAir++;   // close enough: counts!
                else if (done > settings.landingAngleTolerance) badLanding = true;  // landed on head/back
                // else: flip had barely started -> silently cancelled.
                flipping = false;
                flipT = 0f;
            }

            // Award coins collected during this airtime (x multiplier after a completed flip).
            int flips = badLanding ? 0 : flipsThisAir;
            int multiplier = flips > 0 ? rules.backflipMultiplier : 1;
            int points = pendingAirPoints * multiplier + (flips > 0 ? rules.backflipLandingBonus * flips * multiplier : 0);
            GameEvents.RaisePointsAwarded(points);
            if (flips > 0) GameEvents.RaiseBackflipLanded(flips, multiplier, points);

            pendingAirPoints = 0;
            flipsThisAir = 0;
            visual.SetFlipProgress(0f);
            visual.SetOnGround(true);

            if (badLanding)
            {
                Crash("Bad landing!");
            }
            else
            {
                visual.PlayLand(flips > 0);
                GameEvents.RaiseLanded(flips > 0);
            }
        }

        // ------------------------------------------------------------------ collisions
        private void DetectOverlaps(Vector3 feet)
        {
            float r = settings.collisionRadius;
            Vector3 p0 = feet + Vector3.up * (r + 0.05f);
            Vector3 p1 = feet + Vector3.up * Mathf.Max(r + 0.06f, settings.collisionHeight - r);
            int count = Physics.OverlapCapsuleNonAlloc(p0, p1, r, overlapBuffer, GameLayers.InteractableMask, QueryTriggerInteraction.Collide);

            for (int i = 0; i < count; i++)
            {
                Collider c = overlapBuffer[i];
                if (c == null) continue;

                Coin coin = c.GetComponentInParent<Coin>();
                if (coin != null)
                {
                    if (!coin.Collected) CollectCoin(coin);
                    continue;
                }

                Obstacle obstacle = c.GetComponentInParent<Obstacle>();
                if (obstacle != null && !obstacle.HasBeenHit && !IsInvulnerable)
                {
                    obstacle.OnHit();
                    Crash("Hit a " + obstacle.DisplayName);
                }
            }
        }

        private void CollectCoin(Coin coin)
        {
            Vector3 coinPosition = coin.transform.position;
            coin.Collect();
            int value = coin.Value;
            GameEvents.RaiseCoinPickedUpAt(coinPosition);
            GameEvents.RaiseCoinCollected(value, !grounded);
            if (grounded) GameEvents.RaisePointsAwarded(value);
            else pendingAirPoints += value; // paid on landing, possibly doubled
        }

        /// <summary>Crash: speed loss, brief stun, one life lost (GameManager), short invulnerability.</summary>
        public void Crash(string reason)
        {
            if (IsStunned || IsInvulnerable) return;
            stunTimer = settings.stunDuration;
            invulnerableTimer = settings.invulnerabilityTime;
            Speed = Mathf.Max(settings.minSpeed, Speed * settings.crashSpeedFactor);
            flipping = false;
            flipT = 0f;
            flipsThisAir = 0;
            lastJumpPressTime = lastFlipPressTime = -999f;
            visual.SetFlipProgress(0f);
            visual.PlayCrash(settings.stunDuration);
            GameEvents.RaiseCrashed(reason);
        }

        // ------------------------------------------------------------------ orientation & visuals
        private void UpdateOrientation(float dt)
        {
            Vector3 up = grounded ? groundNormal : slope.SlopeNormal;
            Vector3 forward = Vector3.ProjectOnPlane(moveDirection, up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            Quaternion target = Quaternion.LookRotation(forward.normalized, up);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-settings.alignSharpness * dt));
        }

        private void UpdateVisual()
        {
            visual.SetLean(steerValue * (grounded ? 1f : 0.4f));
            float autoTuck = Mathf.InverseLerp(settings.tuckSpeedThreshold01, 1f, Speed01);
            visual.SetTuck(grounded ? Mathf.Max(Mathf.Clamp01(speedValue), autoTuck) : 0f);
            visual.SetSpeed01(grounded ? Mathf.Max(0.25f, Speed01) : 0f);
            visual.SetFlipProgress(FlipProgress);
        }

        private void OnDrawGizmosSelected()
        {
            if (settings == null) return;
            Gizmos.color = Color.yellow;
            float r = settings.collisionRadius;
            Gizmos.DrawWireSphere(transform.position + Vector3.up * (r + 0.05f), r);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * (settings.collisionHeight - r), r);
        }

        /// <summary>Used when no visual is present so the controller never needs null checks.</summary>
        private class NullSkierVisual : ISkierVisual
        {
            public void SetLean(float lean) { }
            public void SetTuck(float tuck) { }
            public void SetSpeed01(float speed01) { }
            public void SetOnGround(bool onGround) { }
            public void SetFlipProgress(float progress01) { }
            public void SetInvulnerable(bool invulnerable) { }
            public void PlayJump() { }
            public void PlayBackflip(float duration) { }
            public void PlayLand(bool afterTrick) { }
            public void PlayCrash(float duration) { }
        }
    }
}
