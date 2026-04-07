# Inspector Authoring (v2.5)

ScriptableObject equivalents of the slots, commands, and command sets defined
in `CommandDemo.cs`. Used to verify that v2.5 Inspector authoring produces the
same recognition behaviour as the code path.

## Contents

- `Slots/` — 6 `VoskSlotAsset` files (target, weapon, quantity, range, heading, elevation)
- `Commands/` — 11 `VoskCommandAsset` files (one per intent in `CommandDemo.cs`)
- `Sets/` — 3 `VoskCommandSetAsset` files (weapons, navigation, common)

## How to wire

1. On the GameObject hosting `VoskCommandRecogniser`, expand **Inspector
   Authoring**:
   - **Slot Assets** → drag in all 6 assets from `Slots/`
   - **Command Set Assets** → drag in `Set_Weapons`, `Set_Navigation`, `Set_Common`
   - **Initial Active Set Names** → `weapons`, `navigation`, `common`
2. On the `CommandDemo` component, enable **Use Inspector Authoring**.
3. Enter Play mode (or build to Quest). `VoskCommandRecogniser.Awake()` will
   call `Configure()` from the assets and `SetActiveSets(initialActiveSetNames)`
   before `CommandDemo.Start()` runs.

When **Use Inspector Authoring** is disabled, `CommandDemo` falls back to the
v2.4 code path: it constructs slots/sets in `Start()` and calls `Configure()`
directly, overriding any asset-driven setup. This is the path tested in
Phase 6 of `v2.5-test-matrix.md`.

## Equivalence with code path

These assets are intentional 1:1 copies of the definitions in
`CommandDemo.Start()`. Any divergence is a bug in either the assets or the
code path — both should produce identical grammar JSON and identical
recognition results for the same input.
