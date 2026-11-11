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

# Ecosystem architecture: self-maintained infrastructure vs upstream plugins

## Our infrastructure layer (we own and maintain)

- **RayTrace** (`CS2-RayTraceRemastered`): `raytrace:craytraceinterface`
  capability — full trace service (TraceOptions/InteractionLayers).
  Consumers: NadeSystem, BotState. We rewrote it; treat as first-party.
- **BotIdentity** (`CS2-Bot-Identity`, api18): `botidentity:api` —
  managed-bot identity (name/steamid disguise, shm bridge). Replaces
  BotHider entirely (archived). Consumers: VoteImprover, MatchOvertime,
  CS2-Bot-Manager, CS2-Bot-Buy.
- **botchat:api**: speaker arbitration + delivery — provider = refactored
  BotChat (`fork/feat/next`); consumers = Banter (`fork/feat/banter-plugin`),
  BotBuy drop-lines. Upstream only has the pre-refactor monolith.

## Upstream plugins (sync on breakage, don't fork)

- BotRandomizer, BotAimImprover, BotBuy, NadeSystem, BotAI, BotState,
  BotController, MultiAddonManager: keep near upstream; our tree is a
  downstream sync point. When a signature/offset breaks after a game
  update, first check upstream for the fix before patching ourselves.
- The in-tree files under `addons/counterstrikesharp/plugins/` (BotAI,
  BotState, NadeSystem, BotRandomizer, BotAimImprover, BotBuy) are
  pre-submodule snapshots — stale, NOT authoritative source. Upstream
  moved every plugin to a submodule (`.gitmodules` on origin/main):
  BotAI → ed0ard/CS2-BotAI, BotState → ed0ard/CS2-Smarter-Bot,
  NadeSystem → ed0ard/CS2-Bot-NadeSystem, BotAimImprover →
  ed0ard/CS2-Bullseye-Bot, BotRandomizer → ed0ard/CS2-Bot-Randomizer,
  BotBuy → ed0ard/CS2-Bot-Buy. For audits/builds use those repos plus
  our forks (CS2-Bot-State, CS2-Bot-NadeSystem), never the in-tree
  copies. Auditing the stale files produces false signature-death
  reports (observed 2026-11-11).

## Triage rule for post-update breakage

- Plugin reports "signature resolved to zero" / "Invalid function
  pointer" → upstream sync first (they usually fix within days).
- Our infrastructure pieces → we fix ourselves (gamedata/offset updates,
  KHook ports); nobody else maintains them.
