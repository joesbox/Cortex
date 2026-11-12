# Cortex Details

This document contains detailed implementation, transport, and hardware-test notes that were previously in the top-level README.

## I/O configuration

I/O Flow auto-arrange leaves 240 pixels of horizontal clearance between node
columns; vertical spacing is unchanged.

Configuration files include persisted Custom CAN message and signal records,
including TX trigger, mode and period. Loading stages them alongside the other
settings; Save to controller writes the changed records. Older files that only
embedded these records in the I/O Flow signature recover them from that
signature before graph validation. Live CAN status is not restored from files.

Local layout saves include both standalone logic-node positions and their
controller channel/rule-slot aliases. Controller reloads reconstruct rules by
channel and slot rather than local logic ID; the aliases preserve their exact
positions and canvas membership across restarts without saving configuration
locally. Each canvas keeps its own viewport and node coordinates.

I/O Flow is the only visible I/O editor and is selected when Settings opens.
The legacy I/O tab is hidden and disabled; CAN Messages, Connector, System and
the shared Save/Revert controls remain available.

Nodes are read-only summary cards with connectors, a 20-point title and 16-point
configuration summaries. Digital/analogue inputs, output channels and decoded
CAN RX/system-value nodes include a rolling ten-second mini chart; logic, CAN TX,
ignition and system CAN nodes do not. Logic results use a prominent text readout.
Select one node to edit its existing
settings in the resizable, scrollable Node properties panel on the right.
Every node type uses the output editor's shared setting cards, with 20-point
headings, 14-point labels/controls, matching padding, borders and spacing.
Canvas nodes share the properties panel's solid purple surface, rounded frame
and inset setting cards, while retaining type-coloured borders and selection glows.
All output modes share the same green (`#68C579`) in node borders, selection
glows, connector pins and connector legends. Subtype labels remain distinct;
analogue inputs use yellow (`#E3C85C`), while digital inputs, ignition and CAN
retain their separate palette colours.
Title, summary and mini-chart cards share the same width and aligned edges,
with connectors in separate side columns. Mini charts fill their card below
the summary, with a 140-pixel chart height. Both axes have major and minor grid lines matching the
main live chart's grey separator colours and stroke weights.
Connection ports are discreet vertical pills (6 by 18 pixels) within transparent
10 by 24 pixel hit targets. Four-pixel side gaps leave more width for the cards.
Ports use dark muted purple; wires and drag previews use moderately bright, desaturated purple with
matching arrow fills, brighter hover feedback and a subdued selected-wire glow.
An empty or multiple selection shows a selection prompt, not an arbitrary node's
settings. Selection and live chart updates do not stage controller changes;
configuration still uses the shared Save/Revert lifecycle.

Mini charts use timestamped controller telemetry: digital electrical state,
analogue value (or voltage when no scaled value is reported), output current,
decoded CAN/system values. Every series is a single green, unfilled line path
with flat caps and no point markers; missing readings start separate path figures.
Series have no per-sample strokes or dots, and
chart labels use 14-point text. The properties panel retains the full per-output
logic runtime details. Missing, invalid and
timed-out readings leave gaps. Charts age out old samples even when telemetry
stops, keep at most 101 samples at 100 ms spacing, and reset when a source
assignment or analogue mode/units changes. Chart history is transient and is
not saved in controller settings or local layout snapshots.

Analogue voltage is connector-side (0-5 V), already scaled by firmware using
`MCU pin volts * (5.0 / 3.3)`; Cortex never applies the divider gain again.
Raw-voltage and analogue-digital mini charts have a fixed 0-5 V range, as does
the main live chart's voltage axis when it contains only connector-voltage
series. Calibrated/NTC values retain their engineering units and dynamic ranges;
a voltage axis shared with calibrated voltage values remains dynamic.
Output thresholds/scales and calibration voltage points use connector volts.
Connector/status readouts and log values consume the reported values unchanged,
including any out-of-range readings rather than silently clamping telemetry.
Log charts retain their shared dynamic value axis for mixed units.

System-value nodes and logic operand readouts support all 14 firmware sources:
system/SIM/IMU temperatures, battery voltage, total current, and all three axes
of acceleration (G), gyroscope (degrees/second) and magnetic field (microtesla).
The live-status reply includes the nine IMU axes as an optional 36-byte suffix.
Older firmware omitting it leaves these readouts unavailable and mini charts
empty; missing data is not displayed as zero. Session loss clears the IMU values.

