// Settings persistence: app preferences and per-card presets in DataStore.
plugins {
    alias(libs.plugins.android.library)
}

android {
    namespace = "com.example.hdmicaptain.prefs"
    compileSdk = libs.versions.compileSdk.get().toInt()
    defaultConfig { minSdk = libs.versions.minSdk.get().toInt() }
}

kotlin { jvmToolchain(21) }

dependencies {
    api(project(":core:model"))
    implementation(libs.androidx.datastore.preferences)
}
