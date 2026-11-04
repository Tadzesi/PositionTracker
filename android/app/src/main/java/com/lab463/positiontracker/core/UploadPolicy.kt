package com.lab463.positiontracker.core

import com.lab463.positiontracker.net.ApiResult
import com.lab463.positiontracker.net.IngestResult
import java.time.Duration
import java.time.Instant

/** What to do with a batch after the server answered `POST /positions`. */
sealed interface UploadOutcome {
    /** Taken: delete the batch. */
    data class Sent(val accepted: Int, val rejected: Int) : UploadOutcome

    /** The event is not live yet: delete the batch (the server will never take it) and wait. */
    data object NotLive : UploadOutcome

    /** The event has finished: stop tracking and forget the pairing. */
    data object Finished : UploadOutcome

    /** The token is no longer valid (set up on another phone, or removed by the organizer): stop and forget the pairing. */
    data object Revoked : UploadOutcome

    /** Keep the batch and send it again after [waitSeconds]; [offline] when the server could not be reached. */
    data class Retry(val waitSeconds: Long, val offline: Boolean) : UploadOutcome

    /** The server refused this batch for good: delete it, go on after [waitSeconds]. [problem] is shown to the user. */
    data class Drop(val waitSeconds: Long, val problem: String) : UploadOutcome
}

object UploadPolicy {
    /** [failures] is the number of failed attempts in a row before this one (for the back-off). */
    fun decide(result: ApiResult<IngestResult>, failures: Int): UploadOutcome = when (result) {
        is ApiResult.Ok -> UploadOutcome.Sent(result.value.accepted, result.value.rejected)
        is ApiResult.Network -> UploadOutcome.Retry(backoff(failures), offline = true)
        is ApiResult.Http -> when {
            result.status in 200..299 -> UploadOutcome.Sent(0, 0)
            result.status == 401 || result.status == 403 -> UploadOutcome.Revoked
            result.status == 409 && result.code == "event_finished" -> UploadOutcome.Finished
            result.status == 409 && result.code == "event_not_live" -> UploadOutcome.NotLive
            result.status == 429 -> UploadOutcome.Retry((result.retryAfterSeconds ?: 60).coerceIn(1, 300), offline = false)
            result.status == 503 -> UploadOutcome.Retry((result.retryAfterSeconds ?: backoff(failures)).coerceIn(1, 300), offline = false)
            result.status == 404 -> UploadOutcome.Drop(60, "not_found")
            result.status in 400..499 -> UploadOutcome.Drop(10, "rejected_batch")
            else -> UploadOutcome.Retry(backoff(failures), offline = false)
        }
    }

    /** 5, 10, 20, 40, 60, 60 … seconds. */
    fun backoff(failures: Int): Long = (5L shl failures.coerceIn(0, 4)).coerceAtMost(60)
}

/** How the phone tracks at a given time, from its own start (wave start) and the server's tuning. */
object TrackingPlan {
    /** Before this much ahead of the own start the phone only sends a fix every [WARMUP_INTERVAL_S] s to save battery. */
    val RACE_MODE_AHEAD: Duration = Duration.ofMinutes(10)
    const val WARMUP_INTERVAL_S = 30L

    /** Tracking switches itself off this long after the own start, even if the server never says "finished". */
    val MAX_AFTER_START: Duration = Duration.ofHours(12)

    fun intervalSeconds(now: Instant, ownStart: Instant?, raceIntervalSeconds: Int): Long {
        val race = raceIntervalSeconds.toLong().coerceIn(1, 60)
        if (ownStart == null) return race
        return if (now.isBefore(ownStart.minus(RACE_MODE_AHEAD))) maxOf(WARMUP_INTERVAL_S, race) else race
    }

    fun isRaceMode(now: Instant, ownStart: Instant?): Boolean =
        ownStart == null || !now.isBefore(ownStart.minus(RACE_MODE_AHEAD))

    fun isOverdue(now: Instant, ownStart: Instant?): Boolean =
        ownStart != null && now.isAfter(ownStart.plus(MAX_AFTER_START))
}
