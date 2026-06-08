# Upstream

Source: https://github.com/xupefei/Locale-Emulator-Core.git

Imported from commit: ae7160dc5deb97947396abcd784f9b98b6ee38b3

Local changes:

- Retargeted `LoaderDll` and `LocaleEmulator` to the VS2022 `v143` toolset.
- Redirected release outputs to `Release\scr\LE`.
- Disabled the legacy secure CRT C++ overload templates for VS2022 compatibility.
- Adjusted `LocaleEmulator` linking so it builds with the bundled legacy WDK libraries.
