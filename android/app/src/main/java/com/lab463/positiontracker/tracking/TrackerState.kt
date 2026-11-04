package com.lab463.positiontracker.tracking

import com.lab463.positiontracker.net.LinkHealth
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update

enum class Phase {
    /** Tracking is off. */
    Off,
    /** The service runs; no fix has been sent yet. */
    Starting,
    /** Fixes are recorded but the organizer has not started the event yet. */
    WaitingForEvent,
    /** Fixes reach the server. */
    Live,
    /** The server cannot be reached; fixes are queued. */
    Offline,
    /** The location is unavailable (no permission, location switched off). */
    NoLocation,
}

/** What the tracking service reports to the screen. Times are epoch milliseconds. */
data class TrackerSnapshot(
    val phase: Phase = Phase.Off,
    val raceMode: Boolean = false,
    val intervalSeconds: Long = 0,
    val lastFixAt: Long? = null,
    val lastAccuracyM: Double? = null,
    val lastSentAt: Long? = null,
    val queued: Int = 0,
    val sent: Int = 0,
    val rejected: Int = 0,
    val link: LinkHealth? = null,
    /** A problem code from the server or the service (`not_found`, `rejected_batch`, `fgs_denied`). */
    val problem: String? = null,
)

/** The service publishes here, the UI collects; both live in the same process. */
object TrackerBus {
    private val _state = MutableStateFlow(TrackerSnapshot())
    val state: StateFlow<TrackerSnapshot> = _state

    fun update(change: (TrackerSnapshot) -> TrackerSnapshot) = _state.update(change)

    fun reset() {
        _state.value = TrackerSnapshot()
    }
}
