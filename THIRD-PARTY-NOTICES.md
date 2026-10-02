# Third-party notices

## Avalonia selector grammar

`src/XamlG.Frameworks/Avalonia/Syntax/Internal` includes the selector grammar, character reader, identifier parser and parse exception adapted from the Avalonia project, commit `17350180c33b063f0e98abbfd19aa3cae63f5d56`.

Changes: isolated namespace, nested syntax types split into individual files, internal visibility, and an invariant numeric parsing helper. The framework binder and C# code generator are XamlG implementations and do not use XamlX.

Copyright (c) the Avalonia Project contributors.

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

## Compatibility tests

The optional upstream test projects link the original XamlX and Avalonia sources at the exact revisions recorded in `tests/Upstream.props` and the CI workflow. Their original licenses remain applicable. No upstream IL compiler or reflection type system is part of XamlG production packages.
