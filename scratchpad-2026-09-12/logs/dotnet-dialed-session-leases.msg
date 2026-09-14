Share a dialed session between the direct-dial requests that reuse it

A session direct dial dialed for one request was registered for reuse,
so a later stream or transfer to the same station ran on it, and the
first request then closed it underneath the second when it finished.

A dialed session now carries a lease count. Every request that reuses it
takes a lease and releases its own when done, and the session closes
when the last lease is released; a request that finds the last lease
already released dials afresh. The session is marked as dialed before it
becomes findable, so no reuse misses its lease. OpenedStream holds its
lease until it is disposed and loses OwnsSession. Sessions the
application opened are still reused without a lease and never closed by
direct dial.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01En7T4rpzV5Lt7gGjNJmPw5
