using UnityEngine;
using UnityEngine.InputSystem;

// RETIRED: the title menu (MainMenu) now owns Main's input, and it hides the "Start Text" prompt.
// This stays only because Main.unity's EventSystem PlayerInput still wires UI/Submit to
// OnAnyInput, and quoteText is serialized there. The handler does nothing. Remove the component
// and that event binding together.
public class IntroInputHandler : MonoBehaviour
{
    public GameObject quoteText;

    public void OnAnyInput(InputAction.CallbackContext context) { }
}
