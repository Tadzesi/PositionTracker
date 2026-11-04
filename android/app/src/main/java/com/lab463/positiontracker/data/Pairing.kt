package com.lab463.positiontracker.data

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.core.content.edit
import com.lab463.positiontracker.net.ApiClient
import com.lab463.positiontracker.net.DeviceSession
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** The phone's pairing with one participant of one event. The device token is the only credential. */
data class Pairing(
    val baseUrl: String,
    val eventId: String,
    val deviceToken: String,
    val eventName: String,
    val participantName: String,
    val bib: String,
)

/**
 * What the app keeps between runs (private SharedPreferences): the pairing (the device token encrypted with an AES key of
 * the Android Keystore that never leaves it), whether tracking should run, the last device session, and the name of the
 * event whose finish ended the last pairing (for the "finished" screen). The invitation link token is not kept.
 */
class PairingStore(context: Context) {
    private val prefs: SharedPreferences = context.getSharedPreferences("pairing", Context.MODE_PRIVATE)

    fun load(): Pairing? {
        val base = prefs.getString(K_BASE, null) ?: return null
        val eventId = prefs.getString(K_EVENT, null) ?: return null
        val token = prefs.getString(K_TOKEN, null)?.let { TokenCipher.decrypt(it) } ?: return null
        return Pairing(base, eventId, token, prefs.getString(K_EVENT_NAME, "").orEmpty(),
            prefs.getString(K_NAME, "").orEmpty(), prefs.getString(K_BIB, "").orEmpty())
    }

    /** Whether a pairing is stored (without decrypting the token). */
    fun isPaired(): Boolean = prefs.contains(K_TOKEN)

    fun save(p: Pairing) = prefs.edit(commit = true) {
        putString(K_BASE, p.baseUrl)
        putString(K_EVENT, p.eventId)
        putString(K_TOKEN, TokenCipher.encrypt(p.deviceToken))
        putString(K_EVENT_NAME, p.eventName)
        putString(K_NAME, p.participantName)
        putString(K_BIB, p.bib)
        remove(K_SESSION)
        remove(K_FINISHED)
        putBoolean(K_WANTED, false)
    }

    /** Forgets the pairing; [ended] (why and which event) is shown once on the "ended" screen. */
    fun clear(ended: Ended? = null) = prefs.edit(commit = true) {
        listOf(K_BASE, K_EVENT, K_TOKEN, K_EVENT_NAME, K_NAME, K_BIB, K_SESSION).forEach { remove(it) }
        putBoolean(K_WANTED, false)
        if (ended != null) {
            putString(K_FINISHED, ended.eventName)
            putString(K_REASON, ended.reason.name)
        } else {
            remove(K_FINISHED)
            remove(K_REASON)
        }
    }

    var trackingWanted: Boolean
        get() = prefs.getBoolean(K_WANTED, false)
        set(value) = prefs.edit(commit = true) { putBoolean(K_WANTED, value) }

    var lastSession: DeviceSession?
        get() = prefs.getString(K_SESSION, null)?.let {
            try { ApiClient.json.decodeFromString(DeviceSession.serializer(), it) } catch (e: Exception) { null }
        }
        set(value) = prefs.edit {
            if (value == null) remove(K_SESSION) else putString(K_SESSION, ApiClient.json.encodeToString(DeviceSession.serializer(), value))
        }

    /** Why and for which event the last pairing ended, until the user dismisses it. */
    val ended: Ended?
        get() {
            val name = prefs.getString(K_FINISHED, null) ?: return null
            val reason = prefs.getString(K_REASON, null)?.let { r -> EndReason.entries.firstOrNull { it.name == r } }
            return Ended(name, reason ?: EndReason.Finished)
        }

    fun dismissEnded() = prefs.edit { remove(K_FINISHED); remove(K_REASON) }

    private companion object {
        const val K_BASE = "baseUrl"
        const val K_EVENT = "eventId"
        const val K_TOKEN = "deviceToken"
        const val K_EVENT_NAME = "eventName"
        const val K_NAME = "participantName"
        const val K_BIB = "bib"
        const val K_SESSION = "session"
        const val K_FINISHED = "finishedEvent"
        const val K_WANTED = "trackingWanted"
        const val K_REASON = "endReason"
    }
}

/** Why a pairing ended: the event finished, or the token stopped working (set up on another phone / removed). */
enum class EndReason { Finished, Revoked }

data class Ended(val eventName: String, val reason: EndReason)

/** AES-GCM with a non-exportable key of the Android Keystore; the stored form is base64(iv + ciphertext). */
internal object TokenCipher {
    private const val ALIAS = "pt_device_token"
    private const val TRANSFORMATION = "AES/GCM/NoPadding"

    fun encrypt(plain: String): String {
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, key()) }
        val out = cipher.iv + cipher.doFinal(plain.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(out, Base64.NO_WRAP)
    }

    fun decrypt(stored: String): String? = try {
        val bytes = Base64.decode(stored, Base64.NO_WRAP)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes, 0, 12))
        String(cipher.doFinal(bytes, 12, bytes.size - 12), Charsets.UTF_8)
    } catch (e: Exception) {
        null // the key was lost (e.g. the app data was restored to another phone): pair again
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return generator.generateKey()
    }
}
