package com.lab463.positiontracker.tracking

import android.Manifest
import android.annotation.SuppressLint
import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.location.Location
import android.os.BatteryManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.lab463.positiontracker.PositionTrackerApp
import com.lab463.positiontracker.R
import com.lab463.positiontracker.core.Times
import com.lab463.positiontracker.core.TrackingPlan
import com.lab463.positiontracker.core.UploadOutcome
import com.lab463.positiontracker.core.UploadPolicy
import com.lab463.positiontracker.data.EndReason
import com.lab463.positiontracker.data.Ended
import com.lab463.positiontracker.data.Pairing
import com.lab463.positiontracker.net.ApiClient
import com.lab463.positiontracker.net.ApiResult
import com.lab463.positiontracker.net.DeviceSession
import com.lab463.positiontracker.net.FixDto
import com.lab463.positiontracker.net.PositionBatch
import com.lab463.positiontracker.net.StatusDto
import com.lab463.positiontracker.ui.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.time.Instant

/**
 * The tracker: a foreground service of type `location`, started by the user while the app is visible (so the
 * "while in use" location permission is enough, no background location). It records fixes into the offline queue, sends
 * them in batches (`POST /api/events/{id}/positions`), follows the device session (`GET /api/device/session`) and stops
 * by itself for good when the event finishes (409 `event_finished` or the session state) or the token is revoked (401).
 * Before the own start minus [TrackingPlan.RACE_MODE_AHEAD] it takes a fix only every 30 s; a partial wake lock keeps the
 * sending going with the screen off. It also stops itself [TrackingPlan.MAX_AFTER_START] after the own start.
 */
