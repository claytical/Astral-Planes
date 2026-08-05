/// <summary>
/// Lets PathChoiceCoordinator override this player's steering during the path-choice
/// redirect sequence, so a forced Vehicle.Move() toward the winning exit isn't fought by the
/// player's own stick/keyboard input every FixedUpdate.
/// </summary>
public partial class LocalPlayer
{
    private bool _inputLocked;

    public void SetInputLocked(bool locked) => _inputLocked = locked;
}
