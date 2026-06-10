using System;

public static class SessionGenome
{
    private static int sessionSeed;

    public static System.Random For(string scope) =>
        new System.Random(HashCode.Combine(sessionSeed, scope.GetHashCode()));

    public static void BootNewSessionSeed(int seed)
    {
        sessionSeed = seed;
    }
}
