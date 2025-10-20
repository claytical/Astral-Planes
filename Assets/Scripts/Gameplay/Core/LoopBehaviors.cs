using UnityEngine;

public static class LoopBehaviors {
    public static void Apply(InstrumentTrack t, NoteBehavior b, VariationProfile v, System.Random rng) {
        var notes = t.GetPersistentLoopNotes();
        if (notes == null || notes.Count == 0) return;

        switch (b) {
            case NoteBehavior.Staccatify:
                for (int i=0;i<notes.Count;i++)
                    notes[i] = (notes[i].stepIndex, notes[i].note, (int)(notes[i].duration*0.55f), notes[i].velocity+5);
                break;

            case NoteBehavior.Legatify:
                for (int i=0;i<notes.Count;i++)
                    notes[i] = (notes[i].stepIndex, notes[i].note, (int)(notes[i].duration*1.35f), notes[i].velocity-5);
                break;

            case NoteBehavior.HumanizeTiming:
                // store per-step offsets if you schedule notes with fine timing; or approximate with dur skew
                for (int i=0;i<notes.Count;i++) {
                    int d = notes[i].duration;
                    int skew = (int)(d * (rng.NextDouble()*0.05 - 0.025)); // ±2.5%
                    notes[i] = (notes[i].stepIndex, notes[i].note, d + skew, notes[i].velocity);
                }
                break;

            case NoteBehavior.VelocityShape:
                for (int i=0;i<notes.Count;i++) {
                    float u = i/(float)Mathf.Max(1, notes.Count-1); // 0..1 across phrase
                    float shaped = Mathf.Lerp(80, 115, Mathf.SmoothStep(0,1,u));
                    notes[i] = (notes[i].stepIndex, notes[i].note, notes[i].duration, shaped);
                }
                break;

            case NoteBehavior.AddNeighborOrnament:
                // duplicate some hits with quick lower/upper neighbor before main note
                // (implementation depends on how you schedule sub-step events; keep simple for now)
                break;

            case NoteBehavior.ChordChange:
            case NoteBehavior.RootShift:
            case NoteBehavior.InvertVoicing:
            case NoteBehavior.RegisterExpand:
            case NoteBehavior.RegisterCompress:
                // You already have ApplyChord / RetuneLoopToChord — call those here
                break;
        }
        t.controller.UpdateVisualizer();
    }
}
