# Diagnostic partial-result streaming

Both diagnostic pull methods support the LSP `partialResultToken` request parameter. This is an optional transport mode for the existing coherent XAML/C# analysis, not a separate compiler or a claim of incremental semantic binding.

## Opt-in and wire values

A request without `partialResultToken` returns the same complete result as before. A supplied token must be a JSON string of at most 1,024 UTF-16 code units or an LSP signed 32-bit integer. Empty strings, zero and negative integers are valid. The server preserves the JSON type and value; it does not turn integers into strings or use the request ID as the token. Explicit null, booleans, containers, non-integers, out-of-range integers and oversized strings receive `InvalidParams` (`-32602`). `workDoneToken` is independent and does not opt into partial results.

The token is cloned into request-owned state. There is no process-global progress buffer or token registry. Clients must use distinct tokens for concurrent streams they need to distinguish. Every progress message is a notification, not a JSON-RPC request:

```json
{
  "jsonrpc": "2.0",
  "method": "$/progress",
  "params": {
    "token": 0,
    "value": { "items": [] }
  }
}
```

The envelope above illustrates the token and shape only; the server does not emit empty workspace batches.

### Document diagnostics

For `textDocument/diagnostic`, the first progress value is the primary document's full or unchanged report, without `relatedDocuments`. This is followed by zero or more values containing only a `relatedDocuments` URI map. Related reports are emitted only when the client negotiated `relatedDocumentSupport`.

The terminal successful response is `{"jsonrpc":"2.0","id":42,"result":null}`. It does not repeat the primary report or replace already-streamed related reports. Even when there are no related documents, the primary report is emitted once as progress. A previous result ID can make that first report `unchanged`; related documents still have their own independent report IDs.

### Workspace diagnostics

For `workspace/diagnostic`, every progress value is an `items` array of complete document reports. Current document URIs are ordinally ordered; removed URIs from `previousResultIds` follow in ordinal order. Open documents retain their actual client version and closed/removed documents use null. Removed paths receive clear reports without the server opening a client-supplied path.

The terminal successful response is `{"jsonrpc":"2.0","id":42,"result":{"items":[]}}`. Reports are not repeated there. An empty workspace with no removal reports sends no progress and returns the empty final result.

## Batching, backpressure and bounds

A workspace or related-document batch contains at most 32 reports and targets at most 262,144 bytes (256 KiB) of serialized JSON. Accounting includes the progress envelope, escaped token/URI strings, JSON punctuation and escaped diagnostic values using the transport's camel-case serializer policy. Content-Length headers are outside that payload target.

Each send is awaited before the next batch is projected. There is no unbounded pending-output queue. At a byte boundary, the batcher may have projected one lookahead report to determine whether it fits. Cancellation is checked before moving the source enumerator and before publication, and disposal retires the enumerator on failure or cancellation.

One document's full diagnostic array is indivisible: sending fragments as separate full reports would incorrectly clear earlier diagnostics. An individual report may therefore exceed the batching target, but remains subject to the connection's hard payload limit (8 MiB by default). A report that exceeds the hard limit fails the request rather than being truncated. The batching target is not an absolute managed-memory quota.

The existing shared semantic analysis still completes before diagnostic projection begins. Related-document selection retains its existing bounded connected-resource traversal (at most 255 related documents); streaming does not promise an unbounded dependency closure. The diagnostic cache retains its existing document/character limits, equality rules and eviction behavior.

## Freshness, cancellation and failures

Every progress frame and the terminal response use the same serialized output gate and complete project/open-buffer snapshot predicate as ordinary semantic publication. A dependency edit can supersede the stream without changing the requesting document's client version. Requests cannot append new results from a different snapshot onto an existing stream.

Client cancellation produces `RequestCancelled` (`-32800`). A superseded snapshot produces `ServerCancelled` (`-32802`) with `{"retriggerRequest":true}`. A frame admitted before cancellation completes its payload and flush; cancellation may suppress later batches, but cannot tear framing or damage a sibling request. An I/O failure or deadline after admission permanently closes the transport, following the existing connection contract.

Under LSP partial-result rules, clients may retain incomplete results after `RequestCancelled` with an appropriate incomplete indication. Results from a request ending in another error, including `ServerCancelled`, must be discarded. A partial stream is not a successful complete workspace report.

Provider, URI, progress-token and previous-result parameters are validated before diagnostic-cache mutation or any progress notification. Cache entries projected before a later cancellation may remain retained, but unknown/undelivered previous IDs cannot cause an incorrect `unchanged` reply. A subsequent valid pull obtains current semantic analysis and compares actual report values.

## Validation and references

The library batch/projection tests, deterministic framed-server tests and real/installed stdio coverage are described in [pull-diagnostic validation](pull-diagnostic-validation.md). CI evidence belongs to the exact tested commit; these test definitions do not themselves certify a revision.

Primary protocol references:

- [LSP 3.17 partial-result rules](https://github.com/microsoft/language-server-protocol/blob/gh-pages/_specifications/lsp/3.17/types/partialResults.md): send all values through progress and keep the terminal result empty; error handling distinguishes client cancellation.
- [LSP 3.17 diagnostic requests](https://github.com/microsoft/language-server-protocol/blob/gh-pages/_specifications/lsp/3.17/language/pullDiagnostics.md): primary-before-related document progress, workspace report shapes and retriable server cancellation.
