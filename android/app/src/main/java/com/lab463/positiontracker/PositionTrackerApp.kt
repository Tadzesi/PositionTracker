package com.lab463.positiontracker

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import com.lab463.positiontracker.core.PairingLink
import com.lab463.positiontracker.data.FixQueue
import com.lab463.positiontracker.data.PairingStore
import com.lab463.positiontracker.net.ApiClient

class PositionTrackerApp : Application() {
    lateinit var store: PairingStore
        private set
    lateinit var queue: FixQueue
        private set

    override fun onCreate() {
        super.onCreate()
        store = PairingStore(this)
        queue = FixQueue(this)
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_TRACKING, getString(R.string.channel_tracking), NotificationManager.IMPORTANCE_LOW)
                .apply { setShowBadge(false) }
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_EVENTS, getString(R.string.channel_events), NotificationManager.IMPORTANCE_DEFAULT)
        )
    }

    fun api(baseUrl: String) = ApiClient(baseUrl, "PositionTracker-Android/${BuildConfig.VERSION_NAME}")

    fun parseLink(text: String): PairingLink? =
        PairingLink.parse(text, BuildConfig.DEFAULT_BASE_URL, allowAnyServer = BuildConfig.DEBUG)

    companion object {
        const val CHANNEL_TRACKING = "tracking"
        const val CHANNEL_EVENTS = "events"
    }
}