class TrackingService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val app get() = application as PositionTrackerApp
    private lateinit var source: LocationSource
    private var wakeLock: PowerManager.WakeLock? = null
    private var pairing: Pairing? = null
    private var api: ApiClient? = null
    private var session: DeviceSession? = null
    private var intervalSeconds = 0L
    private var running = false
    private var ending = false
    private var loops: List<Job> = emptyList()

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        source = LocationSource.create(this)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            scope.launch { stopTracking(flush = true) }
            return START_NOT_STICKY
        }
        if (running) return START_STICKY
        val p = app.store.load()
        if (p == null) {
            stopSelf()
            return START_NOT_STICKY
        }
        if (!hasLocationPermission(this)) {
            app.store.trackingWanted = false
            TrackerBus.update { TrackerSnapshot(phase = Phase.NoLocation, problem = "no_permission") }
            stopSelf()
            return START_NOT_STICKY
        }
        try {
            ServiceCompat.startForeground(this, NOTIFICATION_ID, notification(getString(R.string.notif_starting)),
                if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_LOCATION else 0)
        } catch (e: Exception) {
            // Android does not let a location service start from the background (e.g. a restart after the app was killed).
            app.store.trackingWanted = false
            TrackerBus.update { TrackerSnapshot(phase = Phase.Off, problem = "fgs_denied") }
            notifyOnce(getString(R.string.notif_interrupted_title), getString(R.string.notif_interrupted_text))
            stopSelf()
            return START_NOT_STICKY
        }
        running = true
        pairing = p
        api = app.api(p.baseUrl)
        session = app.store.lastSession
        app.store.trackingWanted = true
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PositionTracker:tracking")
            .apply { setReferenceCounted(false); acquire(TrackingPlan.MAX_AFTER_START.plusHours(2).toMillis()) }
        TrackerBus.update { TrackerSnapshot(phase = Phase.Starting) }
        applyPlan()
        loops = listOf(
            scope.launch { sessionLoop() },
            scope.launch { uploadLoop() },
            scope.launch { tickLoop() },
        )
        return START_STICKY
    }

    override fun onDestroy() {
        source.stop()
        scope.cancel()
        wakeLock?.let { if (it.isHeld) it.release() }
        running = false
        super.onDestroy()
    }

    // ----- location -----

    private fun applyPlan() {
        val ownStart = session?.me?.startTime?.let(Times::parse)
        val now = Instant.now()
        val interval = TrackingPlan.intervalSeconds(now, ownStart, session?.tuning?.intervalSeconds ?: DEFAULT_INTERVAL_S)
        val raceMode = TrackingPlan.isRaceMode(now, ownStart)
        TrackerBus.update { it.copy(raceMode = raceMode, intervalSeconds = interval) }
        if (interval == intervalSeconds) return
        intervalSeconds = interval
        try {
            source.start(interval * 1000, ::onFix)
        } catch (e: SecurityException) {
            TrackerBus.update { it.copy(phase = Phase.NoLocation, problem = "no_permission") }
        }
    }

    private fun onFix(location: Location) {
        val accuracy = if (location.hasAccuracy()) location.accuracy.toDouble() else null
        scope.launch {
            val queued = withContext(Dispatchers.IO) {
                app.queue.add(location.time, location.latitude, location.longitude, accuracy)
                app.queue.count()
            }
            TrackerBus.update {
                it.copy(lastFixAt = System.currentTimeMillis(), lastAccuracyM = accuracy, queued = queued,
                    phase = if (it.phase == Phase.NoLocation) Phase.Starting else it.phase)
            }
        }
    }

    // ----- loops -----

    private suspend fun uploadLoop() {
        var failures = 0
        while (scope.isActive && !ending) {
            val p = pairing ?: return
            val batch = withContext(Dispatchers.IO) { app.queue.oldest(MAX_BATCH) }
            if (batch.isEmpty()) {
                delay(intervalSeconds.coerceAtLeast(1) * 1000)
                continue
            }
            val queued = withContext(Dispatchers.IO) { app.queue.count() }
            val body = PositionBatch(batch.map { FixDto(Times.utcIso(it.time), it.lat, it.lon, it.accuracy) }, deviceStatus(queued))
            val result = api!!.positions(p.deviceToken, p.eventId, body)
            val last = batch.last().id
            when (val outcome = UploadPolicy.decide(result, failures)) {
                is UploadOutcome.Sent -> {
                    failures = 0
                    val left = withContext(Dispatchers.IO) { app.queue.deleteThrough(last); app.queue.count() }
                    TrackerBus.update {
                        it.copy(phase = Phase.Live, lastSentAt = System.currentTimeMillis(), queued = left,
                            sent = it.sent + outcome.accepted, rejected = it.rejected + outcome.rejected, problem = null)
                    }
                    delay(intervalSeconds.coerceAtLeast(1) * 1000)
                }
                UploadOutcome.NotLive -> {
                    failures = 0
                    val left = withContext(Dispatchers.IO) { app.queue.deleteThrough(last); app.queue.count() }
                    TrackerBus.update { it.copy(phase = Phase.WaitingForEvent, queued = left, problem = null) }
                    delay(NOT_LIVE_WAIT_MS)
                }
                UploadOutcome.Finished -> { end(EndReason.Finished); return }
                UploadOutcome.Revoked -> { end(EndReason.Revoked); return }
                is UploadOutcome.Retry -> {
                    failures++
                    if (outcome.offline) TrackerBus.update { it.copy(phase = Phase.Offline, queued = queued) }
                    delay(outcome.waitSeconds * 1000)
                }
                is UploadOutcome.Drop -> {
                    failures = 0
                    val left = withContext(Dispatchers.IO) { app.queue.deleteThrough(last); app.queue.count() }
                    TrackerBus.update { it.copy(queued = left, problem = outcome.problem) }
                    delay(outcome.waitSeconds * 1000)
                }
            }
        }
    }

    private suspend fun sessionLoop() {
        while (scope.isActive && !ending) {
            val p = pairing ?: return
            when (val r = api!!.session(p.deviceToken)) {
                is ApiResult.Ok -> {
                    session = r.value
                    app.store.lastSession = r.value
                    TrackerBus.update { it.copy(link = r.value.link) }
                    if (r.value.event.state == "finished") { end(EndReason.Finished); return }
                    applyPlan()
                }
                is ApiResult.Http -> if (r.status == 401) { end(EndReason.Revoked); return }
                is ApiResult.Network -> Unit
            }
            delay(SESSION_POLL_MS)
        }
    }

    private suspend fun tickLoop() {
        while (scope.isActive && !ending) {
            applyPlan()
            val ownStart = session?.me?.startTime?.let(Times::parse)
            if (TrackingPlan.isOverdue(Instant.now(), ownStart)) {
                stopTracking(flush = true, problem = "overdue")
                return
            }
            updateNotification()
            delay(TICK_MS)
        }
    }

    // ----- ending -----

    /** The event finished or the token was revoked: stop for good, delete the queue and the pairing. */
    private suspend fun end(reason: EndReason) {
        if (ending) return
        ending = true
        source.stop()
        val name = pairing?.eventName.orEmpty()
        withContext(Dispatchers.IO) { app.queue.clear() }
        app.store.clear(Ended(name, reason))
        TrackerBus.reset()
        val (title, text) = when (reason) {
            EndReason.Finished -> getString(R.string.notif_finished_title) to getString(R.string.notif_finished_text, name)
            EndReason.Revoked -> getString(R.string.notif_revoked_title) to getString(R.string.notif_revoked_text)
        }
        notifyOnce(title, text)
        shutDown()
    }

    /** The user stopped (or the safety limit): sends what is queued (a few seconds at most), keeps the pairing. */
    private suspend fun stopTracking(flush: Boolean, problem: String? = null) {
        if (ending) return
        ending = true
        source.stop()
        val self = currentCoroutineContext()[Job]
        loops.filter { it !== self }.forEach { it.cancel() }
        val p = pairing
        if (flush && p != null) {
            withTimeoutOrNull(FLUSH_TIMEOUT_MS) {
                val batch = withContext(Dispatchers.IO) { app.queue.oldest(MAX_BATCH) }
                if (batch.isNotEmpty()) {
                    val body = PositionBatch(batch.map { FixDto(Times.utcIso(it.time), it.lat, it.lon, it.accuracy) }, deviceStatus(0))
                    if (api!!.positions(p.deviceToken, p.eventId, body) is ApiResult.Ok)
                        withContext(Dispatchers.IO) { app.queue.deleteThrough(batch.last().id) }
                }
            }
        }
        app.store.trackingWanted = false
        val left = withContext(Dispatchers.IO) { app.queue.count() }
        TrackerBus.update { TrackerSnapshot(phase = Phase.Off, queued = left, problem = problem) }
        shutDown()
    }

    private fun shutDown() {
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    // ----- status and notifications -----

    private fun deviceStatus(queued: Int): StatusDto {
        val battery = getSystemService(BatteryManager::class.java)
        val level = battery.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY).takeIf { it in 0..100 }?.toDouble()
        return StatusDto(level, battery.isCharging, queued, foreground = true)
    }

    @SuppressLint("MissingPermission") // checked by hasNotificationPermission(); a race is caught
    private fun updateNotification() {
        val s = TrackerBus.state.value
        val text = when (s.phase) {
            Phase.Live -> s.lastSentAt?.let {
                getString(R.string.notif_live, Times.span((System.currentTimeMillis() - it) / 1000))
            } ?: getString(R.string.notif_starting)
            Phase.WaitingForEvent -> getString(R.string.notif_waiting)
            Phase.Offline -> resources.getQuantityString(R.plurals.notif_offline, s.queued, s.queued)
            Phase.NoLocation -> getString(R.string.notif_no_location)
            else -> getString(R.string.notif_starting)
        }
        if (!hasNotificationPermission()) return
        try {
            NotificationManagerCompat.from(this).notify(NOTIFICATION_ID, notification(text))
        } catch (e: SecurityException) {
            // the permission was withdrawn meanwhile
        }
    }

    private fun notification(text: String): Notification =
        NotificationCompat.Builder(this, PositionTrackerApp.CHANNEL_TRACKING)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(getString(R.string.notif_title, pairing?.eventName.orEmpty()).trim())
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setContentIntent(openApp())
            .build()

    @SuppressLint("MissingPermission") // checked by hasNotificationPermission(); a race is caught
    private fun notifyOnce(title: String, text: String) {
        if (!hasNotificationPermission()) return
        val n = NotificationCompat.Builder(this, PositionTrackerApp.CHANNEL_EVENTS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setAutoCancel(true)
            .setContentIntent(openApp())
            .build()
        try {
            NotificationManagerCompat.from(this).notify(EVENT_NOTIFICATION_ID, n)
        } catch (e: SecurityException) {
            // the permission was withdrawn meanwhile
        }
    }

    private fun openApp(): PendingIntent = PendingIntent.getActivity(this, 0,
        Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    private fun hasNotificationPermission(): Boolean =
        Build.VERSION.SDK_INT < 33 ||
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    companion object {
        private const val ACTION_STOP = "com.lab463.positiontracker.STOP"
        private const val NOTIFICATION_ID = 1
        private const val EVENT_NOTIFICATION_ID = 2
        private const val MAX_BATCH = 500
        private const val DEFAULT_INTERVAL_S = 10
        private const val SESSION_POLL_MS = 60_000L
        private const val TICK_MS = 30_000L
        private const val NOT_LIVE_WAIT_MS = 30_000L
        private const val FLUSH_TIMEOUT_MS = 8_000L

        fun hasLocationPermission(context: Context): Boolean =
            ContextCompat.checkSelfPermission(context, Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED

        /** Starts tracking; call it only while the app is in the foreground. */
        fun start(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, TrackingService::class.java))
        }

        fun stop(context: Context) {
            context.startService(Intent(context, TrackingService::class.java).setAction(ACTION_STOP))
        }
    }
}
