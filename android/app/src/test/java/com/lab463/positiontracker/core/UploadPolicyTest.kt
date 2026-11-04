package com.lab463.positiontracker.core

import com.lab463.positiontracker.net.ApiResult
import com.lab463.positiontracker.net.IngestResult
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.time.Duration
import java.time.Instant

class UploadPolicyTest {
    private fun http(status: Int, code: String? = null, retry: Long? = null) = ApiResult.Http(status, code, retryAfterSeconds = retry)

    @Test
    fun `a taken batch is sent`() {
        assertEquals(UploadOutcome.Sent(3, 1), UploadPolicy.decide(ApiResult.Ok(IngestResult(3, 1)), 0))
    }

    @Test
    fun `the end of the race and a revoked token stop tracking`() {
        assertEquals(UploadOutcome.Finished, UploadPolicy.decide(http(409, "event_finished"), 0))
        assertEquals(UploadOutcome.Revoked, UploadPolicy.decide(http(401), 0))
        assertEquals(UploadOutcome.Revoked, UploadPolicy.decide(http(403), 0))
    }

    @Test
    fun `an event that is not live yet drops the batch and waits`() {
        assertEquals(UploadOutcome.NotLive, UploadPolicy.decide(http(409, "event_not_live"), 0))
    }

    @Test
    fun `limits and a busy server keep the batch and honour Retry-After`() {
        assertEquals(UploadOutcome.Retry(17, offline = false), UploadPolicy.decide(http(429, retry = 17), 0))
        assertEquals(UploadOutcome.Retry(60, offline = false), UploadPolicy.decide(http(429), 0))
        assertEquals(UploadOutcome.Retry(1, offline = false), UploadPolicy.decide(http(503, retry = 1), 0))
        assertEquals(UploadOutcome.Retry(5, offline = false), UploadPolicy.decide(http(500), 0))
    }

    @Test
    fun `no connection backs off up to a minute and keeps the batch`() {
        val net = ApiResult.Network(IOException("offline"))
        assertEquals(UploadOutcome.Retry(5, offline = true), UploadPolicy.decide(net, 0))
        assertEquals(UploadOutcome.Retry(40, offline = true), UploadPolicy.decide(net, 3))
        assertEquals(UploadOutcome.Retry(60, offline = true), UploadPolicy.decide(net, 10))
    }

    @Test
    fun `a batch the server refuses for good is dropped`() {
        assertEquals(UploadOutcome.Drop(10, "rejected_batch"), UploadPolicy.decide(http(400), 0))
        assertEquals(UploadOutcome.Drop(10, "rejected_batch"), UploadPolicy.decide(http(413), 0))
        assertEquals(UploadOutcome.Drop(60, "not_found"), UploadPolicy.decide(http(404), 0))
        // another 409 than finished / not live is not the end of the race
        assertEquals(UploadOutcome.Drop(10, "rejected_batch"), UploadPolicy.decide(http(409, "conflict"), 0))
    }

    @Test
    fun `the phone saves battery until ten minutes before its own start`() {
        val start = Instant.parse("2026-11-07T10:00:00Z")
        assertEquals(30, TrackingPlan.intervalSeconds(start.minus(Duration.ofMinutes(11)), start, 5))
        assertFalse(TrackingPlan.isRaceMode(start.minus(Duration.ofMinutes(11)), start))
        assertEquals(5, TrackingPlan.intervalSeconds(start.minus(Duration.ofMinutes(10)), start, 5))
        assertTrue(TrackingPlan.isRaceMode(start.minus(Duration.ofMinutes(10)), start))
        assertEquals(10, TrackingPlan.intervalSeconds(start, null, 10))
        assertEquals(60, TrackingPlan.intervalSeconds(start, start, 600))
    }

    @Test
    fun `tracking stops itself twelve hours after the own start`() {
        val start = Instant.parse("2026-11-07T10:00:00Z")
        assertFalse(TrackingPlan.isOverdue(start.plus(Duration.ofHours(12)), start))
        assertTrue(TrackingPlan.isOverdue(start.plus(Duration.ofHours(12)).plusSeconds(1), start))
        assertFalse(TrackingPlan.isOverdue(start.plus(Duration.ofDays(3)), null))
    }

    @Test
    fun `fix times are strict UTC with a Z`() {
        assertEquals("2026-11-07T10:00:05.123Z", Times.utcIso(Instant.parse("2026-11-07T10:00:05.123Z").toEpochMilli()))
        assertEquals("1 h 05 min", Times.span(3900))
        assertEquals("45 s", Times.span(45))
    }
}
