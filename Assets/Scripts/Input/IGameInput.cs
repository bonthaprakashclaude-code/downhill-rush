namespace SkiGame
{
    /// <summary>
    /// The ONLY way gameplay code receives player intent.
    ///
    /// Gameplay (PlayerController, debug HUD, ...) never reads the keyboard or any
    /// device directly - it only reads this interface. To drive the skier with a new
    /// device (EMG, Wii Balance Board COP, gamepad, network, replay...) write a new
    /// MonoBehaviour that implements IGameInput (ideally by deriving from
    /// <see cref="GameInputBase"/>) and assign it to PlayerController.inputSource.
    /// </summary>
    public interface IGameInput
    {
        /// <summary>Steering, -1 = full left, 0 = straight, +1 = full right. Already smoothed.</summary>
        float Steer { get; }

        /// <summary>Speed control, -1 = full brake, 0 = neutral, +1 = full speed-up / tuck. Already smoothed.</summary>
        float SpeedControl { get; }

        /// <summary>True for exactly ONE frame when a jump is requested (discrete trigger with cooldown).</summary>
        bool JumpTriggered { get; }

        /// <summary>True for exactly ONE frame when a backflip is requested (discrete trigger with cooldown).</summary>
        bool BackflipTriggered { get; }
    }

    /// <summary>
    /// Optional, for proportional devices (Wii Balance Board): when SteerSetsHeading is true,
    /// Steer is the skier's DIRECTION (-1 = full left .. +1 = full right of straight downhill),
    /// followed continuously, instead of a turn rate.
    /// </summary>
    public interface IDirectSteering
    {
        bool SteerSetsHeading { get; }
        /// <summary>How fast the skier's heading follows the target direction (degrees / second).</summary>
        float HeadingFollowRate { get; }
    }
}
