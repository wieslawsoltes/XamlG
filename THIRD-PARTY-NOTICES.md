# Third-party notices

## XamlX compatibility and baseline tests

The optional upstream test projects link source from AvaloniaUI/XamlX revision `7ef6aef496ab6e8dcf3df04bef697be49db37c04`. `tests/XamlG.XamlX.Baseline.Tests/CompilerTestBase.Sre.cs` is adapted from the same revision; only runtime assembly resolution is changed. The test-only adapter generator preserves upstream runtime test bodies while replacing the IL-specific test setup. See `docs/upstream-validation.md` for the exact comparison boundary.

Copyright (c) 2019 Nikita Tsukanov

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

No upstream IL compiler or reflection type system is included in XamlG production packages. The XamlX test-reference assembly is not packable.

## ControlCatalog and themes port

The complete ControlCatalog, desktop/browser hosts, MiniMvvm, Avalonia.Themes.Simple and Avalonia.Themes.Fluent are ported from `wieslawsoltes/Avalonia` revision `a9429a328057befa287ffb5e981f58b86a86eda0`. Copyright (c) AvaloniaUI OÜ. All Rights Reserved. See [the port notice](samples/UPSTREAM-NOTICE.md), [retained upstream MIT license](samples/UPSTREAM-LICENSE.md) and [upstream third-party notices](samples/UPSTREAM-THIRD-PARTY-NOTICES.md). The import manifest records all original file identities and documented adaptations.

## Avalonia literal parsers

The literal parsers and supporting utilities under `src/XamlG.Frameworks/Avalonia/Parsing` originate from Avalonia revision `a9429a328057befa287ffb5e981f58b86a86eda0`. Their [import manifest](src/XamlG.Frameworks/Avalonia/Parsing/upstream.json) records the original source hashes; the [MIT license](src/XamlG.Frameworks/Avalonia/Parsing/LICENSE.md) and source notices are retained. The pristine import is committed separately from compiler adaptations.

The MIT License (MIT)

Copyright (c) AvaloniaUI OÜ
All Rights Reserved

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

### WPF-derived KeySpline parser

The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
## Package dependencies

Roslyn, Avalonia, Dockyard.Blazor, ModelContextProtocol, OpenAI, Microsoft.IdentityModel.JsonWebTokens, System.Security.Cryptography.ProtectedData, Anthropic, Google.GenAI, Monaco Editor, Playwright and other package dependencies retain their own licenses and notices. The browser asset build copies Monaco from its pinned npm package with its license files. Avalonia Browser and Dockyard assets are published from their NuGet packages without removing package attribution. The compiler's Avalonia adapter uses public metadata contracts; its production source is not a copy of XamlX's IL compiler.
