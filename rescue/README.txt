# Rescue Teammate (Rescue) Mod Overview

## 1. Mod Summary
`Rescue Teammate` is a co-op gameplay mod focused on turning downed allies into active rescue targets instead of passive wait states. Teammates can attack corpses to bring allies back, adding meaningful in-combat rescue decisions.

- Mod ID: `rescue`
- Name: `Rescue Teammate`
- Author: `TongsKing`
- Version: `1.0`
- Affects Gameplay: `Yes` (`affects_gameplay: true`)

## 2. Core Mechanics
### 2.1 Corpse Phase After Death
When a player dies, they do not immediately leave combat completely. They enter a rescueable corpse state.

### 2.2 Corpse HP Rules
Each time a player is downed, their corpse gains HP, which scales with down count:

1. 1st down: `16`
2. 2nd down: `40`
3. 3rd down and beyond: `70`

### 2.3 Turn-Based Corpse Regeneration
At the start of the player turn, each corpse heals `20%` of its maximum corpse HP (minimum 1).

### 2.4 How Rescue Works
Teammates can attack a corpse until its corpse HP reaches 0, which triggers revival.
The revived character returns to combat with `1 HP`.

## 3. Targeting and Damage Rules
### 3.1 Target Selection
- You can directly click a corpse on the battlefield to attack it (not only from the left-side team panel).
- Attack cards with target type `AnyEnemy` can be used for corpse-rescue attacks.

### 3.2 Single-Target and AoE
- Single-target attacks can correctly resolve against corpses for rescue.
- Player-caused AoE damage can also hit corpses and contribute to rescue.

### 3.3 Enemy Interaction
Under the current design, enemies do not treat corpses as normal hittable targets for corpse-rescue HP resolution.

## 4. Resource and State Rules
### 4.1 Energy and Stars
When a player goes down, resources are not forcibly cleared. Resource state is preserved after revival.

### 4.2 Card Play Permissions
- You cannot play cards while downed.
- After revival, the player regains turn control for the current turn, including the ability to undo end turn.

### 4.3 Power (Buff/Debuff) Rules
- Corpses do not receive any powers (both buffs and debuffs).
- Characters revived from corpse state also do not receive extra powers during the same resolution window.
- Therefore, effects like `BlightStrike` (deal damage, then apply Doom) will not add Doom after reviving an ally.
- `DoomPower` is removed on revival.

## 5. Stability and Reset Behavior
- Corpse state cache is cleared when a new run starts, preventing corpse bars/data from leaking across runs.
- Down counters are reset when rest-site options are generated.

## 6. Installation and Usage
1. Put the `rescue` mod files into the game's `mods` directory.
2. Launch the game and enable this mod.
3. Enter combat and test the rescue flow when allies are downed.

## 7. Recommended Test Cases
1. After an ally is downed, verify you can directly click the corpse to attack.
2. Use single-target attacks to reduce corpse HP to 0 and confirm revival at 1 HP.
3. Use AoE damage and verify corpses are included in rescue resolution.
4. Use `BlightStrike` for rescue and verify revived targets do not receive Doom.
5. Verify downed players cannot play cards, and revived players can act again and undo end turn.

## 8. Notes
This mod makes substantial combat-rule changes. It is recommended to test compatibility in stages when using other mods related to death, revival, or targeting.
