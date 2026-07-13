- need to fix the trndline it should get calculated when the direction changed
- same fix needed for the channels
- chart should be able to load all the candles not just copuple of last candles


For a weeks-long trading process, the bigger improvements would be:

- Soak tests lasting hours or days.
- Memory and allocation monitoring.
- Reliable WebSocket reconnection and exponential backoff.
- Bounded queues and event buffers.
- Correct cancellation and graceful shutdown.
- Health checks and broker-session renewal.
- Protection against repeated or duplicated orders.
- Structured logging with bounded retention.
- Measuring GC pauses and considering Server GC if the workload is sufficiently
  concurrent.
- retrieve from accidentally shut down.  