Analogue, digital, decoded CAN RX and system-value nodes have source assignment
selectors in the properties panel. Reassignment preserves node positions and retargets both operands
and physical-output wires, including hidden configured rules. Input settings
belong to the selected physical channel and are not copied from the old channel.
Sources already represented by another node are excluded. An input change that
cannot preserve a connected output's mode is rejected with an explanation.
CAN RX choices are enabled decoded receive signals; an unavailable current
assignment remains visible so it can be repaired. Ignition and system CAN are
fixed hardware/protocol sources, not numbered input channels.

Wiring determines the output mode. Output properties offer only source-compatible
sub-options: digital/PWM/intermittent, analogue threshold/scaled PWM, CAN
digital/PWM, or maths digital/PWM. There is no general output-type dropdown,
connection summary, add-rule button or maths output-effect readout on outputs.
Output properties retain category/name, current limits, inrush, retries, applicable
PWM duty/ranges and timing settings. Protection and timing settings are available
for maths and unconnected outputs. Analogue threshold
direction is selectable; analogue source properties include calibration ranges and
NTC beta/nominal resistance, including for logging-only sources.
Connecting an input enables the output; deleting the input wire disables it
without clearing protection or timing settings. Unconnected outputs remain
configurable. The LED and status text show
active, inactive, disabled, over/under current, driver fault, retry lockout,
over temperature or maths error. Live activity is separate from stored enable.
"Disabled" means no input source is configured: no input pin, and not a CAN or
rule-based type. An output that has an input but is off, including an active-low
analogue input at 5 V, shows "Inactive". The check uses the serial service's
confirmed static controller snapshot, because the static settings copy inside
the live data view is never refreshed. Until a snapshot loads, it falls back to
the editor's input.
The controller derives the channel `Enabled` flag from "has an input source" at
load and on every input pass. Cortex never sends it (channel parameter 4 is
ignored by the controller), and config files no longer store it. Cortex reads it
only for display.
The output-channel selector lists only channels not already on the canvas, and
refuses a channel that is already in use. One input node (digital, analogue or
rule) can drive several outputs. Wiring a rule root to a second output makes that
output a follower of the owning output's rules. This uses channel parameter 46
(follow channel). On the controller, it is stored as `RuleCount=0` and
`RootRule=0x80|owner`. Followers keep their own digital/PWM mode, current limits
and timing, and share the owner's result, including toggle and hysteresis state.
Removing a follower's wire disables only that output. Removing the owner's wire
promotes the first follower to own the rules and re-points the rest. Deleting
the rule nodes disconnects every follower. Controllers without parameter 46
reject follower configurations.
Use Add maths node to build rules and chains. The rule directly wired to an
output supplies its final result; reconnecting selects that rule automatically.
The internal controller root index is not exposed in flow text.
Select/delete rule nodes to remove them. Comparison nodes expose hysteresis;
ON/OFF thresholds appear only while it is enabled. Unsupported settings are
hidden, including units/pull resistors for incompatible input modes and timing
durations for disabled timers/ramps. CAN TX nodes select stored messages, enable/disable them
without removing trigger assignments, and stage mode/period edits immediately.
There is no separate Apply step: Save to controller persists these pending
changes. CAN TX nodes use dark blue, distinct from the other I/O node colours.
Connecting additional TX nodes adds assignments; deleting a node/wire removes
that assignment. Frame/signal definitions remain in CAN Messages.

Standalone serial reads (including firmware/version queries after connection)
reset receive buffers, not loaded settings flags. Clearing those flags stopped
live polling after initial hydration. Only actual controller session loss clears
snapshot state. Standalone ACK writes, build-date queries, firmware diagnostics
and log resets share the request gate, and queries preserve an enclosing
session's polling suspension. Override packets use their own packet buffer.
Parser dispatch is serialized across normal reception and log recovery.
Failed connection attempts dispose the serial service and its parser task.

Background cellular health checks skip busy transports rather than queueing
behind another request. They request status without sending cellular settings,
so monitoring does not save configuration or reset the modem connection test.
Secure-stream status checks leave buffered frames intact. Configuration-save, firmware-update and log-transfer
startup wait for an outstanding standalone request before suspending polling.
Secure-stream input is read in batches so large encrypted CAN snapshots do not
stall live telemetry while bytes are processed one at a time.
Configuration saves fail after a missing acknowledgement instead of blindly
retrying a potentially applied write; a late final ACK cannot turn that failure
into success. Read back the controller before retrying a timed-out save.

