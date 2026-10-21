// Recording and Save Recent (Pro): MediaCodec encoders fed by the renderer and the audio tap, MediaMuxer MP4.
plugins {
    alias(libs.plugins.android.library)
}

android {
    namespace = "com.example.hdmicaptain.record"
    compileSdk = libs.versions.compileSdk.get().toInt()
    defaultConfig { minSdk = libs.versions.minSdk.get().toInt() }
}

kotlin { jvmToolchain(21) }

dependencies {
    api(project(":core:model"))
    testImplementation(libs.junit)
}
