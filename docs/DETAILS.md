# Cortex Details

This document contains detailed implementation, transport, and hardware-test notes that were previously in the top-level README.

Cortex application update checks use
`https://updates.mannelectronics.uk/cortex/latest`, rather than calling GitHub's
API from each client. The server caches the latest stable release for one hour
across its workers, including failed attempts. Manual checks use that same cache;
download links still open GitHub. An unavailable update service is reported as a
failed check, with no direct GitHub API fallback. Firmware checks retain their
existing endpoint and behaviour.

PDM registration and cellular diagnostics use the encrypted command transport
whenever a secure session is open, including on controllers with no PIN enabled.
Legacy connections retain plaintext commands. Registration accepts provisioning
requests up to 6,143 bytes, matching the firmware limit, and preserves encrypted
receive state while normal polling is paused. The controller may take up to
three minutes to return its cellular/MQTT result; Cortex reports a reply timeout
after 190 seconds. Registration attempts log the selected transport and packet
size without logging the provisioning request or credentials.

With compatible controller firmware, log downloads use plaintext bulk packets
after PIN unlock. PIN authentication, parameter changes, log deletion and all
other protected traffic remain encrypted. Older firmware keeps the encrypted
download path. Plaintext logs retain transfer checksums but are not confidential
or cryptographically authenticated. Cancelling a download stops the controller
transfer and drains queued packets through its acknowledgement, preserving the
unlocked connection so another file can be downloaded immediately. A malformed
stream, lost connection or failed cancellation handshake requires reconnecting.

Cancellation regression tests are in `tests/Cortex.Tests/PlaintextLogBulkTests.cs`.
Controller log-list sizes of `0xFFFFFFFF` mean unavailable metadata and display
as `size unavailable`; zero still represents an empty file. The entry remains
downloadable and retains its controller index. Firmware using this sentinel
requires this Cortex display update; older builds show it as 4096.00 MB.
Display regression tests are in `tests/Cortex.Tests/LogFileSizeDisplayTests.cs`.

The opt-in `LogCancellationHardwareTests` uses the real serial service without
changing controller parameters or deleting logs. It requires
`CORTEX_LOG_CANCEL_TEST_PORT`, `CORTEX_LOG_CANCEL_TEST_PIN` and
`CORTEX_LOG_CANCEL_TEST_IDENTITY` (the trusted device-public-key SHA256), plus
the closed benchmark logs named in that test. Leave those variables unset for
ordinary test runs. Never put a real PIN in source or test reports.

Reset Logs uses the shared acknowledgement handler for both legacy and encrypted
connections, preserves encrypted stream state, and resumes live polling after
completion. Acknowledgements follow the pending request rather than the most
recent live-poll command; telemetry bytes inside encrypted packets are not
treated as standalone acknowledgements. The opt-in
`LogResetHardwareTests` **erases all controller logs**. Run it only with explicit
permission: set `CORTEX_LOG_RESET_TEST_CONFIRM=ERASE_ALL_CONTROLLER_LOGS`,
`CORTEX_LOG_RESET_TEST_PORT`, `CORTEX_LOG_RESET_TEST_PIN` and
`CORTEX_LOG_RESET_TEST_IDENTITY`. It checks the encrypted reset acknowledgement,
log removal, a new downloadable log, live telemetry and retained authorization.

Settings saves wrap configuration packets in the authorized encrypted transport
when a secure session is active. Legacy connections keep their original wire
format. Both transports share configuration acknowledgement handling, including
cursor advancement, bounded retries, EEPROM-save completion and static readback.
Only standalone encrypted acknowledgement payloads advance the save sequence;
bytes inside telemetry packets do not.

Live polling remains suspended until the final EEPROM-save acknowledgement,
even after the configuration cursor finishes. The pending configuration command
is tracked independently from live polling, and each missing acknowledgement
fails the save after 10 seconds rather than leaving "Updating PDM" indefinitely.
Timeouts can mean settings were partially applied; read back the controller
before retrying. Enable-setting comparisons use the saved static snapshot,
not the effective on/off state reported by live telemetry.

The opt-in `ChannelConfigHardwareTests` temporarily changes one channel's
three-character name, saves and reads it back, then restores and verifies the
original name. It requires explicit permission:
`CORTEX_CHANNEL_CONFIG_TEST_CONFIRM=TEMPORARY_CHANNEL_NAME`,
`CORTEX_CHANNEL_CONFIG_TEST_PORT`, `CORTEX_CHANNEL_CONFIG_TEST_PIN` and
`CORTEX_CHANNEL_CONFIG_TEST_IDENTITY`. Each save must send exactly one changed
configuration packet; output states, protection thresholds and input assignments
are not changed.

The separate output-class hardware test requires
`CORTEX_CHANNEL_CONFIG_TEST_CLASS=TEMPORARY_OUTPUT_CLASS` plus the same port,
PIN and identity variables. With explicit permission, it exercises HVAC Blower
to Heated Steering Wheel with live polling enabled and restores the starting
class. It checks final save completion, automatic static readback, the exact
changed-packet count and continued authorization. This temporarily changes
load-shedding priority; do not enable it as part of ordinary test runs.

Encrypted PIN-management replies (set, change, disable, lock and status) share
the encrypted stream with live-polling replies. Cortex therefore matches them
by operation in the normal secure receiver, instead of reading the port
directly. Plain control requests, such as Info and Hello, wait for in-flight
replies to drain and accept only the expected operation; a throttled Hello is
retried once. Disable and Lock close the local session only when the controller
reports success, because that is when the firmware ends its session.

The opt-in `PinLockHardwareTests` cycles the PIN on and off with live polling
running, then restores the starting state. **Every PIN set rotates the recovery
key.** Run it only with explicit permission:
`CORTEX_PIN_LOCK_TEST_CONFIRM=DISABLE_AND_REENABLE_PIN`,
`CORTEX_PIN_LOCK_TEST_PORT`, `CORTEX_PIN_LOCK_TEST_PIN`,
`CORTEX_PIN_LOCK_TEST_IDENTITY`, `CORTEX_PIN_LOCK_TEST_RECOVERY_FILE` (outside
the repository; receives the latest recovery key) and optional
`CORTEX_PIN_LOCK_TEST_CYCLES`.
