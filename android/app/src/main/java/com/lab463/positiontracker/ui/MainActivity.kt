package com.lab463.positiontracker.ui

import android.content.Intent
import android.os.Bundle
import android.os.SystemClock
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import com.lab463.positiontracker.BuildConfig
import com.lab463.positiontracker.PositionTrackerApp
import com.lab463.positiontracker.core.PairingLink
import com.lab463.positiontracker.net.ApiClient
import com.lab463.positiontracker.net.UpdateInfo
import kotlinx.coroutines.launch

/**
 * The only activity. Routes: an opened invitation (email link or `positiontracker://p?token=…`) → pairing; a pairing
 * that ended → the "ended" screen; paired → home (tracking); else → welcome.
 */
class MainActivity : ComponentActivity() {
    private val app get() = application as PositionTrackerApp
    private var pending by mutableStateOf<PairingLink?>(null)
    private var invalidLink by mutableStateOf(false)
    /** When the update manifest was last read (elapsed time); 0 = not yet or the last read failed. */
    private var lastUpdateCheck = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        if (savedInstanceState == null) handle(intent)
        setContent {
            PositionTrackerTheme {
                Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    Root()
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    private fun handle(intent: Intent?) {
        val data = intent?.dataString ?: return
        val link = app.parseLink(data)
        if (link == null) invalidLink = true else pending = link
    }

    @Composable
    private fun Root() {
        // bumped whenever the stored pairing changes, so the route is read again
        var version by remember { mutableIntStateOf(0) }
        val refresh: () -> Unit = { version++ }
        var update by remember { mutableStateOf<UpdateInfo?>(null) }
        val scope = rememberCoroutineScope()
        // every time the app comes to the front (also from the background, without a cold start), at most once an hour;
        // a failed read is retried at the next resume
        LifecycleEventEffect(Lifecycle.Event.ON_RESUME) {
            val now = SystemClock.elapsedRealtime()
            if (lastUpdateCheck == 0L || now - lastUpdateCheck >= UPDATE_CHECK_MS) {
                lastUpdateCheck = now
                scope.launch {
                    val info = ApiClient.updateInfo(BuildConfig.UPDATE_URL)
                    if (info == null) lastUpdateCheck = 0L
                    else update = info.takeIf { it.versionCode > BuildConfig.VERSION_CODE }
                }
            }
        }
        val store = app.store
        val link = pending
        Column(
            Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            update?.let { UpdateBanner(it) }
            key(version) {
            val paired = store.load()
            val ended = store.ended
            when {
                link != null -> PairScreen(app, link, current = paired, onDone = { pending = null; refresh() },
                    onCancel = { pending = null; refresh() })
                paired != null -> HomeScreen(app, paired, onChanged = refresh)
                ended != null -> EndedScreen(ended) { store.dismissEnded(); refresh() }
                else -> WelcomeScreen(app, invalidLink) { invalidLink = false; pending = it }
            }
            }
        }
    }

    private companion object {
        const val UPDATE_CHECK_MS = 60 * 60 * 1000L
    }
}
