using System;
using UnityEngine;

namespace Gameplay.Mining
{
    [Serializable]
    public enum TrackModifierType
    {
        Spawner, Clear, Remix,
        RootShift, RhythmStyle,
        ChordProgression
    }
    public enum TrackClearType
    {
        EnergyRestore,
        Remix
    }
    public class TrackUtilityMinedObject : MonoBehaviour
    {
        public TrackModifierType type;
        public ChordProgressionProfile chordProfile;
        private RemixUtility _appliedRemix;
        private MinedObject _minedObject;
        private MinedObjectSpawnDirective _directive;
        public void Initialize(InstrumentTrackController controller, MinedObjectSpawnDirective _directive)
        {
            this._directive = _directive;
            if (this._directive == null || this._directive.remixUtility == null) return;
            _minedObject = GetComponent<MinedObject>();
        
            if (_minedObject == null) return;
            _minedObject.musicalRole = this._directive.remixUtility.targetRole;

            // 👇 Resolve the actual track tied to the role
            var resolvedTrack = controller.FindTrackByRole(_minedObject.musicalRole);
            if (resolvedTrack != null)
            {
                // 👇 This sets assignedTrack correctly for RemixRingHolder to use
                _minedObject.AssignTrack(resolvedTrack);
                this._directive.assignedTrack = resolvedTrack;
            }
            this._directive = _directive;
        }
        void OnTriggerEnter2D(Collider2D other)
        {
            CollectionSoundManager.Instance.PlayEffect(SoundEffectPreset.Aether);
            var vehicle = other.GetComponent<Vehicle>();
            if (vehicle != null)
            {
                if (_directive == null)
                {
                    Debug.LogError($"{name} ❌ directive is null in TrackUtilityMinedObject!");
                    return;
                }

                vehicle.AddRemixRole(_minedObject.assignedTrack, _minedObject.musicalRole, _directive);
                GetComponent<Explode>()?.Permanent();
            }
        }

    }
}