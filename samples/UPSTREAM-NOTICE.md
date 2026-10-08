# ControlCatalog and themes port notice

ControlCatalog, ControlCatalog.Desktop, ControlCatalog.Browser and MiniMvvm were copied in full from [wieslawsoltes/Avalonia](https://github.com/wieslawsoltes/Avalonia/tree/a9429a328057befa287ffb5e981f58b86a86eda0/samples), revision `a9429a328057befa287ffb5e981f58b86a86eda0`.

The complete [Avalonia.Themes.Simple](https://github.com/wieslawsoltes/Avalonia/tree/a9429a328057befa287ffb5e981f58b86a86eda0/src/Avalonia.Themes.Simple) and [Avalonia.Themes.Fluent](https://github.com/wieslawsoltes/Avalonia/tree/a9429a328057befa287ffb5e981f58b86a86eda0/src/Avalonia.Themes.Fluent) directories were copied from the same revision. Their original assembly names and version are preserved. These copies are compiled with XamlG and used by the catalog.

Copyright (c) AvaloniaUI OÜ. All Rights Reserved.

The upstream MIT license is retained verbatim in [UPSTREAM-LICENSE.md](UPSTREAM-LICENSE.md). The upstream third-party notices are retained in [UPSTREAM-THIRD-PARTY-NOTICES.md](UPSTREAM-THIRD-PARTY-NOTICES.md). Existing file headers, font assets and the native embedding video's license remain with their files. Upstream linked reactive helpers, native GTK/UTF-8 helpers and shared icons are also included.

This port references framework dependencies from the pinned Avalonia source checkout prepared by `scripts/prepare-controlcatalog.py`. The current catalog uses framework APIs newer than the published Avalonia packages. Each ported XAML project adds a single XamlG.Avalonia project reference to replace its XAML compiler. Repository build integration and validation are additions by the XamlG contributors under this repository's MIT license. The upstream names and artwork identify the sample's origin; this port is maintained in XamlG.

`controlcatalog-upstream.json` records every imported file's original path, destination and SHA-256, plus the explicitly documented adaptations. No upstream page or demo is intentionally omitted.
