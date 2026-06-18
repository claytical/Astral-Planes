using System.Runtime.CompilerServices;
using UnityEngine;

public static class UnityObjectExtensions
{
    public static int GetInstanceID(this Object obj) =>
        RuntimeHelpers.GetHashCode(obj);
}
