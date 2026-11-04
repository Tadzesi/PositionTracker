package com.lab463.positiontracker.net

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.KSerializer
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.util.concurrent.TimeUnit

/** The outcome of an API call: a value, an HTTP error (with the API error code, if any) or no connection. */
sealed interface ApiResult<out T> {
    data class Ok<T>(val value: T) : ApiResult<T>

    /**
     * An HTTP error. [code] is the API error code (`event_finished`, `link_expired`, …) when the body had one;
     * [retryAfterSeconds] the `Retry-After` header.
     */
    data class Http(
        val status: Int,
        val code: String? = null,
        val messageSk: String? = null,
        val messageEn: String? = null,
        val retryAfterSeconds: Long? = null,
    ) : ApiResult<Nothing>

    data class Network(val error: IOException) : ApiResult<Nothing>
}

/**
 * The PositionTracker HTTP API. [baseUrl] is the app root (e.g. `https://www.lab463.com/tracker/app`). The participant
 * link routes (`/api/p/…`) are browser routes: unsafe calls send `X-Requested-With: PositionTracker` and JSON. The device
 * routes use the device token as a bearer token. Nothing of the tokens is logged.
 */
class ApiClient(
    baseUrl: String,
    private val userAgent: String,
    private val http: OkHttpClient = defaultHttp,
) {
    private val base = baseUrl.trimEnd('/')

    suspend fun link(linkToken: String): ApiResult<LinkView> =
        call(get(linkPath(linkToken)), LinkView.serializer())

    suspend fun confirm(linkToken: String, profile: ProfileRequest): ApiResult<JsonObject> =
        call(post(linkPath(linkToken) + "/confirm", json.encodeToString(ProfileRequest.serializer(), profile), browser = true),
            JsonObject.serializer())

    suspend fun consent(linkToken: String, showName: Boolean): ApiResult<ConsentAnswer> =
        call(post(linkPath(linkToken) + "/consent",
            json.encodeToString(ConsentRequest.serializer(), ConsentRequest(true, showName)), browser = true),
            ConsentAnswer.serializer())

    /** Emails a fresh invitation link to the stored address (for an expired or superseded link). */
    suspend fun renew(linkToken: String): ApiResult<JsonObject> =
        call(post(linkPath(linkToken) + "/renew", "{}", browser = true), JsonObject.serializer())

    suspend fun session(deviceToken: String): ApiResult<DeviceSession> =
        call(get("/api/device/session", deviceToken), DeviceSession.serializer())

    suspend fun positions(deviceToken: String, eventId: String, batch: PositionBatch): ApiResult<IngestResult> =
        call(post("/api/events/${segment(eventId)}/positions", json.encodeToString(PositionBatch.serializer(), batch),
            bearer = deviceToken), IngestResult.serializer())

    private fun linkPath(linkToken: String) = "/api/p/" + segment(linkToken)

    private fun segment(value: String): String =
        "https://x/".toHttpUrl().newBuilder().addPathSegment(value).build().encodedPath.removePrefix("/")

    private fun get(path: String, bearer: String? = null): Request =
        Request.Builder().url(base + path).get().headers(bearer).build()

    private fun post(path: String, body: String, bearer: String? = null, browser: Boolean = false): Request =
        Request.Builder().url(base + path).post(body.toRequestBody(JSON_TYPE)).headers(bearer)
            .apply { if (browser) header("X-Requested-With", "PositionTracker") }
            .build()

    private fun Request.Builder.headers(bearer: String?): Request.Builder = apply {
        header("User-Agent", userAgent)
        header("Accept", "application/json")
        if (bearer != null) header("Authorization", "Bearer $bearer")
    }

    private suspend fun <T> call(request: Request, serializer: KSerializer<T>): ApiResult<T> = withContext(Dispatchers.IO) {
        try {
            http.newCall(request).execute().use { response ->
                val text = response.body?.string().orEmpty()
                if (response.isSuccessful) {
                    try {
                        ApiResult.Ok(json.decodeFromString(serializer, text.ifBlank { "{}" }))
                    } catch (e: Exception) {
                        ApiResult.Http(response.code, "bad_response")
                    }
                } else {
                    errorOf(response.code, text, response.header("Retry-After"))
                }
            }
        } catch (e: IOException) {
            ApiResult.Network(e)
        }
    }

    companion object {
        /** The self-install update manifest ([url] is absolute); null when it cannot be read. */
        suspend fun updateInfo(url: String, http: OkHttpClient = defaultHttp): UpdateInfo? = withContext(Dispatchers.IO) {
            try {
                http.newCall(Request.Builder().url(url).header("Cache-Control", "no-cache").build()).execute().use { r ->
                    if (!r.isSuccessful) null else json.decodeFromString(UpdateInfo.serializer(), r.body?.string().orEmpty())
                }
            } catch (e: Exception) {
                null
            }
        }

        private val JSON_TYPE = "application/json; charset=utf-8".toMediaType()

        val json = Json { ignoreUnknownKeys = true; explicitNulls = false; encodeDefaults = true }

        val defaultHttp: OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(20, TimeUnit.SECONDS)
            .writeTimeout(20, TimeUnit.SECONDS)
            .callTimeout(30, TimeUnit.SECONDS)
            .build()

        /** Reads the API error shape `{"code","message":{"sk","en"}}` (or a problem detail) and `Retry-After` (seconds). */
        internal fun errorOf(status: Int, body: String, retryAfter: String?): ApiResult.Http {
            val wait = retryAfter?.trim()?.toLongOrNull()
            val obj = try { json.parseToJsonElement(body).jsonObject } catch (e: Exception) { null }
            val code = obj?.get("code")?.jsonPrimitive?.contentOrNull
            val message = try { obj?.get("message")?.jsonObject } catch (e: Exception) { null }
            return ApiResult.Http(
                status, code,
                message?.get("sk")?.jsonPrimitive?.contentOrNull,
                message?.get("en")?.jsonPrimitive?.contentOrNull ?: obj?.get("detail")?.jsonPrimitive?.contentOrNull,
                wait,
            )
        }
    }
}
