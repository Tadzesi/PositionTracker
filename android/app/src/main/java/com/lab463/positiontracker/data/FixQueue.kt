package com.lab463.positiontracker.data

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper

/** One location fix waiting to be sent. [time] is epoch milliseconds (UTC). */
data class QueuedFix(val id: Long, val time: Long, val lat: Double, val lon: Double, val accuracy: Double?)

/**
 * The offline queue of fixes (SQLite, survives a restart of the app or the phone). Fixes are sent oldest first and deleted
 * once the server took them; at most [MAX_ROWS] are kept (about 28 hours at one fix every 5 s), the oldest go first.
 */
class FixQueue(context: Context) : SQLiteOpenHelper(context, "fixes.db", null, 1) {

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE fix (id INTEGER PRIMARY KEY AUTOINCREMENT, t INTEGER NOT NULL, lat REAL NOT NULL, lon REAL NOT NULL, acc REAL)")
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) = Unit

    @Synchronized
    fun add(time: Long, lat: Double, lon: Double, accuracy: Double?) {
        val db = writableDatabase
        db.insert("fix", null, ContentValues().apply {
            put("t", time); put("lat", lat); put("lon", lon)
            if (accuracy != null) put("acc", accuracy) else putNull("acc")
        })
        db.execSQL("DELETE FROM fix WHERE id <= (SELECT MAX(id) FROM fix) - $MAX_ROWS")
    }

    @Synchronized
    fun oldest(limit: Int): List<QueuedFix> =
        readableDatabase.rawQuery("SELECT id, t, lat, lon, acc FROM fix ORDER BY id LIMIT $limit", null).use { c ->
            buildList {
                while (c.moveToNext()) add(QueuedFix(c.getLong(0), c.getLong(1), c.getDouble(2), c.getDouble(3),
                    if (c.isNull(4)) null else c.getDouble(4)))
            }
        }

    /** Deletes the fixes up to and including [id] (the batch that was sent or dropped). */
    @Synchronized
    fun deleteThrough(id: Long) {
        writableDatabase.delete("fix", "id <= ?", arrayOf(id.toString()))
    }

    @Synchronized
    fun count(): Int =
        readableDatabase.rawQuery("SELECT COUNT(*) FROM fix", null).use { c -> if (c.moveToFirst()) c.getInt(0) else 0 }

    @Synchronized
    fun clear() {
        writableDatabase.delete("fix", null, null)
    }

    companion object {
        const val MAX_ROWS = 20_000
    }
}
