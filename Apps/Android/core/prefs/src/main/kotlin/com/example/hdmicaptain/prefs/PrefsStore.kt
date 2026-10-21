package com.example.hdmicaptain.prefs

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.example.hdmicaptain.model.DevicePreset
import com.example.hdmicaptain.model.GuideSettings
import com.example.hdmicaptain.model.Profile
import kotlinx.serialization.json.Json
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

private val Context.store by preferencesDataStore(name = "settings")

/** App-wide preferences (the Android ViewerPrefs). */
data class AppPrefs(
    val showDiagnostics: Boolean = false,
    val lastCardKey: String? = null,
    /** Pro Save Recent: keep the last [recentMinutes] on disk. */
    val recentEnabled: Boolean = false,
    val recentMinutes: Int = 15,
    val guides: GuideSettings = GuideSettings(),
)

/**
 * Settings that survive restarts and replugs (spec §12.2): app preferences plus one DevicePreset
 * per card, keyed by the card's stable key (DeviceKey) like CaptureCore's PresetStore.
 */
class PrefsStore(context: Context) {
    private val store = context.applicationContext.store

    val app: Flow<AppPrefs> = store.data.map { p ->
        AppPrefs(
            showDiagnostics = p[SHOW_DIAGNOSTICS] ?: false,
            lastCardKey = p[LAST_CARD],
            recentEnabled = p[RECENT_ENABLED] ?: false,
            recentMinutes = p[RECENT_MINUTES] ?: 15,
            guides = p[GUIDES]?.let { runCatching { Json.decodeFromString(GuideSettings.serializer(), it) }.getOrNull() } ?: GuideSettings(),
        )
    }

    suspend fun appPrefs(): AppPrefs = app.first()

    suspend fun setShowDiagnostics(on: Boolean) {
        store.edit { it[SHOW_DIAGNOSTICS] = on }
    }

    suspend fun setRecent(enabled: Boolean, minutes: Int) {
        store.edit {
            it[RECENT_ENABLED] = enabled
            it[RECENT_MINUTES] = minutes
        }
    }

    suspend fun setGuides(guides: GuideSettings) {
        store.edit { it[GUIDES] = Json.encodeToString(GuideSettings.serializer(), guides) }
    }

    /** User profiles (built-ins come from Profile.builtIns). */
    val userProfiles: Flow<List<Profile>> = store.data.map { Profile.decode(it[PROFILES]) }

    suspend fun setUserProfiles(profiles: List<Profile>) {
        store.edit { it[PROFILES] = Profile.encode(profiles) }
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
        val GUIDES = stringPreferencesKey("guides")
        val PROFILES = stringPreferencesKey("userProfiles")
        val RECENT_ENABLED = booleanPreferencesKey("recentEnabled")
        val RECENT_MINUTES = androidx.datastore.preferences.core.intPreferencesKey("recentMinutes")
    }
}
