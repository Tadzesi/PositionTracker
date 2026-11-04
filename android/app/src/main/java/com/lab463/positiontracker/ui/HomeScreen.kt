package com.lab463.positiontracker.ui

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.location.LocationManager
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.core.net.toUri
import androidx.core.location.LocationManagerCompat
import androidx.lifecycle.compose.LifecycleResumeEffect
import com.lab463.positiontracker.PositionTrackerApp
import com.lab463.positiontracker.R
import com.lab463.positiontracker.core.Times
import com.lab463.positiontracker.data.EndReason
import com.lab463.positiontracker.data.Ended
import com.lab463.positiontracker.data.Pairing
import com.lab463.positiontracker.net.ApiResult
import com.lab463.positiontracker.net.DeviceSession
import com.lab463.positiontracker.net.LinkHealth
import com.lab463.positiontracker.tracking.Phase
import com.lab463.positiontracker.tracking.TrackerBus
import com.lab463.positiontracker.tracking.TrackerSnapshot
import com.lab463.positiontracker.tracking.TrackingService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.Instant

/** The phone checks the app needs; read again every time the screen comes back (the user may have changed a setting). */
private data class Checks(val location: Boolean, val locationOn: Boolean, val notifications: Boolean, val battery: Boolean)

private fun checks(context: Context): Checks = Checks(
    location = TrackingService.hasLocationPermission(context),
    locationOn = LocationManagerCompat.isLocationEnabled(context.getSystemService(LocationManager::class.java)),
    notifications = Build.VERSION.SDK_INT < 33 ||
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED,
    battery = context.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(context.packageName),
)

