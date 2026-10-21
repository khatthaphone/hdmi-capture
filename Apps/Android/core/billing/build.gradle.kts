plugins {
    alias(libs.plugins.android.library)
}

android {
    namespace = "com.example.hdmicaptain.billing"
    compileSdk = libs.versions.compileSdk.get().toInt()
    defaultConfig { minSdk = libs.versions.minSdk.get().toInt() }
}

kotlin { jvmToolchain(21) }

dependencies {
    api(project(":core:model"))
}
