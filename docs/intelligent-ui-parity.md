# Intelligent UI parity and rich authoring

This guide describes the implementation in PR #19, extending the core contracts in [Intelligent Avalonia UI](intelligent-ui.md). It is a behavioral implementation using Avalonia XAML/C#, not a claim of compatibility with a private ChatGPT protocol. [OpenUI's architectural observation](https://www.openui.com/blog/how-chatgpt-intelligent-ui-works) is a third-party reference; [MCP Apps](https://modelcontextprotocol.io/extensions/apps/overview) is the public interoperability target.

## Current vocabulary

The reusable compiler has **42 native controls plus 18 source-only composites**. `xamlg_ui_catalog` returns both schemas, limits, namespaces, authoring guidance and complete pricing, counter, dashboard and form examples. The normal Studio/companion registration exposes **17 tools**, including `xamlg_ui_state_action`; form authoring adds no new permission-granting tool.

| Composite family | Elements |
| --- | --- |
| Text and structure | `ui:Heading`, `ui:Paragraph`, `ui:Badge`, `ui:Card`, `ui:Callout`, `ui:Metric`, `ui:KeyValue`, `ui:CodeBlock` |
| Tables | `ui:Table`, `ui:TableRow` |
| Charts | `ui:BarChart`, `ui:LineChart`, `ui:ScatterChart`, `ui:DataPoint` |
| Forms | `ui:Form`, `ui:Field`, `ui:SubmitButton`, `ui:ValidationSummary` |

Composites lower to ordinary registered controls before a snapshot is committed. Desktop, Studio's native Avalonia surface, the Avalonia/Wasm MCP guest, portable DOM projection and static XAML export therefore consume the same resolved primitive tree. Expansion remains subject to node/depth/property/child limits, including generated headings, cells, geometry and error messages. Native factories do not execute composite source.

Use the `ui:` namespace for composites. Native controls use `https://github.com/avaloniaui`. `Card.Space`, `Card.Gap`, `Card.Radius` and `Form.Gap` use a four-pixel scale; other sizes follow their catalog descriptors. Tables require a heading and cell for each column. Chart domains must satisfy Minimum < Maximum and contain all supplied values; empty and all-zero series receive finite default domains. Chart legends and ranges contribute to the computed text fallback. These charts are bounded single-series visualizations, not a complete analytical charting package.

## Transactional local actions

Declare local behavior with `kind: "state"` and an `arguments` object of state-key replacements:

```json
{
  "id": "increment",
  "kind": "state",
  "arguments": {
    "count": "{ui:Expr state.count + 1}",
    "previous": "{ui:Expr state.count}"
  }
}
```

A `<Button ui:Key="increment" ui:Action="increment" Content="Increment"/>` exposes that action. Every replacement expression sees the same pre-action state/data/item. The store validates all target keys and types, renders the whole candidate tree, checks action references and commits one new state revision. Unknown keys, invalid values, expression failures, excessive output or invalid regenerated trees leave state, roots, revisions and generation unchanged. An idempotent patch returns the existing snapshot without notifying observers.

Native sessions, Studio and the portable/native MCP guests route local actions without model inference or external-action review. Remote calls still use ordinary host authorization. `xamlg_ui_action` only prepares an inert intent; `xamlg_ui_state_action` applies a declared local action. Both check the owner, exact document/state revisions and current node availability. A state action cannot acquire tool, navigation, clipboard or message authority.

`TextBox.MaxLength`, ranges, selected indices and other resolved-tree invariants apply to local actions too. An action cannot make the store contain a value that its native input silently truncates. Invalid form values are different: a semantically invalid but well-typed value may be retained for correction, while submission remains disabled.

## Contextual actions in repeated content

A shared action can read the current repeated item, not just global state/data:

```xml
<StackPanel xmlns="https://github.com/avaloniaui"
            xmlns:ui="urn:xamlg:intelligent-ui">
  <ui:Card ui:Key="record"
           ui:Each="{ui:Expr data.records}"
           ui:ItemKey="{ui:Expr item.id}">
    <TextBlock Text="{ui:Expr item.name}"/>
    <Button ui:Key="select" ui:Action="select" Content="Select"/>
  </ui:Card>
  <TextBlock Text="{ui:Expr &quot;Selected: &quot; + state.selected}"/>
</StackPanel>
```

Publish `initialState: { "selected": "" }`, a records array, and:

```json
{
  "id": "select",
  "kind": "state",
  "arguments": { "selected": "{ui:Expr item.id}" }
}
```

`UiTemplate` captures the innermost lexical repetition item on action nodes. Lowering preserves it by stable node key inside the owning store. This context is **not serialized into renderer operations** and is not an accepted action-request argument. A view submits only the surface ID, document/state revisions and node key. The store resolves expressions from its current owned tree, never from a client-provided item or by parsing an ID out of a node-key string.

Item references also work in message/copy/link text and recursively nested tool arguments. Preparing such an action remains inert; the ordinary external-effect review is still required. Data updates rebuild contexts; reordered rows retain their stable identity, deleted rows cannot be invoked and stale calls fail. Nested repetitions use the innermost item. Archive restoration recompiles declarations and recaptures contexts rather than deserializing authority. Default pure expressions and an explicitly approved full-C# backend use their existing expression interfaces.

## Forms

`UiFormExamples.Configuration()` is an executable example, also returned as `form` by catalog discovery. A smaller form looks like this:

