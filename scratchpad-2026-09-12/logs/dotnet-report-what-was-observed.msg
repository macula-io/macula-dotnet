Report what a direct-dial call observed when it runs out of time

A procedure call whose DHT lookups all failed threw
ProcedureNotAdvertisedException, and a call whose lookups were never
answered in time threw ProcedureNotAdvertisedException,
ContentNotAnnouncedException or StationEndpointNotFoundException: an
absence nobody observed. A failed content lookup after an answered one
also replaced what the answered one found, and a station_endpoint lookup
that failed ended at once instead of being retried.

At its deadline a call now throws the last candidate failure, else why
the latest answered lookup found nothing, else the latest failed
lookup's exception, else a TimeoutException. A lookup cut off by the
deadline records nothing. A station_endpoint lookup retries a failed
lookup within its budget and throws StationEndpointNotFoundException
only when a lookup was answered, else the lookup's exception, else a
TimeoutException, and that is what a candidate's failure carries.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01En7T4rpzV5Lt7gGjNJmPw5
