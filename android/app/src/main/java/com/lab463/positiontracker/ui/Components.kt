package com.lab463.positiontracker.ui

import android.content.Context
import android.content.Intent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.net.toUri
import com.lab463.positiontracker.R
import com.lab463.positiontracker.net.ApiResult
import java.util.Locale

@Composable
fun Panel(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(14.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp), content = content)
    }
}

@Composable
fun Title(text: String) = Text(text, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)

@Composable
fun Mute(text: String) = Text(text, style = MaterialTheme.typography.bodyMedium, color = LocalSignals.current.mute)

@Composable
fun Dot(color: Color) = Box(Modifier.size(12.dp).background(color, CircleShape))

/** A checklist line: green or red dot, the text, and a button to fix it when it is not OK. */
@Composable
fun CheckRow(ok: Boolean, text: String, action: String? = null, onAction: () -> Unit = {}) {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
        Dot(if (ok) LocalSignals.current.green else LocalSignals.current.red)
        Box(Modifier.width(10.dp))
        Text(text, modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
        if (!ok && action != null) OutlinedButton(onClick = onAction) { Text(action) }
    }
}

@Composable
fun KeyValue(key: String, value: String) {
    Row(Modifier.fillMaxWidth()) {
        Text(key, modifier = Modifier.weight(1f), color = LocalSignals.current.mute, style = MaterialTheme.typography.bodyMedium)
        Text(value, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
fun PrimaryButton(text: String, enabled: Boolean = true, color: Color? = null, onClick: () -> Unit) {
    Button(
        onClick = onClick, enabled = enabled,
        modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
        colors = if (color == null) androidx.compose.material3.ButtonDefaults.buttonColors()
        else androidx.compose.material3.ButtonDefaults.buttonColors(containerColor = color, contentColor = LocalSignals.current.onSignal),
    ) { Text(text, modifier = Modifier.padding(vertical = 8.dp), style = MaterialTheme.typography.titleMedium) }
}

/** The server's message in the phone's language, else a generic text for the error kind. */
@Composable
fun errorText(error: ApiResult<*>): String {
    val sk = Locale.getDefault().language == "sk"
    return when (error) {
        is ApiResult.Network -> stringResource(R.string.error_network)
        is ApiResult.Http -> (if (sk) error.messageSk else error.messageEn)
            ?: stringResource(R.string.error_http, error.status)
        is ApiResult.Ok -> ""
    }
}

fun openUrl(context: Context, url: String) {
    try {
        context.startActivity(Intent(Intent.ACTION_VIEW, url.toUri()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    } catch (e: Exception) {
        // no browser: nothing to do
    }
}