Math status polling follows all controller math channels rather
than the legacy editor's selected channel. A read-only COM18 test reproduced
the freeze before this fix and verified continued live and math updates for a
minute in I/O Flow after opening Settings and switching through CAN Messages.
Tab entry did not create pending configuration changes. No configuration or
output commands were sent during this check.

The log map reconnects its view-model subscriptions whenever the log view is
reattached, including after switching away and back. Loaded GPS data therefore
continues to refresh the route without needing a new data context. Logs with
only one valid GPS time bucket display a marker without constructing an invalid
single-coordinate route line. Headless UI regressions cover both cases using
synthetic coordinates.

Live polling keeps exactly one `r` request in flight. The next `r` is chained
only after its live response arrives, not after digital, math or CAN status
frames. Previously every received frame chained a request, so each periodic
status reply added a permanent extra request. On COM18 that grew by about 4
requests every 3 seconds to more than 630 live frames/s, and the live view and
I/O Flow mini charts fell further and further behind. Outstanding requests are
assumed lost after 1 s. The 50 ms UI timer queues at most one dispatcher update
and reads the newest sample when that update runs.

Maths runtime details use separate, vertically stacked operand A, operand B and
rule-result sections in the properties panel. Unary operations
hide operand B. Fixed panel/node widths and wrapped text keep changing telemetry
from resizing the layout horizontally; output effects and rule usage remain in
their own sections. Results come from controller telemetry, not local evaluation.
There is no combined all-rule runtime summary.
Maths outputs expose Digital/PWM sub-options: the
connected rule result determines ON/OFF or a duty cycle clamped to 0-100%, not a fixed
PWM slider value. Selected nodes keep their normal background and gain a glow
matching their type-specific border colour; selected wires use a muted purple glow.
Node glows follow the rounded corners and fade outward without a solid expanded outline;
node cards and containers do not clip, so the glow can render beyond the card edge.
Unselected wires are muted purple and brighten on hover. Click a wire and press Delete or
right-click it to remove the connection. Clicking empty canvas clears the
selection. Wires follow connector
positions throughout node drags, including when live updates resize a node.
One digital, analogue or ignition input can drive several outputs: wiring it to another
output adds a link and keeps its existing output links (each output still has one input).

Live readings (output current, analogue voltage/value, maths operands and results,
CAN/system sources, connector pin values and live chart legends/tooltips) are shown to
one decimal place; configured constants keep their entered precision. Output current
thresholds and the inrush current limit snap to 0.1 A (100 mA) steps. Every I/O Flow
slider in the properties panel fills its setting card and shows its value right-aligned beside it in a column with a minimum width sized for the widest expected value, so changing
values never resize the slider. Each output parameter (its title, controls and value) sits in its own subtle card; the Delayed OFF trigger lives in the Delayed OFF card. The Retry count card spans the full panel width unless a PWM output shows the Duty card, which then takes the left half.
Clicking a slider value (in I/O Flow and the System current limit) opens an inline box for typing an exact value. Enter or clicking away applies it, and Escape cancels. A unit suffix such as `A`, `s`, `ms` or `%` is optional, and `,` is accepted as the decimal separator. Non-numbers and values outside the slider's current range are rejected with a red border and a tooltip. Clicking away with an invalid value reverts it. Accepted values snap to the slider step and then pass through the same view-model clamping and rounding as dragging. Keys typed in the box never reach canvas shortcuts such as Delete.

I/O Flow supports up to 20 named canvases. The canvas tabs sit along the top edge of the
canvas frame, with the selected tab joined to the canvas; hovering a tab previews the
same joined look without the selection accent bar. Use them to switch,
the "+" tab to create one, and right-click Delete to remove an empty canvas; at least
one canvas must remain. New nodes appear on the selected canvas.
Right-click a canvas tab and choose Rename to edit its name in place. Enter or
clicking away commits; Escape cancels. A tooltip explains this after a two-second hover.
Deleting a nonempty canvas shows a warning and leaves it intact.
Node identities
and controller settings are shared across canvases, so changing canvases does
not create duplicate physical inputs or outputs.
Select nodes and right-click "Move selected nodes to canvas" to choose a
destination. The entire connected group moves, including indirect connections;
confirmation is required if connected nodes were not selected. Moves place the
group in free space near the destination canvas's viewport, preserving relative
node positions and connections. If nearby space is occupied, the group is placed
below the existing nodes rather than overlapping them.
Auto arrange affects only the current canvas.
Canvas names, membership, selected canvas, node positions, and each canvas's pan
position and zoom are stored in the
local layout file and restored across sessions, independently of controller
configuration. They are also included in saved configuration files and restored
when those files are loaded. Older files without canvas metadata use a single canvas.
The output node's Active label and live LED are in its header.

