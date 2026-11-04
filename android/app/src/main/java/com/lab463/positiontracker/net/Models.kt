package com.lab463.positiontracker.net

import kotlinx.serialization.Serializable

// The JSON of the PositionTracker API (camelCase). Unknown fields are ignored, so the server may add fields.

/** `GET /api/p/{linkToken}`: the participant's invitation. */
@Serializable
data class LinkView(val event: LinkEvent, val me: LinkMe, val language: String = "sk")

@Serializable
data class LinkEvent(
    val id: String,
    val name: String,
    val location: String = "",
    val startTime: String,
    val sport: String = "",
    val finished: Boolean = false,
)

@Serializable
data class LinkMe(
    val id: String,
    val name: String,
    val bib: String,
    val category: String? = null,
    val gender: String? = null,
    val nickname: String? = null,
    /** Invited, Confirmed, Declined or EmailFailed. */
    val status: String,
    val phoneSetUp: Boolean = false,
)

@Serializable
data class ProfileRequest(val nickname: String? = null, val gender: String? = null)

@Serializable
data class ConsentRequest(val locationConsent: Boolean, val showNameToSpectators: Boolean)

/** `POST /api/p/{linkToken}/consent`: the device token is returned only here. */
@Serializable
data class ConsentAnswer(val deviceToken: String, val eventId: String)

/** `GET /api/device/session` (bearer device token). Times are UTC ISO 8601. */
@Serializable
data class DeviceSession(
    val eventId: String,
    val event: SessionEvent,
    val me: SessionMe,
    val tuning: Tuning,
    val link: LinkHealth? = null,
    val serverTime: String,
)

@Serializable
data class SessionEvent(
    val name: String,
    val location: String = "",
    val sport: String = "",
    /** draft, ready, live or finished. */
    val state: String,
    val startTime: String,
)

@Serializable
data class SessionMe(val name: String, val bib: String, val nickname: String? = null, val startTime: String)

@Serializable
data class Tuning(val intervalSeconds: Int = 10, val displacementM: Int = 10, val ignoreInaccurateM: Int = 50)

/** The data-link light of the server: is the phone's data arriving? */
@Serializable
data class LinkHealth(
    /** waiting, good, warning or silent. */
    val level: String,
    val lastBatchAgoSeconds: Double? = null,
    val lastPositionAgoSeconds: Double? = null,
    val accuracyM: Double? = null,
    val clockAheadSeconds: Double? = null,
    val batteryPct: Double? = null,
    /** clock, accuracy, lowBattery, rejected. */
    val problems: List<String> = emptyList(),
    val earlyOnly: Boolean = false,
)

/** `POST /api/events/{eventId}/positions`. */
@Serializable
data class PositionBatch(val fixes: List<FixDto>, val status: StatusDto)

@Serializable
data class FixDto(val t: String, val lat: Double, val lon: Double, val acc: Double? = null)

@Serializable
data class StatusDto(val battery: Double?, val charging: Boolean?, val queue: Int, val foreground: Boolean)

@Serializable
data class IngestResult(val accepted: Int = 0, val rejected: Int = 0)

/** Self-install update manifest next to the APK: [url] is the download page, [apk] the APK itself (since 1.0.2). */
@Serializable
data class UpdateInfo(val versionCode: Int, val versionName: String, val url: String, val apk: String? = null)
