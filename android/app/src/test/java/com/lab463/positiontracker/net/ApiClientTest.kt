package com.lab463.positiontracker.net

import kotlinx.coroutines.test.runTest
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class ApiClientTest {
    private val server = MockWebServer()
    private lateinit var api: ApiClient

    @Before
    fun setUp() {
        server.start()
        api = ApiClient(server.url("/tracker/app").toString(), "PositionTracker-Android/test")
    }

    @After
    fun tearDown() = server.shutdown()

    @Test
    fun `positions go to the event with the bearer token and the strict body`() = runTest {
        server.enqueue(MockResponse().setResponseCode(202).setBody("""{"accepted":2,"rejected":0}"""))
        val batch = PositionBatch(
            listOf(FixDto("2026-11-07T10:00:05.000Z", 49.1, 18.7, 5.0), FixDto("2026-11-07T10:00:10.000Z", 49.2, 18.8, null)),
            StatusDto(80.0, false, 2, true),
        )
        val result = api.positions("dev-token", "e1", batch)

        assertEquals(ApiResult.Ok(IngestResult(2, 0)), result)
        val request = server.takeRequest()
        assertEquals("/tracker/app/api/events/e1/positions", request.path)
        assertEquals("Bearer dev-token", request.getHeader("Authorization"))
        assertNull(request.getHeader("X-Requested-With"))
        assertEquals(
            """{"fixes":[{"t":"2026-11-07T10:00:05.000Z","lat":49.1,"lon":18.7,"acc":5.0},{"t":"2026-11-07T10:00:10.000Z","lat":49.2,"lon":18.8}],"status":{"battery":80.0,"charging":false,"queue":2,"foreground":true}}""",
            request.body.readUtf8(),
        )
    }

    @Test
    fun `the API error code, both messages and Retry-After are read`() = runTest {
        server.enqueue(MockResponse().setResponseCode(409).setHeader("Retry-After", "7")
            .setBody("""{"code":"event_finished","message":{"sk":"Skončilo.","en":"Finished."}}"""))
        val result = api.positions("t", "e1", PositionBatch(emptyList(), StatusDto(null, null, 0, true)))
        assertEquals(ApiResult.Http(409, "event_finished", "Skončilo.", "Finished.", 7), result)
    }

    @Test
    fun `consent is a browser route call with the CSRF header and returns the device token`() = runTest {
        server.enqueue(MockResponse().setBody("""{"deviceToken":"dt","eventId":"e1"}"""))
        val result = api.consent("link/with+odd", showName = true)

        assertEquals(ApiResult.Ok(ConsentAnswer("dt", "e1")), result)
        val request = server.takeRequest()
        assertEquals("/tracker/app/api/p/link%2Fwith+odd/consent", request.path)
        assertEquals("PositionTracker", request.getHeader("X-Requested-With"))
        assertTrue(request.getHeader("Content-Type")!!.startsWith("application/json"))
        assertEquals("""{"locationConsent":true,"showNameToSpectators":true}""", request.body.readUtf8())
    }

    @Test
    fun `the session ignores unknown fields and reads the light`() = runTest {
        server.enqueue(MockResponse().setBody("""
            {"eventId":"e1","event":{"name":"Časovka","location":"Žilina","sport":"cycling","state":"live","startTime":"2026-11-07T10:00:00Z","extra":1},
             "me":{"name":"Rider","bib":"7","nickname":null,"startTime":"2026-11-07T10:20:00Z"},
             "tuning":{"intervalSeconds":5,"displacementM":10,"ignoreInaccurateM":50},
             "link":{"level":"good","lastBatchAgoSeconds":3,"problems":["lowBattery"],"earlyOnly":false},
             "serverTime":"2026-11-07T10:21:00Z"}
            """.trimIndent()))
        val result = api.session("t") as ApiResult.Ok

        assertEquals("live", result.value.event.state)
        assertEquals("2026-11-07T10:20:00Z", result.value.me.startTime)
        assertEquals(5, result.value.tuning.intervalSeconds)
        assertEquals(listOf("lowBattery"), result.value.link!!.problems)
        assertEquals("/tracker/app/api/device/session", server.takeRequest().path)
    }

    @Test
    fun `no server is a network result`() = runTest {
        server.shutdown()
        assertTrue(api.session("t") is ApiResult.Network)
    }

    @Test
    fun `the update manifest is read with and without the direct APK link`() = runTest {
        server.enqueue(MockResponse().setBody(
            """{"versionCode":3,"versionName":"1.0.2","url":"https://x/android/","apk":"https://x/android/positiontracker.apk?v=ab12cd34","certSha256":"AA"}"""))
        server.enqueue(MockResponse().setBody("""{"versionCode":2,"versionName":"1.0.1","url":"https://x/android/","certSha256":"AA"}"""))
        server.enqueue(MockResponse().setResponseCode(503))
        val url = server.url("/tracker/android/latest.json").toString()

        assertEquals(UpdateInfo(3, "1.0.2", "https://x/android/", "https://x/android/positiontracker.apk?v=ab12cd34"), ApiClient.updateInfo(url))
        assertEquals(UpdateInfo(2, "1.0.1", "https://x/android/", null), ApiClient.updateInfo(url))
        assertNull(ApiClient.updateInfo(url))
        assertEquals("no-cache", server.takeRequest().getHeader("Cache-Control"))
    }
}