A minimap below the properties panel in the right-hand sidebar shows only the current canvas's nodes.
The properties panel uses the remaining height and scrolls independently, keeping the canvas unobstructed.
Nodes use their border colour without a viewport tint. The dark-grey, gridded
background fills the preview with a fixed eight-pixel inset, regardless of node
positions, panning or zoom. The world extent expands to the preview's aspect
ratio rather than shrinking the background; nodes retain their proportions
and move relative to the viewport marquee. The visible viewport has the
application's pink accent outline. Click or drag in
the overview to pan without changing zoom, node positions or controller settings.

Math rules have separate A/B input connectors (B is hidden for unary operations).
Every nonconstant operand has a source node and a wire, including imported
settings and controller readback. Source selectors offer only sources
currently wired to the rule plus an inline constant. Reconnecting or deleting
an operand wire changes only that operand; the same source can feed both A and B.
Deleting a source replaces its uses with constant zero and updates the pending
controller rules, including channels not currently displayed.
Analogue nodes may remain unconnected for calibration, CAN telemetry and logging
configuration; their settings, canvas membership and local positions persist
without an output assignment, including after saving or reconnecting.
Decoded CAN RX signals each have their own math-only source node, separate from
the system CAN input used for CAN-controlled outputs. System values also have
math-only source nodes. The context menu adds these sources. Disabled/non-RX
signal sources are visibly unavailable, and saving a rule using one is rejected.
The current controller protocol accepts only output-channel values or constants
as custom CAN TX trigger/payload sources, not raw analogue or digital inputs.

Settings-model edits and channel-owned I/O Flow rule nodes share the same
configuration. Editing a compiled standalone chain through the settings model
replaces its private flow copies with channel-owned nodes at the same positions;
nodes still used by another chain remain available. Deleting a rule removes it
from the pending controller configuration, compacts rule indices and replaces
operands referencing that deleted rule with constant zero. Deleting a standalone
output root unlinks its channel. Deleted inactive slots are cleared.
Save to controller uses a captured configuration; edits made during a save remain
pending rather than being reported as saved. Controller settings refresh waits
for a fresh math status response before loading math rules, including after a
save, so cached telemetry cannot restore deleted rules.
Local I/O Flow persistence stores only canvas layout metadata and node positions,
not input calibration, output settings, maths rules or CAN definitions/assignments.
Unsaved controller edits are discarded on restart; layout is applied only to
matching nodes rebuilt from the current settings. Legacy local drafts are migrated
to layout-only files without restoring their controller edits.
While waiting for initial controller and maths settings, layout saves retain
positions for nodes not loaded yet, including their canvas membership. Loading
an explicit configuration or reverting changes replaces the layout instead.
During the same application session, unsaved edits are retained in memory across
reconnects. After fresh static, maths and custom CAN snapshots arrive, an unchanged controller baseline
restores the draft automatically; an independently changed controller prompts
for Restore Draft or Use Controller. Restoration stages edits, never writes to
the PDM automatically. Already-applied drafts recover their layout without
requiring another save. Revert Changes explicitly discards the draft.
Using Controller requires explicit confirmation to discard the in-memory draft;
cancelling that confirmation defers the choice until reconnect.
Local files are replaced atomically and persistence errors are logged.
Configuration signatures exclude runtime values and cellular/system credentials.
Restored standalone rule chains are validated and compiled before dirty-state
comparison and saving. The pending-change warning and Save button use the same
dirty state. A maths node displays pending/not applied instead of controller
results whenever the live rule count, operands, operation, result rule or output
mode differs from its configured channel.
Graph refresh preserves compatible input assignments even when their source
nodes are not on the canvas. Explicitly deleting an input node clears its output
assignments; changing a channel type clears incompatible assignments. Connection
undo restores the compatible channel type before restoring its input pin.

