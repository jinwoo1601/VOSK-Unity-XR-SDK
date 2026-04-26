# Inspector Authoring

ScriptableObject equivalents of the slots, commands, and command sets defined
in `CommandDemo.cs`. Used to verify that Inspector authoring produces the
same recognition behaviour as the code path.

## Contents

- `Slots/` — 6 `VoxrSlotAsset` files (target, weapon, quantity, range, heading, elevation)
- `Commands/` — 11 `VoxrCommandAsset` files (one per intent in `CommandDemo.cs`)
- `Sets/` — 3 `VoxrCommandSetAsset` files (weapons, navigation, common)

## How to wire

1. On the GameObject hosting `VoxrCommandRecogniser`, expand **Inspector
   Authoring**:
   - **Slot Assets** → drag in all 6 assets from `Slots/`
   - **Command Set Assets** → drag in `Set_Weapons`, `Set_Navigation`, `Set_Common`
   - **Initial Active Set Names** → `weapons`, `navigation`, `common`
2. On the `CommandDemo` component, enable **Use Inspector Authoring**.
3. Enter Play mode (or build to Quest). `VoxrCommandRecogniser.Awake()` will
   call `Configure()` from the assets and `SetActiveSets(initialActiveSetNames)`
   before `CommandDemo.Start()` runs.

When **Use Inspector Authoring** is disabled, `CommandDemo` falls back to the
code path: it constructs slots/sets in `Start()` and calls `Configure()`
directly, overriding any asset-driven setup. This path has been tested in
the device test matrices.

## Equivalence with code path

These assets are intentional 1:1 copies of the definitions in
`CommandDemo.Start()`. Any divergence is a bug in either the assets or the
code path -- both should produce identical grammar JSON and identical
recognition results for the same input.

## See Also

- [Inspector Authoring](../../../Documentation~/inspector-authoring.md) -- zero-code ScriptableObject setup guide
- [ScriptableObjects API](../../../Documentation~/api/scriptable-objects.md) -- asset type reference
