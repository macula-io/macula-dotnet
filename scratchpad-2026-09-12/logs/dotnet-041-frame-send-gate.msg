Serialize frame sends on a FrameStream

System.Net.Quic's QuicStream fails a write that starts while another is
still pending, with InvalidOperationException. FrameStream wrote each
frame with its own WriteAsync and nothing serialized them, so two tasks
sending on one Session at once, such as a publish and the
publish_started fact RunPublisherAsync fires beside it, could fail one
of the sends.

SendFrameAsync now takes turns through a gate, on the control stream and
on dedicated streams alike, and each frame is still written whole in a
single write.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01En7T4rpzV5Lt7gGjNJmPw5
