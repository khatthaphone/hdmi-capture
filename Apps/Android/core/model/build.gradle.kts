// Pure Kotlin (JVM): format choice, capture state + recovery, signal analysis, presets.
// No Android dependencies, so it tests fast and mirrors CaptureCore's platform-free code.
plugins {
    alias(libs.plugins.kotlin.jvm)
}

kotlin {
    jvmToolchain(21)
}

dependencies {
    testImplementation(libs.junit)
}
