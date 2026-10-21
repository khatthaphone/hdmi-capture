package com.example.hdmicaptain.prefs

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.example.hdmicaptain.model.DevicePreset
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

private val Context.store by preferencesDataStore(name = "settings")

/** App-wide preferences (the Android ViewerPrefs). */
data class AppPrefs(
    val showDiagnostics: Boolean = false,
    val lastCardKey: String? = null,
)

/**
 * Settings that survive restarts and replugs (spec §12.2): app preferences plus one DevicePreset
 * per card, keyed by the card's stable key (DeviceKey) like CaptureCore's PresetStore.
 */
class PrefsStore(context: Context) {
    private val store = context.applicationContext.store

    val app: Flow<AppPrefs> = store.data.map { p ->
        AppPrefs(showDiagnostics = p[SHOW_DIAGNOSTICS] ?: false, lastCardKey = p[LAST_CARD])
    }

    suspend fun appPrefs(): AppPrefs = app.first()

    suspend fun setShowDiagnostics(on: Boolean) {
        store.edit { it[SHOW_DIAGNOSTICS] = on }
    }

    suspend fun setLastCard(key: String) {
        store.edit { it[LAST_CARD] = key }
    }

    suspend fun preset(cardKey: String): DevicePreset = DevicePreset.decode(store.data.first()[presetKey(cardKey)])

    suspend fun save(cardKey: String, preset: DevicePreset) {
        store.edit { it[presetKey(cardKey)] = preset.encode() }
    }

    private fun presetKey(cardKey: String) = stringPreferencesKey("preset:$cardKey")

    private companion object {
        val SHOW_DIAGNOSTICS = booleanPreferencesKey("showDiagnostics")
        val LAST_CARD = stringPreferencesKey("lastCardKey")
    }
}
