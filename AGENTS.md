# Project Deployment Notes

- Before deploying or validating NadeSystem, search active server mode cfg files for `bot_nades`. A map cfg command runs after plugin load and can override `NadeSystem.json`; verify the plugin's runtime mode in the server console after map initialization.
- Combined releases must verify ZIP integrity, critical DLL hashes, localized YAML parsing, plugin-reported versions, and `css_plugins list` after restart. Do not infer the active version from filenames alone.
