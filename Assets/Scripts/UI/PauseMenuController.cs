using System.Collections.Generic;

/// <summary>
/// Soft-pause menu opened by a player's gamepad Select (Play/Pause action) during gameplay, shown
/// through the generic <see cref="ModalMenu"/>. "Soft": time and music keep running — the modal
/// only locks gameplay input (LocalPlayer checks <see cref="ModalMenu.ShouldSuppressInput"/>).
/// </summary>
public static class PauseMenuController
{
    static ModalMenuSpec _spec;

    /// <summary>Opens the pause menu for <paramref name="opener"/>, or closes it if it is the open modal.</summary>
    public static void Toggle(LocalPlayer opener)
    {
        if (ModalMenu.IsOpen)
        {
            if (ModalMenu.Current == _spec) ModalMenu.Close();
            return; // some other modal owns the screen
        }
        if (ModalMenu.ShouldSuppressInput) return; // a modal closed on this same press

        var gfm = GameFlowManager.Instance;
        if (gfm == null || gfm.CurrentState != GameState.Playing || gfm.MatchEnded) return;
        if (LivingScorePanel.ShouldSuppressConfirm) return;

        var pad = opener != null ? opener.PrimaryGamepad() : null;
        _spec = new ModalMenuSpec
        {
            title = "Paused",
            pad = pad,
            options = new List<ModalMenuOption>
            {
                new ModalMenuOption("Resume"),
                new ModalMenuOption("Run Tutorial", () => NarratedTutorialController.RunNow()),
                new ModalMenuOption("Quit", () => QuitPrompt.ShowQuitToMainMenu(pad)),
            },
            // Opener left or the run ended underneath us: never leave input locked.
            keepOpen = () => opener != null && GameFlowManager.Instance != null &&
                             GameFlowManager.Instance.CurrentState == GameState.Playing,
        };

        if (!ModalMenu.Show(_spec)) return;

        // Stop any boost already in progress; input is locked from here until the menu closes.
        var vehicles = gfm.GetVehicles();
        if (vehicles != null)
            for (int i = 0; i < vehicles.Count; i++)
                if (vehicles[i] != null) vehicles[i].TurnOffBoost();
    }
}
