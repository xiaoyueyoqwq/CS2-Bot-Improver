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

## BotChat / Banter / botchat:api (self-maintained ecosystem)

- `botchat:api` is live infrastructure: provider = refactored BotChat
  (feat/next), consumers = Banter (feat/banter-plugin) and BotBuy 2.0.5
  drop-lines (graceful fallback if absent). Deployed on the server.
- Authoritative code lives on our fork: `fork/feat/next` (Fimallory's
  refactor: localized base + api) and `fork/feat/banter-plugin` (adds our
  Banter plugin). Fimallory/CS2-BotChat contains only the pre-refactor
  monolith (no api); upstream PR ed0ard/CS2-Bot-Improver#159 would merge
  that old version.
- Pending: user contacting author re: adopting the refactored
  architecture. If accepted, migrate it into the standalone repo the
  submodule PR points at; if declined, maintain it as our own
  ecosystem component like casual-awareness. ed0ard's QQ: 980890875
  (invited in xiaoyueyoqwq/CS2-Bot-Improver PR #1 comments).
