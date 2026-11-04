package com.lab463.positiontracker.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PairingLinkTest {
    private val base = "https://www.lab463.com/tracker/app"

    private fun parse(text: String, any: Boolean = false) = PairingLink.parse(text, base, any)

    @Test
    fun `the email link gives the server and the token`() {
        assertEquals(PairingLink(base, "AbC-123_x"), parse("https://www.lab463.com/tracker/app/p?token=AbC-123_x"))
    }

    @Test
    fun `a pasted text around the link and a different host case still work`() {
        assertEquals(PairingLink(base, "tok"), parse("Váš odkaz: <https://WWW.lab463.com/tracker/app/p?token=tok> ďakujeme"))
    }

    @Test
    fun `the app link uses the default server`() {
        assertEquals(PairingLink(base, "tok"), parse("positiontracker://p?token=tok"))
    }

    @Test
    fun `another server is refused in release and accepted in debug`() {
        assertNull(parse("https://evil.example.org/tracker/app/p?token=tok"))
        assertNull(parse("positiontracker://p?token=tok&base=https%3A%2F%2Fevil.example.org"))
        assertEquals(PairingLink("http://10.0.2.2:5080", "tok"), parse("http://10.0.2.2:5080/p?token=tok", any = true))
        assertEquals(PairingLink("http://10.0.2.2:5080", "tok"),
            parse("positiontracker://p?token=tok&base=http%3A%2F%2F10.0.2.2%3A5080", any = true))
    }

    @Test
    fun `links without a token or to another page are not invitations`() {
        assertNull(parse("https://www.lab463.com/tracker/app/p"))
        assertNull(parse("https://www.lab463.com/tracker/app/p?token="))
        assertNull(parse("https://www.lab463.com/tracker/app/privacy?token=tok"))
        assertNull(parse("https://www.lab463.com/tracker/app/e/123?token=tok"))
        assertNull(parse("positiontracker://x?token=tok"))
        assertNull(parse("ftp://www.lab463.com/tracker/app/p?token=tok"))
        assertNull(parse("nonsense"))
    }
}
