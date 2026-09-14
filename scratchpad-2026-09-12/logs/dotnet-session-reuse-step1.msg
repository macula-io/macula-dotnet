Reuse an open session for stream and content direct dial

A station keeps one connection per identity and closes the older one when
a newer one completes its handshake. Direct dial opened a second
connection under the caller's identity, which closed resolveVia or a pool
link to the same station.

Sessions now register by identity and station once HELLO is accepted,
and unregister on close. OpenStreamDirectAsync,
OpenStreamDirectWithCertChainAsync, PutDirectAsync and GetDirectAsync run
on a registered session, on a dedicated QUIC stream, and leave it open.
The stream calls return DirectDial.OpenedStream, which says whether the
caller owns the session. Calls still dial their own connection: reusing a
session for a call needs a per-session reader first.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01En7T4rpzV5Lt7gGjNJmPw5
