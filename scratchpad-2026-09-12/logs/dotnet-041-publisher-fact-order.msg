Send the publish_started fact before the publish, not beside it

RunPublisherAsync started sending pubsub.publish_started_v1 without
awaiting it and began the publish on a background task right away. Both
sends ran on the session at once: on a session that takes one send at a
time the publish failed with InvalidOperationException, or the fact was
lost. The unawaited call was also compiler warning CS4014.

The started fact is now sent first inside the background task and
awaited, then the publish, then the completed fact. A fact that fails to
send is still caught where it is sent and never fails the publish it
describes, matching macula_publisher.erl.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01En7T4rpzV5Lt7gGjNJmPw5
