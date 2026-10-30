## v1.12.7 (patch)

Changes since v1.12.6:

- Merge remote-tracking branch 'origin/main' into fix/retry-after-http-date (matt-edmondson)
- Cap the upstream throttle pause so one Retry-After cannot stall batched locking [patch] (matt-edmondson)
- Treat an HTTP-date Retry-After as a throttle in the lock fan-out [patch] (matt-edmondson)
- Never advertise expires_in 0 for a short-lived upstream action [patch] (matt-edmondson)

