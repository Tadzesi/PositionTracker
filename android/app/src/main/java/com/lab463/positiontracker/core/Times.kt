package com.lab463.positiontracker.core

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.temporal.ChronoUnit
import java.util.Locale

object Times {
    /** The strict UTC form the server expects for fix times: `2026-11-07T10:00:05.123Z`. */
    fun utcIso(epochMillis: Long): String =
        DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.ROOT)
            .withZone(java.time.ZoneOffset.UTC)
            .format(Instant.ofEpochMilli(epochMillis))

    fun parse(iso: String): Instant? = try { Instant.parse(iso) } catch (e: Exception) { null }

    /** Local date and time for the UI, e.g. `7. 11. 2026 10:00` (Slovak) or `07/11/2026 10:00`. */
    fun local(iso: String, locale: Locale = Locale.getDefault()): String {
        val instant = parse(iso) ?: return iso
        val pattern = if (locale.language == "sk") "d. M. yyyy H:mm" else "dd/MM/yyyy HH:mm"
        return DateTimeFormatter.ofPattern(pattern, locale).withZone(ZoneId.systemDefault()).format(instant)
    }

    /** `1 h 05 min`, `12 min`, `45 s` — for countdowns and ages. */
    fun span(seconds: Long): String {
        val s = seconds.coerceAtLeast(0)
        return when {
            s >= 86_400 -> "${s / 86_400} d ${(s % 86_400) / 3600} h"
            s >= 3600 -> "${s / 3600} h ${"%02d".format((s % 3600) / 60)} min"
            s >= 60 -> "${s / 60} min"
            else -> "$s s"
        }
    }

    fun secondsBetween(from: Instant, to: Instant): Long = ChronoUnit.SECONDS.between(from, to)
}
