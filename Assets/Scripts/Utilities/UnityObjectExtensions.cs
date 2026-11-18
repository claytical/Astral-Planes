using System.Runtime.CompilerServices;
using UnityEngine;

public static class UnityObjectExtensions
{
    public static int GetInstanceID(this Object obj) =>
        RuntimeHelpers.GetHashCode(obj);

    // Use instead of `GetComponent<T>() ?? AddComponent<T>()`: in the Editor a missing component
    // comes back as Unity's "fake null" object, which C#'s ?? treats as non-null — so the Add
    // never ran and the caller got a MissingComponentException later (Editor-only, unlike builds).
    public static T GetOrAddComponent<T>(this GameObject go) where T : Component =>
        go.TryGetComponent(out T existing) ? existing : go.AddComponent<T>();

    public static T GetOrAddComponent<T>(this Component c) where T : Component =>
        c.gameObject.GetOrAddComponent<T>();
}
