# Project Deployment Notes

- Before deploying or validating NadeSystem, search active server mode cfg files for `bot_nades`. A map cfg command runs after plugin load and can override `NadeSystem.json`; verify the plugin's runtime mode in the server console after map initialization.
- Combined releases must verify ZIP integrity, critical DLL hashes, localized YAML parsing, plugin-reported versions, and `css_plugins list` after restart. Do not infer the active version from filenames alone.

# Maintained fork patches (upstream-rejected)

## BotAI casual-awareness (feat/casual-awareness)

- Upstream PR ed0ard/CS2-Bot-Improver#123 closed 2026-10-25, unmerged.
  Maintainers rejected arbitrary patch-module toggles: BotAI byte patches
  share state-machine assumptions, so unchecked subsets create hybrid
  behaviors upstream does not want to support.
- We maintain it ourselves. Branch: `fork/feat/casual-awareness`
  (BotAIPatchCategories.cs + BotAIConfig.cs + BotAI.cs wiring, 2 commits).
- Known cross-category dependencies when toggling subsets:
  - `VisionAttention`'s AlwaysWatchApproachPoints/IsNoticable_AlwaysTrue
    assume Awareness' global-knowledge patches; disabling Awareness alone
    leaves bots tunnel-staring at approach points.
  - `StateMachine`'s Idle_IsSafeAlwaysFalse drives investigation that
    depends on Awareness/BombInfo hearing triggers; isolated it produces
    aimless permanent alert.
  - `Upkeep_BotCOS/SIN_ZeroDrift` pairs with CombatForce spray behavior.
- If resubmitted upstream, present as preset tiers (casual/normal/hard)
  with verified combinations, not arbitrary toggles.
