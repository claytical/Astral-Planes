using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One row of <see cref="ModalMenu"/>. Purely visual: the modal polls input itself and tells the
/// row whether it is highlighted, so no Button/EventSystem is involved. Restyle the colors on the
/// ButtonTemplate inside Resources/UI/ModalMenu.prefab.
/// </summary>
public class ModalMenuButton : MonoBehaviour
{
    [SerializeField] Graphic background;
    [SerializeField] TMP_Text label;

    [SerializeField] Color backgroundNormal = new Color(0.15f, 0.15f, 0.17f, 1f);
    [SerializeField] Color backgroundSelected = new Color(0.92f, 0.92f, 0.92f, 1f);
    [SerializeField] Color labelNormal = new Color(0.8f, 0.8f, 0.85f, 1f);
    [SerializeField] Color labelSelected = Color.black;

    public void SetLabel(string text)
    {
        if (label != null) label.text = text;
    }

    public void SetSelected(bool selected)
    {
        if (background != null) background.color = selected ? backgroundSelected : backgroundNormal;
        if (label != null) label.color = selected ? labelSelected : labelNormal;
    }
}
