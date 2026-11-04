package com.lab463.positiontracker.ui

import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.lab463.positiontracker.PositionTrackerApp
import com.lab463.positiontracker.R
import com.lab463.positiontracker.core.PairingLink
import com.lab463.positiontracker.core.Times
import com.lab463.positiontracker.data.EndReason
import com.lab463.positiontracker.data.Ended
import com.lab463.positiontracker.data.Pairing
import com.lab463.positiontracker.net.ApiResult
import com.lab463.positiontracker.net.LinkView
import com.lab463.positiontracker.net.ProfileRequest
import com.lab463.positiontracker.net.UpdateInfo
import com.lab463.positiontracker.tracking.TrackerBus
import com.lab463.positiontracker.tracking.TrackingService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
fun WelcomeScreen(app: PositionTrackerApp, invalidLink: Boolean, onLink: (PairingLink) -> Unit) {
    var text by remember { mutableStateOf("") }
    var wrong by remember { mutableStateOf(invalidLink) }
    Title(stringResource(R.string.app_name))
    Panel {
        Text(stringResource(R.string.welcome_how), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        Text(stringResource(R.string.welcome_step1))
        Text(stringResource(R.string.welcome_step2))
        Text(stringResource(R.string.welcome_step3))
    }
    Panel {
        Mute(stringResource(R.string.welcome_paste))
        OutlinedTextField(
            value = text, onValueChange = { text = it; wrong = false },
            modifier = Modifier.fillMaxWidth(), singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            placeholder = { Text("https://www.lab463.com/tracker/app/p?token=…") },
            isError = wrong,
        )
        if (wrong) Text(stringResource(R.string.welcome_invalid), color = LocalSignals.current.red)
        PrimaryButton(stringResource(R.string.welcome_continue), enabled = text.isNotBlank()) {
            val link = app.parseLink(text)
            if (link == null) wrong = true else onLink(link)
        }
    }
    Mute(stringResource(R.string.welcome_iphone))
}

/** The invitation: event, consent (same text as the web page), then confirm + consent on the server and store the pairing. */
@Composable
fun PairScreen(app: PositionTrackerApp, link: PairingLink, current: Pairing?, onDone: () -> Unit, onCancel: () -> Unit) {
    val api = remember(link) { app.api(link.baseUrl) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    var view by remember { mutableStateOf<LinkView?>(null) }
    var error by remember { mutableStateOf<ApiResult<*>?>(null) }
    var busy by remember { mutableStateOf(false) }
    var consent by remember { mutableStateOf(false) }
    var showName by remember { mutableStateOf(false) }
    var nickname by remember { mutableStateOf("") }
    var renewed by remember { mutableStateOf(false) }

    LaunchedEffect(link) {
        when (val r = api.link(link.linkToken)) {
            is ApiResult.Ok -> { view = r.value; nickname = r.value.me.nickname.orEmpty() }
            else -> error = r
        }
    }

    Title(stringResource(R.string.pair_title))
    val v = view
    if (v == null) {
        if (error == null) Row(verticalAlignment = Alignment.CenterVertically) {
            CircularProgressIndicator(); Spacer(Modifier.width(12.dp)); Text(stringResource(R.string.loading))
        } else {
            val e = error as? ApiResult.Http
            Panel {
                Text(stringResource(R.string.pair_link_problem), fontWeight = FontWeight.SemiBold)
                Text(errorText(error!!), color = LocalSignals.current.red)
                if (e != null && (e.code == "link_expired" || e.code == "link_superseded") && !renewed) {
                    PrimaryButton(stringResource(R.string.pair_renew)) {
                        scope.launch { if (api.renew(link.linkToken) is ApiResult.Ok) renewed = true }
                    }
                }
                if (renewed) Text(stringResource(R.string.pair_renewed), color = LocalSignals.current.green)
            }
            OutlinedButton(onClick = onCancel, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.back)) }
        }
        return
    }

    Panel {
        Text(v.event.name, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
        Mute(listOf(v.event.location, Times.local(v.event.startTime)).filter { it.isNotBlank() }.joinToString(" · "))
        Text("${v.me.name} · #${v.me.bib}" + (v.me.category?.let { " · $it" } ?: ""))
    }
    if (v.event.finished) {
        Panel { Text(stringResource(R.string.pair_event_finished)) }
        OutlinedButton(onClick = onCancel, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.back)) }
        return
    }
    if (current != null && current.eventId != v.event.id) Panel {
        Text(stringResource(R.string.pair_replaces, current.eventName), color = LocalSignals.current.yellow)
    }
    if (v.me.phoneSetUp) Panel { Mute(stringResource(R.string.pair_phone_set_up)) }
    if (v.me.status != "Confirmed") Panel {
        Mute(stringResource(R.string.pair_nickname))
        OutlinedTextField(value = nickname, onValueChange = { if (it.length <= 30) nickname = it },
            singleLine = true, modifier = Modifier.fillMaxWidth())
    }
    Panel {
        Text(stringResource(R.string.consent_title), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        Mute("• " + stringResource(R.string.consent_organizer))
        Mute("• " + stringResource(R.string.consent_spectators))
        Mute("• " + stringResource(R.string.consent_retention))
        Mute("• " + stringResource(R.string.consent_app_stops))
        TextButton(onClick = { openUrl(context, link.baseUrl + "/privacy") }) { Text(stringResource(R.string.consent_privacy)) }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Checkbox(checked = consent, onCheckedChange = { consent = it })
            Text(stringResource(R.string.consent_location))
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Checkbox(checked = showName, onCheckedChange = { showName = it })
            Text(stringResource(R.string.consent_show_name))
        }
        error?.let { Text(errorText(it), color = LocalSignals.current.red) }
        PrimaryButton(stringResource(R.string.consent_agree), enabled = consent && !busy, color = LocalSignals.current.green) {
            busy = true
            error = null
            scope.launch {
                if (v.me.status != "Confirmed") {
                    val c = api.confirm(link.linkToken, ProfileRequest(nickname = nickname.trim().ifEmpty { null }))
                    if (c !is ApiResult.Ok) { error = c; busy = false; return@launch }
                }
                when (val r = api.consent(link.linkToken, showName)) {
                    is ApiResult.Ok -> {
                        // a pairing replaces the previous one: stop its tracking and drop its unsent fixes
                        if (current != null) TrackingService.stop(context)
                        withContext(Dispatchers.IO) { app.queue.clear() }
                        TrackerBus.reset()
                        app.store.save(Pairing(link.baseUrl, r.value.eventId, r.value.deviceToken, v.event.name, v.me.name, v.me.bib))
                        onDone()
                    }
                    else -> error = r
                }
                busy = false
            }
        }
    }
    OutlinedButton(onClick = onCancel, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.cancel)) }
}

@Composable
fun EndedScreen(ended: Ended, onOk: () -> Unit) {
    Title(stringResource(if (ended.reason == EndReason.Finished) R.string.ended_finished_title else R.string.ended_revoked_title))
    Panel {
        Text(
            if (ended.reason == EndReason.Finished) stringResource(R.string.ended_finished_text, ended.eventName)
            else stringResource(R.string.ended_revoked_text)
        )
    }
    PrimaryButton(stringResource(R.string.ok), onClick = onOk)
}

@Composable
fun UpdateBanner(info: UpdateInfo) {
    val context = LocalContext.current
    Panel {
        Text(stringResource(R.string.update_available, info.versionName), fontWeight = FontWeight.SemiBold)
        Text(stringResource(R.string.update_hint), color = MaterialTheme.colorScheme.onSurfaceVariant)
        // the APK itself: the browser downloads it at once (older manifests have only the download page)
        OutlinedButton(onClick = { openUrl(context, info.apk ?: info.url) }) { Text(stringResource(R.string.update_download)) }
    }
}
