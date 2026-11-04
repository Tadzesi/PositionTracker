package com.lab463.positiontracker.tracking

import android.annotation.SuppressLint
import android.content.Context
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.os.Looper
import com.google.android.gms.common.ConnectionResult
import com.google.android.gms.common.GoogleApiAvailability
import com.google.android.gms.location.LocationCallback
import com.google.android.gms.location.LocationRequest
import com.google.android.gms.location.LocationResult
import com.google.android.gms.location.LocationServices
import com.google.android.gms.location.Priority

/**
 * Location updates every [intervalMs], also when standing still (the organizer must see a rider who stopped). The fused
 * provider of Google Play services where available, otherwise the platform GPS provider (phones without Google services).
 */
interface LocationSource {
    fun start(intervalMs: Long, onFix: (Location) -> Unit)
    fun stop()

    companion object {
        fun create(context: Context): LocationSource =
            if (GoogleApiAvailability.getInstance().isGooglePlayServicesAvailable(context) == ConnectionResult.SUCCESS)
                FusedSource(context) else PlatformSource(context)
    }
}

@SuppressLint("MissingPermission") // the service checks the permission before it starts the source
private class FusedSource(context: Context) : LocationSource {
    private val client = LocationServices.getFusedLocationProviderClient(context)
    private var callback: LocationCallback? = null

    override fun start(intervalMs: Long, onFix: (Location) -> Unit) {
        stop()
        val request = LocationRequest.Builder(Priority.PRIORITY_HIGH_ACCURACY, intervalMs)
            .setMinUpdateIntervalMillis(intervalMs / 2)
            .setMinUpdateDistanceMeters(0f)
            .setWaitForAccurateLocation(false)
            .build()
        val cb = object : LocationCallback() {
            override fun onLocationResult(result: LocationResult) = result.locations.forEach(onFix)
        }
        callback = cb
        client.requestLocationUpdates(request, cb, Looper.getMainLooper())
    }

    override fun stop() {
        callback?.let { client.removeLocationUpdates(it) }
        callback = null
    }
}

@SuppressLint("MissingPermission")
private class PlatformSource(context: Context) : LocationSource {
    private val manager = context.getSystemService(LocationManager::class.java)
    private var listener: LocationListener? = null

    override fun start(intervalMs: Long, onFix: (Location) -> Unit) {
        stop()
        val l = LocationListener { onFix(it) }
        listener = l
        manager.requestLocationUpdates(LocationManager.GPS_PROVIDER, intervalMs, 0f, l, Looper.getMainLooper())
    }

    override fun stop() {
        listener?.let { manager.removeUpdates(it) }
        listener = null
    }
}