@SuppressLint("BatteryLife") // a race tracker is the case the exemption exists for; the app is not distributed by Google Play
@Composable
fun HomeScreen(app: PositionTrackerApp, pairing: Pairing, onChanged: () -> Unit) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val tracker by TrackerBus.state.collectAsState()
    var session by remember { mutableStateOf(app.store.lastSession) }
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var check by remember { mutableStateOf(checks(context)) }
    var confirmStop by remember { mutableStateOf(false) }
    var confirmUnpair by remember { mutableStateOf(false) }
    val running = tracker.phase != Phase.Off && app.store.trackingWanted
    // the user started tracking but the service is gone (the phone killed the app, or it restarted)
    val interrupted = tracker.phase == Phase.Off && app.store.trackingWanted

    LifecycleResumeEffect(Unit) {
        check = checks(context)
        onPauseOrDispose { }
    }
    // the clock of the "… ago" texts; also notices that the service ended the pairing (event finished)
    LaunchedEffect(Unit) {
        while (true) {
            now = System.currentTimeMillis()
            if (!app.store.isPaired()) { onChanged(); return@LaunchedEffect }
            delay(1000)
        }
    }
    // the device session: event state, own start and the server's light (the service polls it too while it runs)
    LaunchedEffect(pairing.deviceToken) {
        val api = app.api(pairing.baseUrl)
        while (true) {
            when (val r = api.session(pairing.deviceToken)) {
                is ApiResult.Ok -> {
                    session = r.value
                    app.store.lastSession = r.value
                    if (r.value.event.state == "finished" && !running) {
                        withContext(Dispatchers.IO) { app.queue.clear() }
                        app.store.clear(Ended(pairing.eventName, EndReason.Finished))
                        onChanged(); return@LaunchedEffect
                    }
                }
                is ApiResult.Http -> if (r.status == 401 && !running) {
                    app.store.clear(Ended(pairing.eventName, EndReason.Revoked))
                    onChanged(); return@LaunchedEffect
                }
                is ApiResult.Network -> Unit
            }
            delay(30_000)
        }
    }

    val startTracking = { TrackingService.start(context) }
    val permissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { granted ->
        check = checks(context)
        if (granted[Manifest.permission.ACCESS_FINE_LOCATION] == true) startTracking()
    }
    val askLocation = {
        val list = mutableListOf(Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION)
        if (Build.VERSION.SDK_INT >= 33) list += Manifest.permission.POST_NOTIFICATIONS
        permissions.launch(list.toTypedArray())
    }

    // ----- header -----
    val s = session
    val ownStart = s?.me?.startTime?.let(Times::parse)
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(s?.event?.name ?: pairing.eventName, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
        Mute("${pairing.participantName} · #${pairing.bib}")
        if (s != null) Mute(stringResource(R.string.home_own_start, Times.local(s.me.startTime)) +
            (ownStart?.let { st ->
                val left = Times.secondsBetween(Instant.ofEpochMilli(now), st)
                if (left > 0) " · " + stringResource(R.string.home_in, Times.span(left)) else ""
            } ?: ""))
    }

    StatusPanel(tracker, running, interrupted, s, now)

    // ----- the big button -----
    if (!running) {
        PrimaryButton(stringResource(R.string.home_start), enabled = check.locationOn, color = LocalSignals.current.green) {
            if (!check.location || !check.notifications) askLocation() else startTracking()
        }
        Mute(stringResource(R.string.home_start_hint))
    } else {
        PrimaryButton(stringResource(R.string.home_stop), color = LocalSignals.current.red) { confirmStop = true }
        Mute(stringResource(R.string.home_auto_stop))
    }

    // ----- phone checks -----
    Panel {
        Text(stringResource(R.string.checks_title), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        CheckRow(check.location, stringResource(R.string.check_location), stringResource(R.string.check_allow)) { askLocation() }
        CheckRow(check.locationOn, stringResource(R.string.check_location_on), stringResource(R.string.check_turn_on)) {
            context.startActivity(Intent(Settings.ACTION_LOCATION_SOURCE_SETTINGS))
        }
        CheckRow(check.notifications, stringResource(R.string.check_notifications), stringResource(R.string.check_allow)) { askLocation() }
        CheckRow(check.battery, stringResource(R.string.check_battery), stringResource(R.string.check_allow)) {
            try {
                context.startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, ("package:" + context.packageName).toUri()))
            } catch (e: Exception) {
                context.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
            }
        }
        vendorHint()?.let { Mute(it) }
    }

    Panel {
        Mute(stringResource(R.string.home_safety))
    }

    TextButton(onClick = { confirmUnpair = true }) { Text(stringResource(R.string.home_unpair)) }

    if (confirmStop) AlertDialog(
        onDismissRequest = { confirmStop = false },
        title = { Text(stringResource(R.string.home_stop_confirm_title)) },
        text = { Text(stringResource(R.string.home_stop_confirm_text)) },
        confirmButton = { TextButton(onClick = { confirmStop = false; TrackingService.stop(context) }) { Text(stringResource(R.string.home_stop)) } },
        dismissButton = { TextButton(onClick = { confirmStop = false }) { Text(stringResource(R.string.cancel)) } },
    )
    if (confirmUnpair) AlertDialog(
        onDismissRequest = { confirmUnpair = false },
        title = { Text(stringResource(R.string.home_unpair)) },
        text = { Text(stringResource(R.string.home_unpair_text)) },
        confirmButton = {
            TextButton(onClick = {
                confirmUnpair = false
                if (running) TrackingService.stop(context)
                scope.launch {
                    withContext(Dispatchers.IO) { app.queue.clear() }
                    app.store.clear(null)
                    TrackerBus.reset()
                    onChanged()
                }
            }) { Text(stringResource(R.string.home_unpair)) }
        },
        dismissButton = { TextButton(onClick = { confirmUnpair = false }) { Text(stringResource(R.string.cancel)) } },
    )
}

