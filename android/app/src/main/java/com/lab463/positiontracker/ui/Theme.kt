package com.lab463.positiontracker.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color

/** The signal colours of the web app (styles.css), per theme. */
@Immutable
data class Signals(val green: Color, val yellow: Color, val red: Color, val blue: Color, val mute: Color, val onSignal: Color)

private val DarkSignals = Signals(Color(0xFF25E07B), Color(0xFFFFD60A), Color(0xFFFF1E3C), Color(0xFF3DA5FF), Color(0xFF8D93A6), Color(0xFF03170B))
private val LightSignals = Signals(Color(0xFF16B364), Color(0xFFF2B705), Color(0xFFE10B2D), Color(0xFF1E88E5), Color(0xFF5B6275), Color(0xFFFFFFFF))

val LocalSignals = staticCompositionLocalOf { DarkSignals }

private val Dark = darkColorScheme(
    primary = Color(0xFFFF1E3C), onPrimary = Color.White,
    secondary = Color(0xFF25E07B), onSecondary = Color(0xFF03170B),
    background = Color(0xFF0C0D11), onBackground = Color(0xFFF4F5F8),
    surface = Color(0xFF15171E), onSurface = Color(0xFFF4F5F8),
    surfaceVariant = Color(0xFF1C1F29), onSurfaceVariant = Color(0xFF8D93A6),
    outline = Color(0xFF2A2E3A), error = Color(0xFFFF1E3C),
)

private val Light = lightColorScheme(
    primary = Color(0xFFE10B2D), onPrimary = Color.White,
    secondary = Color(0xFF16B364), onSecondary = Color.White,
    background = Color(0xFFEAEDF2), onBackground = Color(0xFF12141A),
    surface = Color(0xFFFFFFFF), onSurface = Color(0xFF12141A),
    surfaceVariant = Color(0xFFF4F6F9), onSurfaceVariant = Color(0xFF5B6275),
    outline = Color(0xFFD3D8E1), error = Color(0xFFE10B2D),
)

@Composable
fun PositionTrackerTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    androidx.compose.runtime.CompositionLocalProvider(LocalSignals provides if (dark) DarkSignals else LightSignals) {
        MaterialTheme(colorScheme = if (dark) Dark else Light, content = content)
    }
}
