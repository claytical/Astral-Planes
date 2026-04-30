# Team Migration Notes

## 2026-04-30 — MineNode legacy flee serialized keys removed

`MineNode` no longer serializes the legacy flee fields `fleeRadiusWorld` and `fleeBlendWeight`.

This is an intentional one-time data migration: existing scene/prefab YAML may still contain those keys, but Unity will ignore and drop them on re-save.
