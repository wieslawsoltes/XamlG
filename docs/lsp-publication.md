# Cancellation-safe LSP publication

An LSP frame is an ASCII Content-Length header followed by exactly that many UTF-8 bytes. Cancelling after the header cannot be treated as cancelling an ordinary application operation: writing another frame would corrupt the stream.

`LspConnection` delegates serialized writes to `LspMessageWriter`. Queue cancellation is separate from the connection's lifetime and per-frame deadline (30 seconds by default). Requests may cancel while queued and immediately before publication. Once a message is admitted, its original request token is not passed to stream writes. The frame finishes or the entire connection becomes unusable.

`TryWriteAsync` evaluates a synchronous freshness predicate under the output semaphore before emitting bytes. This is the logical publication point. A newer project/document revision arriving afterwards may supersede the published result but cannot truncate it; its notification is serialized after the admitted frame. Both diagnostics and semantic responses use this check. Requests that become obsolete in the publication queue receive ContentModified rather than a stale result.

A header, payload or flush failure permanently faults the writer. Its closed token interrupts the input loop, and the server does not attempt to send another error response on that stream. A finite deadline also bounds the await when a custom Stream implementation ignores cancellation. Such an abandoned I/O operation may still complete later: it retains its own buffers, its errors are observed, and no subsequent frames are ever written. The host retains stream ownership and can dispose its transport.

The deterministic `PublicationTests` pause stream writes to exercise cancellation after a header, cancellation while queued, supersession while queued, predicate failure, partial header/payload writes, flush failure, cooperative/noncooperative deadlines, repeated disposal, and connection-lifetime shutdown. They parse the resulting byte stream rather than checking only invocation counts.
