package com.lab463.positiontracker.core

import java.net.URI
import java.net.URLDecoder

/** A participant's invitation: the server (app root) and the personal link token. */
data class PairingLink(val baseUrl: String, val linkToken: String) {
    companion object {
        /**
         * Reads an invitation from what the user opened or pasted:
         * - the email link `https://www.lab463.com/tracker/app/p?token=…` (the server is the part before `/p`),
         * - the app link of the participant page `positiontracker://p?token=…[&base=…]` (default server unless `base`).
         *
         * The server must be [defaultBaseUrl] unless [allowAnyServer] (debug builds), so a crafted link cannot send the
         * phone's positions elsewhere. Null when the text holds no valid invitation.
         */
        fun parse(text: String, defaultBaseUrl: String, allowAnyServer: Boolean): PairingLink? {
            val candidate = text.trim().split(Regex("\\s+")).firstOrNull { it.contains("token=") } ?: return null
            val uri = try { URI(candidate.trim('<', '>', '"', '\'')) } catch (e: Exception) { return null }
            val query = parseQuery(uri.rawQuery ?: return null)
            val token = query["token"]?.takeIf { it.isNotBlank() && it.length <= 512 } ?: return null
            val base = when (uri.scheme?.lowercase()) {
                "positiontracker" -> {
                    if (uri.host != "p") return null
                    query["base"]?.trimEnd('/') ?: defaultBaseUrl
                }
                "https", "http" -> {
                    val path = uri.rawPath ?: return null
                    if (!path.endsWith("/p")) return null
                    val port = if (uri.port == -1) "" else ":${uri.port}"
                    "${uri.scheme.lowercase()}://${uri.host?.lowercase() ?: return null}$port${path.removeSuffix("/p")}"
                }
                else -> return null
            }
            if (!isAllowed(base, defaultBaseUrl, allowAnyServer)) return null
            return PairingLink(base.trimEnd('/'), token)
        }

        private fun isAllowed(base: String, defaultBaseUrl: String, allowAnyServer: Boolean): Boolean {
            if (base.trimEnd('/').equals(defaultBaseUrl.trimEnd('/'), ignoreCase = true)) return true
            if (!allowAnyServer) return false
            return base.startsWith("https://", ignoreCase = true) || base.startsWith("http://", ignoreCase = true)
        }

        private fun parseQuery(raw: String): Map<String, String> =
            raw.split('&').mapNotNull { pair ->
                val i = pair.indexOf('=')
                if (i <= 0) null else decode(pair.substring(0, i)) to decode(pair.substring(i + 1))
            }.toMap()

        private fun decode(s: String): String = URLDecoder.decode(s, "UTF-8")
    }
}