@Composable
private fun StatusPanel(t: TrackerSnapshot, running: Boolean, interrupted: Boolean, session: DeviceSession?, now: Long) {
    val sig = LocalSignals.current
    val (color, title) = when {
        !running && (interrupted || t.problem == "fgs_denied") -> sig.red to stringResource(R.string.status_interrupted)
        !running && t.problem == "overdue" -> sig.mute to stringResource(R.string.status_overdue)
        !running -> sig.mute to stringResource(R.string.status_off)
        t.phase == Phase.Live -> sig.green to stringResource(R.string.status_live)
        t.phase == Phase.WaitingForEvent -> sig.yellow to stringResource(R.string.status_waiting_event)
        t.phase == Phase.Offline -> sig.red to pluralStringResource(R.plurals.status_offline, t.queued, t.queued)
        t.phase == Phase.NoLocation -> sig.red to stringResource(R.string.status_no_location)
        else -> sig.blue to stringResource(R.string.status_starting)
    }
    Panel {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.background(color, RoundedCornerShape(6.dp)).padding(horizontal = 4.dp, vertical = 14.dp)) {}
            Spacer(Modifier.width(12.dp))
            Text(title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
        }
        if (session?.event?.state == "ready" || session?.event?.state == "draft") Mute(stringResource(R.string.status_event_not_started))
        if (running && !t.raceMode && t.intervalSeconds > 0) Mute(stringResource(R.string.status_warmup, t.intervalSeconds))
        if (running) {
            KeyValue(stringResource(R.string.kv_last_fix), t.lastFixAt?.let {
                stringResource(R.string.ago, Times.span((now - it) / 1000)) + (t.lastAccuracyM?.let { a -> " · ±${a.toInt()} m" } ?: "")
            } ?: "–")
            KeyValue(stringResource(R.string.kv_last_sent), t.lastSentAt?.let { stringResource(R.string.ago, Times.span((now - it) / 1000)) } ?: "–")
            KeyValue(stringResource(R.string.kv_queued), t.queued.toString())
        }
        val link = t.link ?: session?.link
        if (link != null) ServerLight(link)
        t.problem?.let { p ->
            when (p) {
                "not_found" -> Text(stringResource(R.string.problem_not_found), color = sig.red)
                "rejected_batch" -> Text(stringResource(R.string.problem_rejected), color = sig.red)
                "no_permission" -> Text(stringResource(R.string.status_no_location), color = sig.red)
                else -> Unit
            }
        }
    }
}

/** The organizer's side of the link: the same light the participant page shows. */
@Composable
private fun ServerLight(link: LinkHealth) {
    val sig = LocalSignals.current
    val (color: Color, text) = when (link.level) {
        "good" -> sig.green to stringResource(R.string.light_good)
        "warning" -> sig.yellow to stringResource(R.string.light_warning)
        "silent" -> sig.red to stringResource(R.string.light_silent)
        else -> sig.mute to stringResource(R.string.light_waiting)
    }
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().padding(top = 4.dp)) {
        Dot(color)
        Spacer(Modifier.width(10.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium)
    }
    link.problems.forEach { p ->
        val hint = when (p) {
            "clock" -> stringResource(R.string.hint_clock)
            "accuracy" -> stringResource(R.string.hint_accuracy)
            "lowBattery" -> stringResource(R.string.hint_battery)
            "rejected" -> stringResource(R.string.hint_rejected)
            else -> null
        }
        hint?.let { Mute("• $it") }
    }
    if (link.earlyOnly) Mute(stringResource(R.string.hint_early))
}

/** Extra advice for makers known to stop background apps despite the battery exemption (dontkillmyapp.com). */
@Composable
private fun vendorHint(): String? = when (Build.MANUFACTURER.lowercase()) {
    "xiaomi", "redmi", "poco" -> stringResource(R.string.vendor_xiaomi)
    "huawei", "honor" -> stringResource(R.string.vendor_huawei)
    "samsung" -> stringResource(R.string.vendor_samsung)
    "oneplus", "oppo", "realme", "vivo" -> stringResource(R.string.vendor_oppo)
    else -> null
}