```xml
<ui:Form xmlns="https://github.com/avaloniaui"
         xmlns:ui="urn:xamlg:intelligent-ui"
         Title="Project setup">
  <ui:Field Label="Project" IsRequired="True"
            ErrorText="Enter a project name.">
    <TextBox ui:Key="name" ui:Bind="project" MaxLength="80"/>
  </ui:Field>
  <ui:Field Label="Seats" IsRequired="True"
            IsValid="{ui:Expr state.seats &lt;= data.limit}"
            ErrorText="The seat limit is exceeded.">
    <NumericUpDown ui:Key="seats" ui:Bind="seats"
                   Minimum="1" Maximum="100"/>
  </ui:Field>
  <ui:ValidationSummary/>
  <ui:SubmitButton ui:Key="submit" ui:Action="submit"
                   Content="Use configuration"/>
  <Button ui:Key="reset" ui:Action="reset" Content="Reset"/>
</ui:Form>
```

Publish declared state slots, explicit data and submit/reset actions through the ordinary `UiPublish` contract. A Field must contain exactly one registered native input. It uses that input's declared InputProperty, so no reflection or assumed CLR property name is needed. Native inputs retain their original keys, bindings and control instances while labels and validation messages change.

### Validity and availability

`IsRequired` checks the resolved input value. Null, whitespace-only text, an unchecked boolean, an empty array or `SelectedIndex = -1` are missing; numeric zero and selected index zero are present. `Field.IsValid` adds an explicit boolean predicate. `Form.IsValid` adds a form-level predicate. These expressions may depend on several state slots or current tool data.

Only currently visible, enabled fields and inputs participate. Fields inside a collapsed Expander/TreeViewItem or an inactive tab do not block submission. `ui:When` can remove a field entirely. Structural errors remain errors even for hidden declarations. Nested forms are rejected; sibling or repeated forms have independent validity scopes.

A SubmitButton lowers to a normal Button with `IsEnabled` constrained by its form's current validity. The server's normal action-availability check enforces this for direct MCP calls as well as UI clicks. The form does not disable its inputs: users can correct invalid values. Ordinary reset/cancel Buttons are not automatically gated by form validity. A SubmitButton or ValidationSummary outside a Form is rejected.

### Feedback and permissions

Fields render labels, required indicators, optional help and explicit error text. Missing accessible names and tooltips are populated from that information without overwriting application-provided values. ValidationSummary presents active field errors and a form-level error. The same messages contribute to the computed fallback. `ShowErrors = false` suppresses messages, **not** validation or submit gating.

Form validation is UI behavior, not authentication, authorization or consent. A model-authored checkbox is not permission to run an external tool. A valid message/tool/link/copy submission still goes through the host's existing review/permission path. Full-C# preview continues to allow only declared local state actions, never external effects.

## Evidence and remaining scope

| Area | Implementation and boundary |
| --- | --- |
| Local reactive behavior | Atomic state actions, row-scoped expressions, keyed reconciliation, state/data revalidation and rollback are implemented. Default expressions remain a bounded pure C# subset. |
| Rich composition | The 18 composites above are implemented. Code blocks provide selectable text; they are not a full syntax-highlighting/editor component. |
| Forms | Required/custom predicates, active-field validation, summaries and submit gating are implemented. Touched/dirty field policy, async validators, focus-first-invalid, Enter-to-submit and nested forms are not implemented as first-class form features. |
| Host lifecycle | Portable and native guests guard replacement/retirement and late responses; tests cover disposal and state actions. Commercial-host policy differences still require independent interoperability checks. |
| Charts and tables | Bounded static/reactive charts and composed cells are implemented. Multiple series, pie/area charts, zoom/brush interactions, virtualization and first-class sortable/paged tables remain outside this catalog. |
| Rich references | Retained owner-scoped tool data is implemented. Dedicated server-resolved image/entity/product/citation/map components and mixed inline Markdown/XAML response composition are not yet implemented. |
| Executable code | Genuine C# is explicit, reviewed and isolated in the browser guest. An opaque-origin frame is not a hard CPU/memory quota or an OS process sandbox; stronger execution isolation remains separate work. |
| Protocol and pixels | Public MCP Apps is supported with portable fallback. Private ChatGPT DIL/operation-wire compatibility and pixel-identical rendering across hosts are not claimed. |

Component counts alone are not full parity. The remaining entries above must be addressed and verified before making a broader parity claim.

The .NET suite includes contextual-action, form, input-limit, discovery, native control, archive and MCP tests. The portable suite executes the embedded resource. `intelligent-ui-parity.spec.mjs` imports `intelligent-ui-forms.cases.mjs`; these run the actual published Avalonia/Wasm guest against the companion to exercise native local actions, forms, keyed row reordering, chart updates, surface replacement and approved C# execution.

```sh
dotnet test tests/XamlG.IntelligentUI.Tests -c Release -warnaserror
cd tools/XamlG.Playground
npm ci --ignore-scripts --no-audit --no-fund
npx playwright install chromium
npx playwright test --config=playwright.ui.config.mjs
# Requires a real production browser publish and a Release companion build:
python ../../scripts/test-browser-studio.py tests/intelligent-ui.spec.mjs tests/intelligent-ui-lifecycle.spec.mjs tests/intelligent-ui-parity.spec.mjs
```

Source tests, completed CI runs, a merged PR, published NuGet packages and a deployed Pages site are distinct evidence. PR #19 records checks by exact commit; a passing parent does not certify a later head.