Application tooltips appear only after the mouse has remained stationary for at
least 400 ms (or the control's longer hover delay). Movement hides the current
tooltip and restarts the delay, including movement within a table. Clicking,
scrolling, typing, leaving a window or deactivating it dismisses the tooltip.
Existing tooltip content and `ToolTip.ShowOnDisabled` behavior are preserved.

I/O Flow node positions are local layout preferences. Revert Changes restores
the baseline graph and controller settings, including removing unused nodes
added since the baseline, without moving nodes that remain on the canvas.
Restored deleted nodes use their baseline positions. The resulting layout is
saved locally for the next session.
Ctrl+Z (or Undo) reverses the last 10 completed node moves, auto-arranges and
connection additions/removals within I/O Flow. A multi-node drag is one action;
clicks and cancelled/no-op changes do not consume history. Connection undo
restores the settings changed by that connection, not unrelated subsequent
edits. CAN TX undo uses the normal pending-settings update and retains the action for
retry if it fails. History is session-only and resets on graph reload/revert,
save, node removal or output/CAN message reassignment. If affected settings
were edited independently, stale history is cleared with an explanation rather
than overwriting those edits. Text fields retain their normal text undo.
Undo is unavailable during an active drag or controller connection update.
Undoing a move saves the restored layout locally without marking controller
settings dirty; undoing all settings changes back to the baseline clears the
pending-change warning.
Node locations use a local two-way container binding so Nodify's drag updates
do not override subsequent undo, auto-arrange or restored-layout positions.

CAN TX connections and mode/period edits in I/O Flow are pending
configuration changes. Neither writes to the PDM until Save to controller;
Revert Changes discards them. Nodes and CAN Messages share enabled state,
trigger channel, mode and period. Create TX nodes explicitly in I/O Flow.
Creating a stored TX message or changing
its direction to TX does not add a node. Changing a message to RX removes its TX
node without moving other nodes.
The I/O Flow period slider is visible only in periodic mode and accepts
100-30,000 ms in 100 ms steps, matching channel settings. One-shot ignores the
period and retains its stored value for future periodic use.
Stored CAN definitions load automatically after the initial static snapshot on
each connection, without opening CAN Messages. Continuous CAN status polling
remains limited to the visible CAN tab.

Deleting a CAN TX node disables its message and clears its channel assignment,
marking the configuration pending for Save to controller. It does not delete the
stored CAN frame or signals. Revert restores the assignments and nodes.
Each assigned TX message has its own node and output-channel wire. Only stored
TX messages are offered (RX messages are excluded). Each node edits its own
enabled state and mode/period without changing another assignment.

Deleting a stored CAN message also clears its TX role and channel assignment,
removing its I/O Flow node/wire immediately.
Deleting its last stored signal or clearing all stored signals has the same
effect; deleting only some signals retains the message assignment. Deleted
messages are not offered for re-assignment. A channel with no remaining assigned
messages shows CAN TX disabled. These deletions remain pending until saved and
can be reverted.

Every enabled, channel-assigned TX message is represented by a node and wire,
including after repeated removal and re-addition. New nodes avoid existing node
bounds using measured dimensions when available and conservative size estimates
before rendering. CAN TX placement reserves room for periodic controls. When
the current viewport has no free space, placement extends the canvas below the
existing nodes instead of stacking new nodes at an occupied position.

The DBC Import tab's Signal name field filters the signal list as you type,
using a case-insensitive substring match on signal names. Clear the field to
show all signals. Hidden signals are removed from the import selection. The
filter also applies when loading another DBC file.

Incremental DBC imports match stored frames by message name, CAN ID and
standard/extended ID type. Additional signals use that frame only when their
Intel/Motorola bit positions are free and fit its existing DLC. Frame direction,
enable state, timing and TX trigger are retained, even if changed since the first
import. An identical signal reimport reuses its slot and retains its TX value
source. Deleted/disabled signals do not reserve bits. Overlapping signals,
conflicting message names, ambiguous frame matches and out-of-DLC layouts are
reported instead of creating duplicate frames or changing existing settings.
A uniquely matching frame with a placeholder name can also be reused.

The divider between CAN Messages' import/manual-entry and stored-message
containers can be dragged vertically (or adjusted with Up/Down when focused).
Their proportions are saved in `%LOCALAPPDATA%\Cortex\can-messages-layout.json`
and restored across sessions. Both sections retain a minimum usable height;
resizing the window keeps the selected proportions wherever space permits.
This is a local layout preference and does not change controller configuration.
CAN Messages and Live Data share the same thin divider and centered grip.
Live Data's divider also saves its proportions independently in
`%LOCALAPPDATA%\Cortex\live-data-layout.json`, retaining its existing default
ratio and resize limits. Previously saved CAN layout preferences are still read.

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
Bulk-read waits observe the stall deadline even when a stream ignores its
cancellation token. A stall closes the port to abort the native read and
requires reconnecting; it does not attempt chunk fallback on the closed port.
For other recoverable legacy bulk failures, chunk fallback reopens the selected
log before reading, so the file starts at the beginning rather than a partial
bulk-transfer offset. Physical Windows serial-driver behavior still requires
the opt-in hardware checks.

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
